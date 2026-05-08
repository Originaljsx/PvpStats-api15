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
CREATE INDEX IF NOT EXISTS idx_match_events_actor ON match_events(match_id, actor_name);
CREATE INDEX IF NOT EXISTS idx_match_events_target ON match_events(match_id, target_name);
";
            cmd.ExecuteNonQuery();

            // Additive column migrations for Phase B step 3 (rollup metrics).
            // SQLite can't add a column conditionally in pure DDL, so use PRAGMA + ALTER TABLE.
            EnsureColumn(conn, "match_players", "events_involving_player", "INTEGER");
            EnsureColumn(conn, "match_players", "buff_param_applied_sum", "INTEGER");
            EnsureColumn(conn, "match_players", "buff_apply_count", "INTEGER");
            EnsureColumn(conn, "match_players", "buff_remove_count", "INTEGER");
            EnsureColumn(conn, "match_players", "deaths_caused", "INTEGER");
            EnsureColumn(conn, "match_players", "deaths_suffered_in_log", "INTEGER");
            EnsureColumn(conn, "match_players", "rolled_up_at_utc", "TEXT");
            // Layer 1 (v2.6.6.0): exact damage / absorbed-hit metrics from the log.
            EnsureColumn(conn, "match_players", "damage_dealt_log", "INTEGER");
            EnsureColumn(conn, "match_players", "damage_taken_log", "INTEGER");
            EnsureColumn(conn, "match_players", "heal_dealt_log", "INTEGER");
            EnsureColumn(conn, "match_players", "zero_damage_hits_dealt", "INTEGER");
            EnsureColumn(conn, "match_players", "zero_damage_hits_taken", "INTEGER");
            EnsureColumn(conn, "match_events", "heal_amount", "INTEGER");
            EnsureColumn(conn, "match_events", "absorbed", "INTEGER DEFAULT 0");
            // Layer 2 (v2.6.7.0): shield-aware metrics using the curated catalog.
            EnsureColumn(conn, "match_players", "shields_applied_count", "INTEGER");
            EnsureColumn(conn, "match_players", "shield_uptime_seconds", "REAL");
            EnsureColumn(conn, "match_players", "shielded_hits_taken", "INTEGER");
            EnsureColumn(conn, "match_players", "shielded_hits_caused_others", "INTEGER");
            // Layer 3 (v2.6.8.0): mit/amp/vuln normalization columns.
            EnsureColumn(conn, "match_players", "damage_dealt_raw_log", "INTEGER");
            EnsureColumn(conn, "match_players", "damage_taken_raw_log", "INTEGER");
            EnsureColumn(conn, "match_players", "damage_dealt_amp_added", "INTEGER");
            EnsureColumn(conn, "match_players", "damage_taken_mit_avoided", "INTEGER");
            // Layer 4 (v2.6.9.0): the headline ones — total HP shielded + total HP your shields absorbed.
            EnsureColumn(conn, "match_players", "shielding_done_log", "INTEGER");
            EnsureColumn(conn, "match_players", "shielding_damage_mitigated_log", "INTEGER");
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"SQLite schema init failed at {_dbPath}.");
        }
    }

    private void EnsureColumn(SqliteConnection conn, string table, string column, string typeDecl) {
        try {
            using var info = conn.CreateCommand();
            info.CommandText = $"PRAGMA table_info({table});";
            using var rdr = info.ExecuteReader();
            while (rdr.Read()) {
                if (string.Equals(rdr.GetString(1), column, StringComparison.OrdinalIgnoreCase)) {
                    return; // already present
                }
            }
        } catch { /* fall through to ALTER attempt */ }
        try {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};";
            alter.ExecuteNonQuery();
        } catch (Exception ex) {
            _plugin.Log.Warning(ex, $"Failed to ALTER TABLE {table} ADD COLUMN {column}: column may already exist.");
        }
    }

    /// <summary>
    /// Bulk-insert match_events rows in a single transaction. Caller is responsible
    /// for ordering events by sequence within the match.
    /// </summary>
    internal async Task RecordMatchEventsAsync(string matchId, IEnumerable<MatchEventRow> events) {
        if (string.IsNullOrEmpty(matchId)) return;
        var list = events as IList<MatchEventRow> ?? events.ToList();
        if (list.Count == 0) return;

        await _writeLock.WaitAsync();
        try {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"
INSERT OR REPLACE INTO match_events (
    match_id, seq, timestamp_utc, event_type, actor_name, target_name,
    ability_id, ability_name, amount, heal_amount, absorbed, flags_json
) VALUES (
    $match_id, $seq, $ts, $event_type, $actor, $target,
    $ability_id, $ability_name, $amount, $heal, $absorbed, $flags
);
";
            var pMatch = ins.Parameters.Add("$match_id", SqliteType.Text);
            var pSeq = ins.Parameters.Add("$seq", SqliteType.Integer);
            var pTs = ins.Parameters.Add("$ts", SqliteType.Text);
            var pEt = ins.Parameters.Add("$event_type", SqliteType.Text);
            var pActor = ins.Parameters.Add("$actor", SqliteType.Text);
            var pTarget = ins.Parameters.Add("$target", SqliteType.Text);
            var pAbId = ins.Parameters.Add("$ability_id", SqliteType.Integer);
            var pAbN = ins.Parameters.Add("$ability_name", SqliteType.Text);
            var pAmt = ins.Parameters.Add("$amount", SqliteType.Integer);
            var pHeal = ins.Parameters.Add("$heal", SqliteType.Integer);
            var pAbs = ins.Parameters.Add("$absorbed", SqliteType.Integer);
            var pFlags = ins.Parameters.Add("$flags", SqliteType.Text);

            pMatch.Value = matchId;
            foreach (var e in list) {
                pSeq.Value = e.Seq;
                pTs.Value = e.TimestampUtcIso;
                pEt.Value = e.EventType ?? string.Empty;
                pActor.Value = (object?)e.ActorName ?? DBNull.Value;
                pTarget.Value = (object?)e.TargetName ?? DBNull.Value;
                pAbId.Value = (object?)e.AbilityId ?? DBNull.Value;
                pAbN.Value = (object?)e.AbilityName ?? DBNull.Value;
                pAmt.Value = (object?)e.Amount ?? DBNull.Value;
                pHeal.Value = (object?)e.HealAmount ?? DBNull.Value;
                pAbs.Value = e.Absorbed ? 1 : 0;
                pFlags.Value = (object?)e.FlagsJson ?? DBNull.Value;
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"Failed to bulk-insert match_events for match {matchId}.");
        } finally {
            _writeLock.Release();
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

    /// <summary>Lightweight DTO for batch event inserts.</summary>
    internal sealed class MatchEventRow {
        public required long Seq;
        public required string TimestampUtcIso;
        public required string EventType;
        public string? ActorName;
        public string? TargetName;
        public int? AbilityId;
        public string? AbilityName;
        public long? Amount;
        public long? HealAmount;
        public bool Absorbed;
        public string? FlagsJson;
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
    /// Compute and write per-player rollup metrics for a single match. This is a
    /// *framework* iteration — metrics here are simple aggregates over match_events
    /// (event involvement counts, buff Param sums, kill/death counts). Real
    /// FFLogs-style effective_shielding will land in a follow-up that decodes the
    /// FFXIV-obfuscated damage values and absorbed flags inside NetworkAbility
    /// effect entries.
    /// </summary>
    internal async Task RollupCCMatchAsync(string matchId) {
        if (string.IsNullOrEmpty(matchId)) return;
        await _writeLock.WaitAsync();
        try {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            // Per-player buff_apply param sum + buff counts (where this player was the source/applier).
            using (var cmd = conn.CreateCommand()) {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE match_players AS p
SET
    buff_param_applied_sum = COALESCE((
        SELECT SUM(amount)
        FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type = 'buff_apply'
          AND e.actor_name = p.name
    ), 0),
    buff_apply_count = COALESCE((
        SELECT COUNT(*)
        FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type = 'buff_apply'
          AND e.actor_name = p.name
    ), 0),
    buff_remove_count = COALESCE((
        SELECT COUNT(*)
        FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type = 'buff_remove'
          AND e.actor_name = p.name
    ), 0)
WHERE p.match_id = $match_id;
";
                cmd.Parameters.AddWithValue("$match_id", matchId);
                cmd.ExecuteNonQuery();
            }

            // Per-player deaths suffered (target of a death event) and deaths caused (actor of a death event).
            using (var cmd = conn.CreateCommand()) {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE match_players AS p
SET
    deaths_suffered_in_log = COALESCE((
        SELECT COUNT(*)
        FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type = 'death'
          AND e.target_name = p.name
    ), 0),
    deaths_caused = COALESCE((
        SELECT COUNT(*)
        FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type = 'death'
          AND e.actor_name = p.name
    ), 0)
WHERE p.match_id = $match_id;
";
                cmd.Parameters.AddWithValue("$match_id", matchId);
                cmd.ExecuteNonQuery();
            }

            // Total events that mention this player in either role — sanity-check metric.
            using (var cmd = conn.CreateCommand()) {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE match_players AS p
SET
    events_involving_player = COALESCE((
        SELECT COUNT(*)
        FROM match_events e
        WHERE e.match_id = p.match_id
          AND (e.actor_name = p.name OR e.target_name = p.name)
    ), 0),
    rolled_up_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE p.match_id = $match_id;
";
                cmd.Parameters.AddWithValue("$match_id", matchId);
                cmd.ExecuteNonQuery();
            }

            // Layer 2 (v2.6.7.0): shield-aware metrics using ShieldCatalog.
            // shields_applied_count: number of shield-type buff_apply events sourced from this player
            // shield_uptime_seconds: total seconds (apply -> remove pairs) of shield-type buffs this player applied
            // shielded_hits_taken: count of damage=0 hits on this player while at least one shield-type buff was active on them
            // shielded_hits_caused_others: same but where this player was the SHIELD source (the credit-worthy applier)
            var shieldIdsCsv = Game.ShieldCatalog.SqlInList();
            using (var cmd = conn.CreateCommand()) {
                cmd.Transaction = tx;
                cmd.CommandText = $@"
UPDATE match_players AS p
SET
    shields_applied_count = COALESCE((
        SELECT COUNT(*) FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type = 'buff_apply'
          AND e.actor_name = p.name
          AND e.ability_id IN ({shieldIdsCsv})
    ), 0),
    shield_uptime_seconds = COALESCE((
        SELECT SUM(
            CAST((julianday(rem.timestamp_utc) - julianday(app.timestamp_utc)) * 86400.0 AS REAL)
        )
        FROM match_events app
        LEFT JOIN match_events rem
          ON rem.match_id = app.match_id
         AND rem.event_type = 'buff_remove'
         AND rem.ability_id = app.ability_id
         AND rem.target_name = app.target_name
         AND rem.actor_name = app.actor_name
         AND rem.seq > app.seq
         AND NOT EXISTS (
             SELECT 1 FROM match_events mid
             WHERE mid.match_id = app.match_id
               AND mid.event_type IN ('buff_apply', 'buff_remove')
               AND mid.ability_id = app.ability_id
               AND mid.target_name = app.target_name
               AND mid.seq > app.seq AND mid.seq < rem.seq
         )
        WHERE app.match_id = p.match_id
          AND app.event_type = 'buff_apply'
          AND app.actor_name = p.name
          AND app.ability_id IN ({shieldIdsCsv})
          AND rem.seq IS NOT NULL
    ), 0.0)
WHERE p.match_id = $match_id;
";
                cmd.Parameters.AddWithValue("$match_id", matchId);
                cmd.ExecuteNonQuery();
            }

            // shielded_hits_taken — for each absorbed (damage=0) hit on player p, was a shield-type
            // status active on p at that moment? Approximated via "the most recent buff_apply for this
            // status on p (before the hit) is not yet matched by a buff_remove (after the hit)".
            using (var cmd = conn.CreateCommand()) {
                cmd.Transaction = tx;
                cmd.CommandText = $@"
UPDATE match_players AS p
SET
    shielded_hits_taken = COALESCE((
        SELECT COUNT(*) FROM match_events hit
        WHERE hit.match_id = p.match_id
          AND hit.event_type IN ('ability', 'ability_aoe')
          AND hit.target_name = p.name
          AND hit.absorbed = 1
          AND EXISTS (
              SELECT 1 FROM match_events sa
              WHERE sa.match_id = hit.match_id
                AND sa.event_type = 'buff_apply'
                AND sa.target_name = hit.target_name
                AND sa.ability_id IN ({shieldIdsCsv})
                AND sa.seq < hit.seq
                AND NOT EXISTS (
                    SELECT 1 FROM match_events sr
                    WHERE sr.match_id = sa.match_id
                      AND sr.event_type = 'buff_remove'
                      AND sr.ability_id = sa.ability_id
                      AND sr.target_name = sa.target_name
                      AND sr.seq > sa.seq AND sr.seq < hit.seq
                )
          )
    ), 0),
    shielded_hits_caused_others = COALESCE((
        SELECT COUNT(*) FROM match_events hit
        WHERE hit.match_id = p.match_id
          AND hit.event_type IN ('ability', 'ability_aoe')
          AND hit.absorbed = 1
          AND hit.target_name <> p.name
          AND EXISTS (
              SELECT 1 FROM match_events sa
              WHERE sa.match_id = hit.match_id
                AND sa.event_type = 'buff_apply'
                AND sa.actor_name = p.name
                AND sa.target_name = hit.target_name
                AND sa.ability_id IN ({shieldIdsCsv})
                AND sa.seq < hit.seq
                AND NOT EXISTS (
                    SELECT 1 FROM match_events sr
                    WHERE sr.match_id = sa.match_id
                      AND sr.event_type = 'buff_remove'
                      AND sr.ability_id = sa.ability_id
                      AND sr.target_name = sa.target_name
                      AND sr.seq > sa.seq AND sr.seq < hit.seq
                )
          )
    ), 0)
WHERE p.match_id = $match_id;
";
                cmd.Parameters.AddWithValue("$match_id", matchId);
                cmd.ExecuteNonQuery();
            }

            // Layer 1 (v2.6.6.0): exact damage / absorbed-hit metrics from log.
            // damage_dealt_log: sum of effect-03 damage values where this player was caster
            // damage_taken_log: same but as target
            // heal_dealt_log: sum of effect-04 (lifesteal/heal) values where this player was caster
            // zero_damage_hits_*: count of ability events with absorbed=1 (effect-03 had value=0)
            using (var cmd = conn.CreateCommand()) {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE match_players AS p
SET
    damage_dealt_log = COALESCE((
        SELECT SUM(amount) FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type IN ('ability', 'ability_aoe')
          AND e.actor_name = p.name
    ), 0),
    damage_taken_log = COALESCE((
        SELECT SUM(amount) FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type IN ('ability', 'ability_aoe')
          AND e.target_name = p.name
    ), 0),
    heal_dealt_log = COALESCE((
        SELECT SUM(heal_amount) FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type IN ('ability', 'ability_aoe')
          AND e.actor_name = p.name
    ), 0),
    zero_damage_hits_dealt = COALESCE((
        SELECT COUNT(*) FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type IN ('ability', 'ability_aoe')
          AND e.actor_name = p.name
          AND e.absorbed = 1
    ), 0),
    zero_damage_hits_taken = COALESCE((
        SELECT COUNT(*) FROM match_events e
        WHERE e.match_id = p.match_id
          AND e.event_type IN ('ability', 'ability_aoe')
          AND e.target_name = p.name
          AND e.absorbed = 1
    ), 0)
WHERE p.match_id = $match_id;
";
                cmd.Parameters.AddWithValue("$match_id", matchId);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"Rollup failed for match {matchId}.");
        } finally {
            _writeLock.Release();
        }

        // Layer 3 (v2.6.8.0) — mit/amp/vuln normalization. Runs as a separate
        // read-then-write pass since it walks the event timeline in C# memory.
        try {
            await ApplyLayer3RollupAsync(matchId);
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"Layer 3 rollup failed for match {matchId}.");
        }

        // Layer 4 (v2.6.9.0) — the two HP metrics: shielding done + shielding-absorbed damage.
        try {
            await ApplyLayer4ShieldingHpAsync(matchId);
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"Layer 4 shielding-HP rollup failed for match {matchId}.");
        }
    }

    /// <summary>
    /// Layer 4: compute per-player <c>shielding_done_log</c> (sum of HP your shields
    /// were worth at apply time) and <c>shielding_damage_mitigated_log</c> (estimated
    /// HP your shields absorbed during the match).
    ///
    /// Pass 1 (collect):
    ///   • For each shield-applying ability cast (catalog hit), compute shield HP:
    ///     - HealEffectMultiple  → heal_amount × Factor (Adloquium-family)
    ///     - MaxHpFraction       → most recent observed max HP for target × Factor
    ///     - FlatValue           → constant
    ///     Credit caster's <c>shielding_done</c>.
    ///   • Build per-ability_id damage histogram (non-zero hits only) for median estimation.
    ///   • Note shield-status applies so we know who applied which shield to whom.
    ///
    /// Pass 2 (attribute):
    ///   • For each absorbed-by-shield hit (effect-03 value=0 on a target with at
    ///     least one active shield-type buff), estimate the would-be damage as the
    ///     median damage of that ability across non-absorbed hits in this match,
    ///     credit the shield's source.
    /// </summary>
    private async Task ApplyLayer4ShieldingHpAsync(string matchId) {
        await _writeLock.WaitAsync();
        try {
            using var conn = Open();

            // Read all events relevant to this layer in seq order.
            var formulas = Game.ShieldFormulaCatalog.ByAbilityId;
            var shieldStatusIds = Game.ShieldCatalog.ShieldStatusIds;

            var rows = new List<(long seq, string type, string? actor, string? target, int? abilityId, long? amount, long? heal, int absorbed)>(4096);
            using (var read = conn.CreateCommand()) {
                read.CommandText = @"
SELECT seq, event_type, actor_name, target_name, ability_id, amount, heal_amount, absorbed
FROM match_events
WHERE match_id = $match_id
ORDER BY seq;";
                read.Parameters.AddWithValue("$match_id", matchId);
                using var rdr = read.ExecuteReader();
                while (rdr.Read()) {
                    rows.Add((
                        rdr.GetInt64(0),
                        rdr.GetString(1),
                        rdr.IsDBNull(2) ? null : rdr.GetString(2),
                        rdr.IsDBNull(3) ? null : rdr.GetString(3),
                        rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
                        rdr.IsDBNull(5) ? null : rdr.GetInt64(5),
                        rdr.IsDBNull(6) ? null : rdr.GetInt64(6),
                        rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7)
                    ));
                }
            }

            if (rows.Count == 0) return;

            var shieldingDone = new Dictionary<string, long>(StringComparer.Ordinal);
            var shieldingMitigated = new Dictionary<string, long>(StringComparer.Ordinal);

            // Per-ability damage histogram (non-zero damage events only).
            var damageByAbility = new Dictionary<int, List<long>>();
            // Most recent observed max HP per player (from hp_update events).
            var lastMaxHp = new Dictionary<string, long>(StringComparer.Ordinal);
            // Active shield buffs on each target: target_name -> list of (status_id, applier, applied_seq)
            var activeShields = new Dictionary<string, List<(uint status, string applier, long seq)>>(StringComparer.Ordinal);

            // Pass 1: collect histogram, max-HP snapshots, and the shielding_done credit.
            foreach (var r in rows) {
                if (r.type == "hp_update" && r.target != null && r.amount.HasValue) {
                    // amount in hp_update was set to current HP; we don't have max HP captured.
                    // For the v1 ship this means MaxHpFraction shields under-count when
                    // the target's last seen max HP is unknown. Fixed in 4.1.
                    lastMaxHp[r.target] = r.amount.Value;
                } else if ((r.type == "ability" || r.type == "ability_aoe") && r.amount.HasValue && r.amount.Value > 0 && r.abilityId.HasValue) {
                    if (!damageByAbility.TryGetValue(r.abilityId.Value, out var list)) {
                        list = new List<long>();
                        damageByAbility[r.abilityId.Value] = list;
                    }
                    list.Add(r.amount.Value);
                }

                if ((r.type == "ability" || r.type == "ability_aoe") && r.abilityId.HasValue && r.actor != null) {
                    if (formulas.TryGetValue((uint)r.abilityId.Value, out var formula)) {
                        long shieldHp = 0;
                        switch (formula.Kind) {
                            case Game.ShieldFormulaKind.HealEffectMultiple:
                                if (r.heal.HasValue && r.heal.Value > 0)
                                    shieldHp = (long)(r.heal.Value * formula.Factor);
                                break;
                            case Game.ShieldFormulaKind.MaxHpFraction:
                                if (r.target != null && lastMaxHp.TryGetValue(r.target, out var maxHp) && maxHp > 0)
                                    shieldHp = (long)(maxHp * formula.Factor);
                                break;
                            case Game.ShieldFormulaKind.FlatValue:
                                shieldHp = formula.FlatValue;
                                break;
                        }
                        if (shieldHp > 0) {
                            shieldingDone.TryGetValue(r.actor, out var prev);
                            shieldingDone[r.actor] = prev + shieldHp;
                        }
                    }
                }
            }

            // Median damage per ability for absorbed-hit estimation.
            var medianByAbility = new Dictionary<int, long>();
            foreach (var kv in damageByAbility) {
                kv.Value.Sort();
                medianByAbility[kv.Key] = kv.Value[kv.Value.Count / 2];
            }

            // Pass 2: walk again with shield-active state, attribute absorbed hits.
            foreach (var r in rows) {
                if (r.type == "buff_apply" && r.abilityId.HasValue && r.target != null && shieldStatusIds.Contains((uint)r.abilityId.Value)) {
                    if (!activeShields.TryGetValue(r.target, out var list)) {
                        list = new List<(uint, string, long)>();
                        activeShields[r.target] = list;
                    }
                    list.Add(((uint)r.abilityId.Value, r.actor ?? string.Empty, r.seq));
                } else if (r.type == "buff_remove" && r.abilityId.HasValue && r.target != null && shieldStatusIds.Contains((uint)r.abilityId.Value)) {
                    if (activeShields.TryGetValue(r.target, out var list)) {
                        // Remove the most recent matching status_id (LIFO; reapplies overwrite).
                        for (var i = list.Count - 1; i >= 0; i--) {
                            if (list[i].status == (uint)r.abilityId.Value) { list.RemoveAt(i); break; }
                        }
                    }
                } else if ((r.type == "ability" || r.type == "ability_aoe") && r.absorbed == 1 && r.target != null && r.abilityId.HasValue) {
                    // Damage=0 on (presumably) shielded target. Find an active shield to credit.
                    if (activeShields.TryGetValue(r.target, out var list) && list.Count > 0) {
                        // Credit the most-recently-applied shield (heuristic; FFLogs uses
                        // similar last-applied-wins logic when multiple shields are stacked).
                        var (_, applier, _) = list[list.Count - 1];
                        if (!string.IsNullOrEmpty(applier)) {
                            var estimated = medianByAbility.GetValueOrDefault(r.abilityId.Value, 0L);
                            if (estimated > 0) {
                                shieldingMitigated.TryGetValue(applier, out var prev);
                                shieldingMitigated[applier] = prev + estimated;
                            }
                        }
                    }
                }
            }

            // Bulk write.
            using var tx = conn.BeginTransaction();
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = @"
UPDATE match_players SET
    shielding_done_log             = $done,
    shielding_damage_mitigated_log = $mit
WHERE match_id = $match_id AND name = $name;";
            var pMatch = upd.Parameters.Add("$match_id", SqliteType.Text);
            var pName = upd.Parameters.Add("$name", SqliteType.Text);
            var pDone = upd.Parameters.Add("$done", SqliteType.Integer);
            var pMit = upd.Parameters.Add("$mit", SqliteType.Integer);
            pMatch.Value = matchId;

            var allNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in shieldingDone.Keys) allNames.Add(k);
            foreach (var k in shieldingMitigated.Keys) allNames.Add(k);

            foreach (var name in allNames) {
                pName.Value = name;
                pDone.Value = shieldingDone.GetValueOrDefault(name, 0L);
                pMit.Value = shieldingMitigated.GetValueOrDefault(name, 0L);
                upd.ExecuteNonQuery();
            }
            tx.Commit();
        } finally {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Layer 3: walks every event in seq order while maintaining per-player
    /// active-modifier sets, normalizes each damage event's amount by the
    /// product of active modifiers, and writes the per-player aggregates.
    ///
    /// Damage normalization model is multiplicative:
    ///   raw_damage = displayed_damage / (Π outgoing_factors_on_caster × Π incoming_factors_on_target)
    /// where each factor &lt; 1.0 reduces damage and &gt; 1.0 amplifies.
    /// Dom-restricted modifiers (PhysicalOnly / MagicalOnly) currently apply
    /// to all damage — a known imprecision, since the IINACT log doesn't
    /// expose damage type cleanly. Layer 3.1 can refine this when we model
    /// per-ability damage types.
    /// </summary>
    private async Task ApplyLayer3RollupAsync(string matchId) {
        await _writeLock.WaitAsync();
        try {
            using var conn = Open();

            // Read all events for the match in sequence order. We only need a
            // few columns and we cap at events relevant to layer 3 (modifier
            // applies/removes + ability damage).
            var modIds = Game.DamageModifierCatalog.ById;
            var modIdsCsv = Game.DamageModifierCatalog.SqlInList();

            var events = new List<(long seq, string type, string? actor, string? target, int? abilityId, long? amount)>(4096);
            using (var read = conn.CreateCommand()) {
                read.CommandText = $@"
SELECT seq, event_type, actor_name, target_name, ability_id, amount
FROM match_events
WHERE match_id = $match_id
  AND (
        event_type IN ('ability', 'ability_aoe')
        OR (event_type IN ('buff_apply', 'buff_remove') AND ability_id IN ({modIdsCsv}))
      )
ORDER BY seq;";
                read.Parameters.AddWithValue("$match_id", matchId);
                using var rdr = read.ExecuteReader();
                while (rdr.Read()) {
                    events.Add((
                        rdr.GetInt64(0),
                        rdr.GetString(1),
                        rdr.IsDBNull(2) ? null : rdr.GetString(2),
                        rdr.IsDBNull(3) ? null : rdr.GetString(3),
                        rdr.IsDBNull(4) ? null : rdr.GetInt32(4),
                        rdr.IsDBNull(5) ? null : rdr.GetInt64(5)
                    ));
                }
            }

            if (events.Count == 0) return;

            // Active modifiers per player: Dictionary<playerName, HashSet<statusId>>.
            // We track by status_id only; if a status is reapplied while still active,
            // the second apply is a no-op (HashSet semantics), and the first remove
            // clears it. Imperfect for stack-counted modifiers but adequate for the
            // boolean-presence statuses we're tracking.
            var active = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);

            // Per-player aggregates we'll write back.
            var rawDealt = new Dictionary<string, double>(StringComparer.Ordinal);
            var rawTaken = new Dictionary<string, double>(StringComparer.Ordinal);
            var preventedByMit = new Dictionary<string, double>(StringComparer.Ordinal);
            var ampEnhanced = new Dictionary<string, double>(StringComparer.Ordinal);

            HashSet<uint> ActiveOf(string? name) {
                if (string.IsNullOrEmpty(name)) return new HashSet<uint>();
                if (!active.TryGetValue(name!, out var set)) {
                    set = new HashSet<uint>();
                    active[name!] = set;
                }
                return set;
            }

            foreach (var e in events) {
                if (e.type == "buff_apply" && e.abilityId.HasValue && e.target != null) {
                    ActiveOf(e.target).Add((uint)e.abilityId.Value);
                } else if (e.type == "buff_remove" && e.abilityId.HasValue && e.target != null) {
                    ActiveOf(e.target).Remove((uint)e.abilityId.Value);
                } else if ((e.type == "ability" || e.type == "ability_aoe") && e.amount.HasValue && e.amount.Value > 0) {
                    var displayed = (double)e.amount.Value;

                    // Compute outgoing-amp factor on caster.
                    double outFactor = 1.0;
                    if (e.actor != null) {
                        foreach (var sid in ActiveOf(e.actor)) {
                            if (modIds.TryGetValue(sid, out var mod) && mod.Direction == Game.ModifierDirection.Outgoing) {
                                outFactor *= mod.Factor;
                            }
                        }
                    }
                    // Incoming-mit factor on target.
                    double inFactor = 1.0;
                    if (e.target != null) {
                        foreach (var sid in ActiveOf(e.target)) {
                            if (modIds.TryGetValue(sid, out var mod) && mod.Direction == Game.ModifierDirection.Incoming) {
                                inFactor *= mod.Factor;
                            }
                        }
                    }

                    var combined = outFactor * inFactor;
                    if (combined <= 0) combined = 1.0;
                    var raw = displayed / combined;

                    if (e.actor != null) {
                        rawDealt.TryGetValue(e.actor, out var prevD);
                        rawDealt[e.actor] = prevD + raw;
                        // amp_enhanced = how much MORE the caster dealt vs raw (positive when outFactor > 1)
                        ampEnhanced.TryGetValue(e.actor, out var prevA);
                        ampEnhanced[e.actor] = prevA + (displayed - raw * inFactor); // outgoing-amp contribution only
                    }
                    if (e.target != null) {
                        rawTaken.TryGetValue(e.target, out var prevT);
                        rawTaken[e.target] = prevT + raw;
                        // prevented_by_mit = raw_to_target - actual_displayed (positive when inFactor < 1)
                        preventedByMit.TryGetValue(e.target, out var prevM);
                        preventedByMit[e.target] = prevM + (raw - displayed) * (inFactor < 1.0 ? 1.0 : 0.0);
                    }
                }
            }

            // Bulk update match_players.
            using var tx2 = conn.BeginTransaction();
            using var upd = conn.CreateCommand();
            upd.Transaction = tx2;
            upd.CommandText = @"
UPDATE match_players SET
    damage_dealt_raw_log     = $dealt,
    damage_taken_raw_log     = $taken,
    damage_dealt_amp_added   = $amp,
    damage_taken_mit_avoided = $mit
WHERE match_id = $match_id AND name = $name;";
            var pMatch = upd.Parameters.Add("$match_id", SqliteType.Text);
            var pName = upd.Parameters.Add("$name", SqliteType.Text);
            var pD = upd.Parameters.Add("$dealt", SqliteType.Integer);
            var pT = upd.Parameters.Add("$taken", SqliteType.Integer);
            var pA = upd.Parameters.Add("$amp", SqliteType.Integer);
            var pM = upd.Parameters.Add("$mit", SqliteType.Integer);
            pMatch.Value = matchId;

            var allNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in rawDealt.Keys) allNames.Add(k);
            foreach (var k in rawTaken.Keys) allNames.Add(k);

            foreach (var name in allNames) {
                pName.Value = name;
                pD.Value = (long)Math.Round(rawDealt.GetValueOrDefault(name, 0));
                pT.Value = (long)Math.Round(rawTaken.GetValueOrDefault(name, 0));
                pA.Value = (long)Math.Round(ampEnhanced.GetValueOrDefault(name, 0));
                pM.Value = (long)Math.Round(preventedByMit.GetValueOrDefault(name, 0));
                upd.ExecuteNonQuery();
            }
            tx2.Commit();
        } finally {
            _writeLock.Release();
        }
    }

    /// <summary>Read-only DTO for the Phase B "Advanced" tab in the match detail UI.</summary>
    internal sealed class CCAdvancedRow {
        public string Name = "";
        public string? Job;
        public string? Team;
        public long? DamageDealtLog;
        public long? DamageTakenLog;
        public long? HealDealtLog;
        public long? ZeroDamageHitsDealt;
        public long? ZeroDamageHitsTaken;
        public long? ShieldsAppliedCount;
        public double? ShieldUptimeSeconds;
        public long? ShieldedHitsTaken;
        public long? ShieldedHitsCausedOthers;
        public long? DamageDealtRawLog;
        public long? DamageTakenRawLog;
        public long? DamageDealtAmpAdded;
        public long? DamageTakenMitAvoided;
        public long? ShieldingDoneLog;
        public long? ShieldingDamageMitigatedLog;
        public string? RolledUpAtUtc;
    }

    /// <summary>
    /// Read the Phase B advanced metrics for one CC match's players. Used by the
    /// in-game match detail window's Advanced tab. Returns rows in player_idx
    /// order; missing matches return an empty list.
    /// </summary>
    internal List<CCAdvancedRow> GetCCAdvancedRows(string matchId) {
        var result = new List<CCAdvancedRow>();
        if (string.IsNullOrEmpty(matchId)) return result;
        try {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT name, job, team,
    damage_dealt_log, damage_taken_log, heal_dealt_log,
    zero_damage_hits_dealt, zero_damage_hits_taken,
    shields_applied_count, shield_uptime_seconds,
    shielded_hits_taken, shielded_hits_caused_others,
    damage_dealt_raw_log, damage_taken_raw_log,
    damage_dealt_amp_added, damage_taken_mit_avoided,
    shielding_done_log, shielding_damage_mitigated_log,
    rolled_up_at_utc
FROM match_players
WHERE match_id = $id
ORDER BY player_idx;";
            cmd.Parameters.AddWithValue("$id", matchId);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) {
                result.Add(new CCAdvancedRow {
                    Name = rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                    Job = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    Team = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    DamageDealtLog = rdr.IsDBNull(3) ? null : rdr.GetInt64(3),
                    DamageTakenLog = rdr.IsDBNull(4) ? null : rdr.GetInt64(4),
                    HealDealtLog = rdr.IsDBNull(5) ? null : rdr.GetInt64(5),
                    ZeroDamageHitsDealt = rdr.IsDBNull(6) ? null : rdr.GetInt64(6),
                    ZeroDamageHitsTaken = rdr.IsDBNull(7) ? null : rdr.GetInt64(7),
                    ShieldsAppliedCount = rdr.IsDBNull(8) ? null : rdr.GetInt64(8),
                    ShieldUptimeSeconds = rdr.IsDBNull(9) ? null : rdr.GetDouble(9),
                    ShieldedHitsTaken = rdr.IsDBNull(10) ? null : rdr.GetInt64(10),
                    ShieldedHitsCausedOthers = rdr.IsDBNull(11) ? null : rdr.GetInt64(11),
                    DamageDealtRawLog = rdr.IsDBNull(12) ? null : rdr.GetInt64(12),
                    DamageTakenRawLog = rdr.IsDBNull(13) ? null : rdr.GetInt64(13),
                    DamageDealtAmpAdded = rdr.IsDBNull(14) ? null : rdr.GetInt64(14),
                    DamageTakenMitAvoided = rdr.IsDBNull(15) ? null : rdr.GetInt64(15),
                    ShieldingDoneLog = rdr.IsDBNull(16) ? null : rdr.GetInt64(16),
                    ShieldingDamageMitigatedLog = rdr.IsDBNull(17) ? null : rdr.GetInt64(17),
                    RolledUpAtUtc = rdr.IsDBNull(18) ? null : rdr.GetString(18),
                });
            }
        } catch (Exception ex) {
            _plugin.Log.Warning(ex, $"GetCCAdvancedRows failed for match {matchId}.");
        }
        return result;
    }

    /// <summary>
    /// Re-run the rollup for every completed CC match. Useful after schema/algorithm changes.
    /// Returns count of matches processed.
    /// </summary>
    internal async Task<int> RollupAllCCMatchesAsync() {
        var matchIds = new List<string>();
        try {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM matches WHERE mode = 'cc' AND is_completed = 1 AND is_deleted = 0;";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) matchIds.Add(rdr.GetString(0));
        } catch (Exception ex) {
            _plugin.Log.Error(ex, "Failed to enumerate matches for rollup.");
            return 0;
        }
        var done = 0;
        foreach (var id in matchIds) {
            await RollupCCMatchAsync(id);
            done++;
        }
        return done;
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
    p.effective_shielding, p.effective_mitigation, p.overheal,
    p.events_involving_player, p.buff_param_applied_sum, p.buff_apply_count, p.buff_remove_count,
    p.deaths_caused, p.deaths_suffered_in_log,
    p.damage_dealt_log, p.damage_taken_log, p.heal_dealt_log,
    p.zero_damage_hits_dealt, p.zero_damage_hits_taken,
    p.shields_applied_count, p.shield_uptime_seconds,
    p.shielded_hits_taken, p.shielded_hits_caused_others,
    p.damage_dealt_raw_log, p.damage_taken_raw_log,
    p.damage_dealt_amp_added, p.damage_taken_mit_avoided,
    p.shielding_done_log, p.shielding_damage_mitigated_log,
    p.rolled_up_at_utc
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
