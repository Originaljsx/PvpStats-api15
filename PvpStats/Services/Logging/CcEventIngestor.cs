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
/// Filters to the line types relevant for advanced PvP analytics:
///   21  NetworkAbility       (single-target action effect; flags include 'absorbed')
///   22  NetworkAOEAbility    (AOE action effect)
///   25  NetworkDeath
///   26  NetworkBuff          (status apply — shield value lives in a Param field)
///   30  NetworkBuffRemove    (status removed — diff vs apply gives effective shield)
/// Other line types are dropped to keep volume manageable. The full raw line
/// is preserved in match_events.flags_json so step 3 can do detailed parsing
/// without re-reading the IINACT logs.
/// </summary>
internal sealed class CcEventIngestor : IDisposable {
    private static readonly HashSet<int> CapturedTypes = new() { 21, 22, 25, 26, 30 };

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

        // Best-effort field extraction. Step 3 will do precise parsing per type.
        switch (line.Type) {
            case 21: case 22: // NetworkAbility / NetworkAOEAbility
                row.ActorName = NullIfEmpty(line.Field(1));
                row.TargetName = NullIfEmpty(line.Field(5));
                row.AbilityId = (int)line.FieldHex(2);
                row.AbilityName = NullIfEmpty(line.Field(3));
                break;
            case 25: // NetworkDeath
                row.TargetName = NullIfEmpty(line.Field(1));
                row.ActorName = NullIfEmpty(line.Field(3));
                break;
            case 26: case 30: // NetworkBuff / NetworkBuffRemove
                row.AbilityId = (int)line.FieldHex(0);
                row.AbilityName = NullIfEmpty(line.Field(1));
                row.ActorName = NullIfEmpty(line.Field(5));
                row.TargetName = NullIfEmpty(line.Field(3));
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
        _ => $"raw_{t}",
    };

    private static string ToIsoUtc(DateTime dt) {
        if (dt == DateTime.MinValue) return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
