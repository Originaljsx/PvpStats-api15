using LiteDB;
using PvpStats.Types.Match;
using PvpStats.Types.Match.Timeline;
using PvpStats.Types.Player;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PvpStats.Services;
internal class StorageService {
    private const string CCTable = "ccmatch";
    private const string FLTable = "flmatch";
    private const string RWTable = "rwmatch";
    private const string CCTimelineTable = "cctimeline";
    private const string FLTimelineTable = "fltimeline";
    private const string RWTimelineTable = "rwtimeline";
    private const string AutoPlayerLinksTable = "playerlinks_auto";
    private const string ManualPlayerLinksTable = "playerlinks_manual";

    private Plugin _plugin;
    private SemaphoreSlim _dbLock = new SemaphoreSlim(1, 1);
    private LiteDatabase Database { get; init; }

    internal StorageService(Plugin plugin, string path) {
        _plugin = plugin;
        Database = OpenOrRecover(path);

        //if(Database.UserVersion <= 0) {
        //    //foreach(var x in GetCCMatches().Find("Teams")) {
        //    //    foreach(var y in x.Teams) {
        //    //        foreach(var z in y.Value.Players) {
        //    //        }
        //    //    }
        //    //}

        //    var x = Database.GetCollection(CCTable);
        //    foreach(var doc in x.FindAll()) {
        //        foreach(var team in doc["Teams"].AsDocument) {
        //        }
        //    }

        //    //Database.UserVersion = 1;
        //}

        //set mapper properties
        BsonMapper.Global.EmptyStringToNull = false;
        BsonMapper.Global.RegisterType<DateTime>(
            serialize: dt => new BsonValue(dt.ToUniversalTime()),
            deserialize: v => v.AsDateTime.ToUniversalTime()
        );

        //BsonMapper.Global.RegisterType(
        //    serialize: key => key.FullName,
        //    deserialize: bson => (PlayerAlias)bson.AsString
        //);

        //create indices
        var ccMatchCollection = GetCCMatches();
        ccMatchCollection.EnsureIndex(m => m.IsCompleted);
        ccMatchCollection.EnsureIndex(m => m.IsDeleted);
        ccMatchCollection.EnsureIndex(m => m.DutyStartTime);
        ccMatchCollection.EnsureIndex(m => m.MatchType);
        ccMatchCollection.EnsureIndex(m => m.Arena);
        ccMatchCollection.EnsureIndex(m => m.IsBookmarked);

        var flMatchCollection = GetFLMatches();
        flMatchCollection.EnsureIndex(m => m.DutyStartTime);

        var rwMatchCollection = GetRWMatches();
        rwMatchCollection.EnsureIndex(m => m.DutyStartTime);
    }

    public void Dispose() {
        Database.Dispose();
    }

    private LiteDatabase OpenOrRecover(string path) {
        try {
            var db = new LiteDatabase(path);
            // Sanity probe — checkpoint the WAL and run a trivial enumeration. If the
            // file is structurally intact but the log is inconsistent, this surfaces it
            // here at construction time rather than later on the first user query.
            db.Checkpoint();
            _ = db.GetCollection(CCTable).Count(LiteDB.Query.All());
            return db;
        } catch (Exception ex) {
            _plugin.Log.Error(ex, $"LiteDB failed to open or sanity-probe at {path}. Backing up and recreating.");
            try {
                var dir = Path.GetDirectoryName(path);
                var stem = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                if (File.Exists(path)) {
                    File.Move(path, Path.Combine(dir!, $"{stem}.{ts}.bak{ext}"));
                }
                var logPath = Path.Combine(dir!, $"{stem}-log{ext}");
                if (File.Exists(logPath)) {
                    File.Move(logPath, Path.Combine(dir!, $"{stem}-log.{ts}.bak{ext}"));
                }
            } catch (Exception backupEx) {
                _plugin.Log.Error(backupEx, "Failed to back up corrupt LiteDB files; deleting outright.");
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                var logPath2 = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-log{Path.GetExtension(path)}");
                try { if (File.Exists(logPath2)) File.Delete(logPath2); } catch { }
            }
            return new LiteDatabase(path);
        }
    }
    internal ILiteCollection<CrystallineConflictMatch> GetCCMatches() {
        return Database.GetCollection<CrystallineConflictMatch>(CCTable);
    }

    internal async Task AddCCMatch(CrystallineConflictMatch match) {
        LogUpdate(match.Id.ToString());
        await WriteToDatabase(() => GetCCMatches().Insert(match));
    }

    internal async Task AddCCMatches(IEnumerable<CrystallineConflictMatch> matches) {
        LogUpdate(null, matches.Count());
        await WriteToDatabase(() => GetCCMatches().Insert(matches.Where(m => m.Id != null)));
    }

    internal async Task UpdateCCMatch(CrystallineConflictMatch match) {
        LogUpdate(match.Id.ToString());
        await WriteToDatabase(() => GetCCMatches().Update(match));
    }

    internal async Task UpdateCCMatches(IEnumerable<CrystallineConflictMatch> matches) {
        LogUpdate(null, matches.Count());
        await WriteToDatabase(() => GetCCMatches().Update(matches.Where(m => m.Id != null)));
    }

    internal ILiteCollection<FrontlineMatch> GetFLMatches() {
        return Database.GetCollection<FrontlineMatch>(FLTable);
    }

    internal async Task AddFLMatch(FrontlineMatch match) {
        LogUpdate(match.Id.ToString());
        await WriteToDatabase(() => GetFLMatches().Insert(match));
    }

    internal async Task AddFLMatches(IEnumerable<FrontlineMatch> matches) {
        LogUpdate(null, matches.Count());
        await WriteToDatabase(() => GetFLMatches().Insert(matches.Where(m => m.Id != null)));
    }

    internal async Task UpdateFLMatch(FrontlineMatch match) {
        LogUpdate(match.Id.ToString());
        await WriteToDatabase(() => GetFLMatches().Update(match));
    }

    internal async Task UpdateFLMatches(IEnumerable<FrontlineMatch> matches) {
        LogUpdate(null, matches.Count());
        await WriteToDatabase(() => GetFLMatches().Update(matches.Where(m => m.Id != null)));
    }

    internal ILiteCollection<RivalWingsMatch> GetRWMatches() {
        return Database.GetCollection<RivalWingsMatch>(RWTable);
    }

    internal async Task AddRWMatch(RivalWingsMatch match) {
        LogUpdate(match.Id.ToString());
        await WriteToDatabase(() => GetRWMatches().Insert(match));
    }

    internal async Task AddRWMatches(IEnumerable<RivalWingsMatch> matches) {
        LogUpdate(null, matches.Count());
        await WriteToDatabase(() => GetRWMatches().Insert(matches.Where(m => m.Id != null)));
    }

    internal async Task UpdateRWMatch(RivalWingsMatch match) {
        LogUpdate(match.Id.ToString());
        await WriteToDatabase(() => GetRWMatches().Update(match));
    }

    internal ILiteCollection<CrystallineConflictMatchTimeline> GetCCTimelines() {
        return Database.GetCollection<CrystallineConflictMatchTimeline>(CCTimelineTable);
    }

    internal async Task AddCCTimeline(CrystallineConflictMatchTimeline timeline) {
        LogUpdate(timeline.Id.ToString());
        await WriteToDatabase(() => GetCCTimelines().Insert(timeline));
    }

    internal async Task UpdateCCTimeline(CrystallineConflictMatchTimeline timeline) {
        LogUpdate(timeline.Id.ToString());
        await WriteToDatabase(() => GetCCTimelines().Update(timeline));
    }

    internal ILiteCollection<FrontlineMatchTimeline> GetFLTimelines() {
        return Database.GetCollection<FrontlineMatchTimeline>(FLTimelineTable);
    }

    internal async Task AddFLTimeline(FrontlineMatchTimeline timeline) {
        LogUpdate(timeline.Id.ToString());
        await WriteToDatabase(() => GetFLTimelines().Insert(timeline));
    }

    internal async Task UpdateFLTimeline(FrontlineMatchTimeline timeline) {
        LogUpdate(timeline.Id.ToString());
        await WriteToDatabase(() => GetFLTimelines().Update(timeline));
    }

    internal ILiteCollection<RivalWingsMatchTimeline> GetRWTimelines() {
        return Database.GetCollection<RivalWingsMatchTimeline>(RWTimelineTable);
    }

    internal async Task AddRWTimeline(RivalWingsMatchTimeline timeline) {
        LogUpdate(timeline.Id.ToString());
        await WriteToDatabase(() => GetRWTimelines().Insert(timeline));
    }

    internal async Task UpdateRWTimeline(RivalWingsMatchTimeline timeline) {
        LogUpdate(timeline.Id.ToString());
        await WriteToDatabase(() => GetRWTimelines().Update(timeline));
    }

    internal async Task UpdateRWMatches(IEnumerable<RivalWingsMatch> matches) {
        LogUpdate(null, matches.Count());
        await WriteToDatabase(() => GetRWMatches().Update(matches.Where(m => m.Id != null)));
    }

    internal ILiteCollection<PlayerAliasLink> GetAutoLinks() {
        return Database.GetCollection<PlayerAliasLink>(AutoPlayerLinksTable);
    }

    internal async Task SetAutoLinks(IEnumerable<PlayerAliasLink> links) {
        LogUpdate(null, links.Count());
        _plugin.Storage.GetAutoLinks().DeleteAll();
        await WriteToDatabase(() => GetAutoLinks().Insert(links.Where(x => x.Id != null)));
    }

    internal ILiteCollection<PlayerAliasLink> GetManualLinks() {
        return Database.GetCollection<PlayerAliasLink>(ManualPlayerLinksTable);
    }

    internal async Task SetManualLinks(IEnumerable<PlayerAliasLink> links) {
        LogUpdate(null, links.Count());
        //kind of hacky
        GetManualLinks().DeleteAll();
        await WriteToDatabase(() => GetManualLinks().Insert(links.Where(x => x.Id != null)));
    }

    private void LogUpdate(string? id = null, int count = 0) {
        var callingMethod = new StackFrame(2, true).GetMethod();
        var writeMethod = new StackFrame(1, true).GetMethod();

        _plugin.Log.Verbose(string.Format("Invoking {0,-25} {2,-30}{3,-30} Caller: {1,-70}",
            writeMethod?.Name, $"{callingMethod?.DeclaringType?.ToString() ?? ""}.{callingMethod?.Name ?? ""}", id != null ? $"ID: {id}" : "", count != 0 ? $"Count: {count}" : ""));
    }

    private async Task WriteToDatabase(Func<object> action) {
        try {
            await _dbLock.WaitAsync();
            action.Invoke();
        } finally {
            _dbLock.Release();
        }
    }
}
