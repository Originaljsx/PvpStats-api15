using PvpStats.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PvpStats.Services.Logging;

/// <summary>
/// Bridges <see cref="IinactLogWatcher"/> output to the SQLite match_events
/// table, scoped to an active Crystalline Conflict match. Buffers events in
/// memory between StartCapture / StopCapture so the LiteDB+SQLite write paths
/// stay off the watcher thread.
///
/// Captured line types:
///   21  NetworkAbility       (single-target action effect — 8 effect entries parsed)
///   22  NetworkAOEAbility    (AOE action effect)
///   25  NetworkDeath
///   26  NetworkBuff          (status apply — Field(7) is hex param/stack count)
///   30  NetworkBuffRemove    (status removed — Field(7) is remaining param)
///   37  NetworkActionSync    (per-action status snapshot — has duration float per status)
///   38  NetworkStatusEffect  (full status snapshot — fires periodically)
///   39  NetworkUpdateHp      (HP/MP snapshot — useful to diff for actual HP loss)
///
/// For each type 21/22 line we also pre-parse the 8 effect entries: sum the
/// effect-03 (damage) values into <see cref="SqliteStorageService.MatchEventRow.Amount"/>
/// and flag any zero-damage effect-03 entries for downstream analytics.
/// </summary>
internal sealed class CcEventIngestor : IDisposable {
    private static readonly HashSet<int> CapturedTypes = new() { 21, 22, 25, 26, 30, 37, 38, 39 };

    private readonly Plugin _plugin;
    private readonly IinactLogWatcher _watcher;

    private readonly object _lock = new();
    private string? _activeMatchId;
    private DateTime _captureStartUtc;
    private long _seq;
    private List<SqliteStorageService.MatchEventRow> _buffer = new();

    public bool IsCapturing => _activeMatchId != null;
    public long EventsBufferedForCurrentMatch => _seq;

    public CcEventIngestor(Plugin plugin, IinactLogWatcher watcher) {
        _plugin = plugin;
        _watcher = watcher;
        _watcher.LineReceived += OnLine;
    }

    public void Dispose() {
        _watcher.LineReceived -= OnLine;
        // Best-effort flush of anything still buffered.
        if (_activeMatchId != null) {
            _plugin.Log.Information("[CcEventIngestor] Disposing while capturing — flushing buffer.");
            _ = FlushAsync();
        }
    }

    /// <summary>Begin capturing events for a CC match. Idempotent if called for the same id.</summary>
    public void StartCapture(string matchId) {
        if (string.IsNullOrEmpty(matchId)) return;
        lock (_lock) {
            if (_activeMatchId == matchId) return;
            if (_activeMatchId != null) {
                _plugin.Log.Warning($"[CcEventIngestor] StartCapture({matchId}) while already capturing {_activeMatchId} — discarding old buffer.");
                _buffer = new List<SqliteStorageService.MatchEventRow>();
                _seq = 0;
            }
            _activeMatchId = matchId;
            _captureStartUtc = DateTime.UtcNow;
            _seq = 0;
            _buffer = new List<SqliteStorageService.MatchEventRow>();
            _plugin.Log.Information($"[CcEventIngestor] Capture started for match {matchId} at {_captureStartUtc:O}");
        }
    }

    /// <summary>
    /// Stop capturing and flush buffered events. Returns the count flushed.
    /// </summary>
    public async Task<int> StopCaptureAndFlushAsync() {
        return await FlushAsync();
    }

    private async Task<int> FlushAsync() {
        string? matchId;
        List<SqliteStorageService.MatchEventRow> toFlush;
        lock (_lock) {
            matchId = _activeMatchId;
            toFlush = _buffer;
            _buffer = new List<SqliteStorageService.MatchEventRow>();
            _activeMatchId = null;
            _seq = 0;
        }

        if (matchId == null || toFlush.Count == 0) {
            if (matchId != null) _plugin.Log.Information($"[CcEventIngestor] Stopped capture for {matchId} — 0 events.");
            return 0;
        }

        try {
            await _plugin.SqliteStorage.RecordMatchEventsAsync(matchId, toFlush);
            _plugin.Log.Information($"[CcEventIngestor] Flushed {toFlush.Count} events for match {matchId}.");
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"[CcEventIngestor] Flush failed for match {matchId}.");
        }
        return toFlush.Count;
    }

    private void OnLine(ActLogLine line) {
        if (!CapturedTypes.Contains(line.Type)) return;

        // Read state under lock; bail fast if not actively capturing.
        string? matchId;
        long seq;
        lock (_lock) {
            if (_activeMatchId == null) return;
            matchId = _activeMatchId;
            seq = _seq++;
        }

        var row = new SqliteStorageService.MatchEventRow {
            Seq = seq,
            TimestampUtcIso = ToIsoUtc(line.Timestamp),
            EventType = MapEventType(line.Type),
            FlagsJson = line.Raw,
        };

        // Field positions per OverlayPlugin/cactbot LogGuide. ActLogLine.Field(N)
        // returns the Nth field after type+timestamp. Verified against real CC
        // match logs at C:\Users\Jacob\ccstats\Network_30109_20260508.log.
        switch (line.Type) {
            case 21: case 22:
                // NetworkAbility / NetworkAOEAbility
                // Field(0)=casterId, Field(1)=casterName, Field(2)=abilityIdHex,
                // Field(3)=abilityName, Field(4)=targetId, Field(5)=targetName.
                // Effect entries are 8 (flags, value) pairs at Fields(6..21).
                row.ActorName = NullIfEmpty(line.Field(1));
                row.TargetName = NullIfEmpty(line.Field(5));
                row.AbilityId = (int)line.FieldHex(2);
                row.AbilityName = NullIfEmpty(line.Field(3));
                ParseAbilityEffects(line, row);
                break;
            case 25:
                // NetworkDeath: Field(0)=victimId, Field(1)=victimName,
                // Field(2)=killerId, Field(3)=killerName.
                row.TargetName = NullIfEmpty(line.Field(1));
                row.ActorName = NullIfEmpty(line.Field(3));
                break;
            case 26: case 30:
                // NetworkBuff / NetworkBuffRemove. Field(7) is a HEX value
                // (count or shield param — confirmed by Galvanize=00, Swift Sprint=64=100decimal).
                row.AbilityId = (int)line.FieldHex(0);
                row.AbilityName = NullIfEmpty(line.Field(1));
                row.ActorName = NullIfEmpty(line.Field(4));
                row.TargetName = NullIfEmpty(line.Field(6));
                row.Amount = (long)line.FieldHex(7);
                break;
            case 37:
                // NetworkActionSync — per-ability status snapshot.
                // Field(0)=actorId, Field(1)=actorName.
                row.ActorName = NullIfEmpty(line.Field(1));
                break;
            case 38:
                // NetworkStatusEffect — full per-player status snapshot.
                // Field(0)=playerId, Field(1)=playerName.
                row.TargetName = NullIfEmpty(line.Field(1));
                break;
            case 39:
                // NetworkUpdateHp — HP/MP snapshot.
                // Field(0)=playerId, Field(1)=playerName, Field(2)=currentHpDecimal,
                // Field(3)=maxHpDecimal.
                row.TargetName = NullIfEmpty(line.Field(1));
                row.Amount = line.FieldInt(2); // current HP
                break;
        }

        // Flush the buffer in chunks to avoid unbounded memory if a match runs long
        // or contains an unusual number of events.
        bool shouldFlushChunk = false;
        lock (_lock) {
            if (_activeMatchId != matchId) {
                // Match was stopped between dequeue and now; drop the row.
                return;
            }
            _buffer.Add(row);
            if (_buffer.Count >= 2000) shouldFlushChunk = true;
        }

        if (shouldFlushChunk) {
            _ = FlushChunkAsync(matchId);
        }
    }

    private async Task FlushChunkAsync(string matchId) {
        List<SqliteStorageService.MatchEventRow> chunk;
        lock (_lock) {
            if (_activeMatchId != matchId || _buffer.Count == 0) return;
            chunk = _buffer;
            _buffer = new List<SqliteStorageService.MatchEventRow>();
        }
        try {
            await _plugin.SqliteStorage.RecordMatchEventsAsync(matchId, chunk);
        } catch (Exception ex) {
            _plugin.Log.Warning(ex, $"[CcEventIngestor] Chunk flush failed for {matchId}; events lost.");
        }
    }

    private static string MapEventType(int t) => t switch {
        21 => "ability",
        22 => "ability_aoe",
        25 => "death",
        26 => "buff_apply",
        30 => "buff_remove",
        37 => "status_sync",
        38 => "status_snapshot",
        39 => "hp_update",
        _ => $"raw_{t}",
    };

    /// <summary>
    /// Parses the 8 (flags, value) effect-entry pairs of a NetworkAbility line
    /// (Fields 6..21). For each effect-03 (damage) entry, accumulates damage
    /// from the upper 16 bits of the value field — verified against real CC
    /// damage values (e.g. flags=720003 value=33900000 → damage=0x3390=13200).
    /// Sets row.Amount to the total damage (or 0 if none) and row.Absorbed=true
    /// when any effect-03 entry has value=0 (typical signature of a fully-
    /// absorbed-by-shield hit).
    /// </summary>
    private static void ParseAbilityEffects(ActLogLine line, SqliteStorageService.MatchEventRow row) {
        long totalDamage = 0;
        long totalHeal = 0;
        bool sawZeroDamage = false;

        for (var pair = 0; pair < 8; pair++) {
            var flagsField = line.Field(6 + pair * 2);
            var valueField = line.Field(7 + pair * 2);
            if (string.IsNullOrEmpty(flagsField) || flagsField == "0") continue;
            if (!uint.TryParse(flagsField, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var flagsHex)) continue;
            uint valueHex = 0;
            if (!string.IsNullOrEmpty(valueField)) {
                uint.TryParse(valueField, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out valueHex);
            }
            var effectType = flagsHex & 0xFF;
            switch (effectType) {
                case 0x03: // damage
                    if (valueHex == 0) sawZeroDamage = true;
                    else totalDamage += (long)(valueHex >> 16);
                    break;
                case 0x04: // heal (lifesteal in attack abilities — confirmed not absorption)
                    totalHeal += (long)(valueHex >> 16);
                    break;
                // 0x0E (status apply), 0x1B (ability echo), 0x0F (mp/tp gain) — not aggregated here
            }
        }

        row.Amount = totalDamage;
        row.HealAmount = totalHeal;
        row.Absorbed = sawZeroDamage;
    }

    private static string ToIsoUtc(DateTime dt) {
        if (dt == DateTime.MinValue) return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
