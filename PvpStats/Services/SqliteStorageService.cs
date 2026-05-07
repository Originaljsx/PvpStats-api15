using Microsoft.Data.Sqlite;
using PvpStats.Types.Match;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PvpStats.Services;

/// <summary>
/// Phase B storage layer. Runs alongside LiteDB-based StorageService for now;
/// receives mirrored writes so the SQLite schema is always populated, and
/// powers CSV exports that LiteDB never had a clean answer for.
/// </summary>
internal class SqliteStorageService : IDisposable {
    private readonly Plugin _plugin;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    internal string DatabasePath => _dbPath;

    internal SqliteStorageService(Plugin plugin, string dbPath) {
        _plugin = plugin;
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        InitializeSchema();
    }

    public void Dispose() {
        _writeLock.Dispose();
    }

    private SqliteConnection Open() {
        var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
        conn.Open();
        using (var pragma = conn.CreateCommand()) {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    private void InitializeSchema() {
        try {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS matches (
    id TEXT PRIMARY KEY,
    mode TEXT NOT NULL,
    duty_id INTEGER,
    territory_id INTEGER,
    arena TEXT,
    duty_start TEXT NOT NULL,
    match_start TEXT,
    match_end TEXT,
    match_duration_seconds REAL,
    is_completed INTEGER DEFAULT 0,
    is_deleted INTEGER DEFAULT 0,
    is_bookmarked INTEGER DEFAULT 0,
    is_overtime INTEGER DEFAULT 0,
    match_type TEXT,
    winner_team TEXT,
    local_player_name TEXT,
    local_player_world TEXT,
    data_center TEXT,
    game_version TEXT,
    plugin_version TEXT,
    rank_before_json TEXT,
    rank_after_json TEXT,
    raw_blob_json TEXT,
    written_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS match_players (
    match_id TEXT NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
    player_idx INTEGER NOT NULL,
    name TEXT NOT NULL,
    world TEXT,
    job TEXT,
    team TEXT,
    rank_json TEXT,
    kills INTEGER,
    deaths INTEGER,
    assists INTEGER,
    damage_dealt INTEGER,
    damage_taken INTEGER,
    hp_restored INTEGER,
    time_on_crystal_seconds REAL,
    effective_shielding INTEGER,
    effective_mitigation INTEGER,
    overheal INTEGER,
    PRIMARY KEY (match_id, player_idx)
);

CREATE TABLE IF NOT EXISTS match_events (
    match_id TEXT NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
    seq INTEGER NOT NULL,
    timestamp_utc TEXT NOT NULL,
    event_type TEXT NOT NULL,
    actor_name TEXT,
    target_name TEXT,
    ability_id INTEGER,
    ability_name TEXT,
    amount INTEGER,
    flags_json TEXT,
    PRIMARY KEY (match_id, seq)
);

CREATE INDEX IF NOT EXISTS idx_matches_mode_start ON matches(mode, duty_start);
CREATE INDEX IF NOT EXISTS idx_match_players_name ON match_players(name, world);
CREATE INDEX IF NOT EXISTS idx_match_events_type ON match_events(match_id, event_type);
";
            cmd.ExecuteNonQuery();
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"SQLite schema init failed at {_dbPath}.");
        }
    }

    internal async Task RecordCCMatchAsync(CrystallineConflictMatch match) {
        if (match?.Id == null) return;
        await _writeLock.WaitAsync();
        try {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            UpsertCCMatch(conn, tx, match);
            UpsertCCPlayers(conn, tx, match);
            tx.Commit();
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"Failed to mirror CC match {match.Id} to SQLite.");
        } finally {
            _writeLock.Release();
        }
    }

    private void UpsertCCMatch(SqliteConnection conn, SqliteTransaction tx, CrystallineConflictMatch match) {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO matches (
    id, mode, duty_id, territory_id, arena, duty_start, match_start, match_end,
    match_duration_seconds, is_completed, is_deleted, is_bookmarked, is_overtime,
    match_type, winner_team, local_player_name, local_player_world,
    data_center, game_version, plugin_version, rank_before_json, rank_after_json, raw_blob_json
) VALUES (
    $id, 'cc', $duty_id, $territory_id, $arena, $duty_start, $match_start, $match_end,
    $match_duration_seconds, $is_completed, $is_deleted, $is_bookmarked, $is_overtime,
    $match_type, $winner_team, $local_name, $local_world,
    $data_center, $game_version, $plugin_version, $rank_before, $rank_after, $raw_blob
)
ON CONFLICT(id) DO UPDATE SET
    duty_id = excluded.duty_id,
    territory_id = excluded.territory_id,
    arena = excluded.arena,
    match_start = excluded.match_start,
    match_end = excluded.match_end,
    match_duration_seconds = excluded.match_duration_seconds,
    is_completed = excluded.is_completed,
    is_deleted = excluded.is_deleted,
    is_bookmarked = excluded.is_bookmarked,
    is_overtime = excluded.is_overtime,
    match_type = excluded.match_type,
    winner_team = excluded.winner_team,
    local_player_name = excluded.local_player_name,
    local_player_world = excluded.local_player_world,
    data_center = excluded.data_center,
    game_version = excluded.game_version,
    plugin_version = excluded.plugin_version,
    rank_before_json = excluded.rank_before_json,
    rank_after_json = excluded.rank_after_json,
    raw_blob_json = excluded.raw_blob_json;
";
        var post = match.PostMatch;
        cmd.Parameters.AddWithValue("$id", match.Id.ToString());
        cmd.Parameters.AddWithValue("$duty_id", (long)match.DutyId);
        cmd.Parameters.AddWithValue("$territory_id", (long)match.TerritoryId);
        cmd.Parameters.AddWithValue("$arena", (object?)match.Arena?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duty_start", IsoUtc(match.DutyStartTime));
        cmd.Parameters.AddWithValue("$match_start", (object?)IsoUtcOrNull(match.MatchStartTime) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$match_end", (object?)IsoUtcOrNull(match.MatchEndTime) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$match_duration_seconds", (object?)match.MatchDuration?.TotalSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_completed", match.IsCompleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$is_deleted", match.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$is_bookmarked", match.IsBookmarked ? 1 : 0);
        cmd.Parameters.AddWithValue("$is_overtime", match.IsOvertime ? 1 : 0);
        cmd.Parameters.AddWithValue("$match_type", match.MatchType.ToString());
        cmd.Parameters.AddWithValue("$winner_team", (object?)match.MatchWinner?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$local_name", (object?)match.LocalPlayer?.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$local_world", (object?)match.LocalPlayer?.HomeWorld ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$data_center", (object?)match.DataCenter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$game_version", (object?)match.GameVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plugin_version", (object?)match.PluginVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rank_before", (object?)JsonOrNull(post?.RankBefore) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rank_after", (object?)JsonOrNull(post?.RankAfter) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$raw_blob", JsonOrNull(match) ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    private void UpsertCCPlayers(SqliteConnection conn, SqliteTransaction tx, CrystallineConflictMatch match) {
        // Wipe existing rows for this match before re-inserting; handles in-place updates cleanly.
        using (var del = conn.CreateCommand()) {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM match_players WHERE match_id = $id;";
            del.Parameters.AddWithValue("$id", match.Id.ToString());
            del.ExecuteNonQuery();
        }

        var rows = FlattenCCPlayers(match).ToList();
        if (rows.Count == 0) return;

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
INSERT INTO match_players (
    match_id, player_idx, name, world, job, team, rank_json,
    kills, deaths, assists, damage_dealt, damage_taken, hp_restored, time_on_crystal_seconds
) VALUES (
    $match_id, $player_idx, $name, $world, $job, $team, $rank,
    $kills, $deaths, $assists, $damage_dealt, $damage_taken, $hp_restored, $tcrystal
);
";
        var pMatchId = ins.Parameters.Add("$match_id", SqliteType.Text);
        var pIdx = ins.Parameters.Add("$player_idx", SqliteType.Integer);
        var pName = ins.Parameters.Add("$name", SqliteType.Text);
        var pWorld = ins.Parameters.Add("$world", SqliteType.Text);
        var pJob = ins.Parameters.Add("$job", SqliteType.Text);
        var pTeam = ins.Parameters.Add("$team", SqliteType.Text);
        var pRank = ins.Parameters.Add("$rank", SqliteType.Text);
        var pKills = ins.Parameters.Add("$kills", SqliteType.Integer);
        var pDeaths = ins.Parameters.Add("$deaths", SqliteType.Integer);
        var pAssists = ins.Parameters.Add("$assists", SqliteType.Integer);
        var pDD = ins.Parameters.Add("$damage_dealt", SqliteType.Integer);
        var pDT = ins.Parameters.Add("$damage_taken", SqliteType.Integer);
        var pHP = ins.Parameters.Add("$hp_restored", SqliteType.Integer);
        var pTC = ins.Parameters.Add("$tcrystal", SqliteType.Real);

        var idx = 0;
        foreach (var r in rows) {
            pMatchId.Value = match.Id.ToString();
            pIdx.Value = idx++;
            pName.Value = (object?)r.Name ?? DBNull.Value;
            pWorld.Value = (object?)r.World ?? DBNull.Value;
            pJob.Value = (object?)r.Job ?? DBNull.Value;
            pTeam.Value = (object?)r.Team ?? DBNull.Value;
            pRank.Value = (object?)r.RankJson ?? DBNull.Value;
            pKills.Value = (object?)r.Kills ?? DBNull.Value;
            pDeaths.Value = (object?)r.Deaths ?? DBNull.Value;
            pAssists.Value = (object?)r.Assists ?? DBNull.Value;
            pDD.Value = (object?)r.DamageDealt ?? DBNull.Value;
            pDT.Value = (object?)r.DamageTaken ?? DBNull.Value;
            pHP.Value = (object?)r.HpRestored ?? DBNull.Value;
            pTC.Value = (object?)r.TimeOnCrystalSeconds ?? DBNull.Value;
            ins.ExecuteNonQuery();
        }
    }

    private static IEnumerable<FlatPlayer> FlattenCCPlayers(CrystallineConflictMatch match) {
        var post = match.PostMatch;
        if (post != null) {
            foreach (var teamKv in post.Teams) {
                foreach (var row in teamKv.Value.PlayerStats) {
                    yield return new FlatPlayer {
                        Name = row.Player?.Name,
                        World = row.Player?.HomeWorld,
                        Job = row.Job?.ToString(),
                        Team = teamKv.Key.ToString(),
                        RankJson = JsonOrNull(row.PlayerRank),
                        Kills = row.Kills,
                        Deaths = row.Deaths,
                        Assists = row.Assists,
                        DamageDealt = row.DamageDealt,
                        DamageTaken = row.DamageTaken,
                        HpRestored = row.HPRestored,
                        TimeOnCrystalSeconds = row.TimeOnCrystal.TotalSeconds,
                    };
                }
            }
            yield break;
        }
        // No post-match scoreboard yet — fall back to the intro roster so the row at least exists.
        foreach (var teamKv in match.Teams) {
            foreach (var p in teamKv.Value.Players) {
                yield return new FlatPlayer {
                    Name = p.Alias?.Name,
                    World = p.Alias?.HomeWorld,
                    Job = p.Job?.ToString(),
                    Team = teamKv.Key.ToString(),
                    RankJson = JsonOrNull(p.Rank),
                };
            }
        }
    }

    private sealed class FlatPlayer {
        public string? Name;
        public string? World;
        public string? Job;
        public string? Team;
        public string? RankJson;
        public int? Kills;
        public int? Deaths;
        public int? Assists;
        public int? DamageDealt;
        public int? DamageTaken;
        public int? HpRestored;
        public double? TimeOnCrystalSeconds;
    }

    /// <summary>
    /// Streams a CSV containing one row per (match × player) for completed CC matches.
    /// Returns the path written to, or null on failure.
    /// </summary>
    internal string? ExportCCToCsv(string outputPath) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    m.id, m.duty_start, m.match_start, m.match_end, m.match_duration_seconds,
    m.arena, m.match_type, m.winner_team, m.is_overtime,
    m.local_player_name, m.local_player_world, m.data_center, m.plugin_version,
    p.player_idx, p.name, p.world, p.job, p.team,
    p.kills, p.deaths, p.assists,
    p.damage_dealt, p.damage_taken, p.hp_restored, p.time_on_crystal_seconds,
    p.effective_shielding, p.effective_mitigation, p.overheal
FROM matches m
JOIN match_players p ON p.match_id = m.id
WHERE m.mode = 'cc' AND m.is_deleted = 0 AND m.is_completed = 1
ORDER BY m.duty_start, m.id, p.player_idx;
";
            using var rdr = cmd.ExecuteReader();

            using var sw = new StreamWriter(outputPath);
            // Header
            for (var i = 0; i < rdr.FieldCount; i++) {
                if (i > 0) sw.Write(',');
                sw.Write(EscapeCsv(rdr.GetName(i)));
            }
            sw.WriteLine();
            // Rows
            while (rdr.Read()) {
                for (var i = 0; i < rdr.FieldCount; i++) {
                    if (i > 0) sw.Write(',');
                    var v = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                    sw.Write(EscapeCsv(v?.ToString()));
                }
                sw.WriteLine();
            }
            return outputPath;
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"CSV export failed (target: {outputPath}).");
            return null;
        }
    }

    private static string EscapeCsv(string? s) {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var needsQuote = s.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        return needsQuote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string IsoUtc(DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string? IsoUtcOrNull(DateTime? dt) =>
        dt.HasValue ? IsoUtc(dt.Value) : null;

    private static string? JsonOrNull(object? o) {
        if (o == null) return null;
        try {
            return JsonSerializer.Serialize(o, new JsonSerializerOptions {
                ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
                WriteIndented = false,
            });
        } catch {
            return null;
        }
    }
}
