using PvpStats.Types.Match;
using PvpStats.Types.Match.Timeline;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PvpStats.Services.DataCache;
internal class CCMatchCacheService : MatchCacheService<CrystallineConflictMatch> {

    public CCMatchCacheService(Plugin plugin) : base(plugin) {
    }

    protected override IEnumerable<CrystallineConflictMatch> GetFromStorage() {
        return Plugin.Storage.GetCCMatches().Query().ToList();
    }

    protected override async Task AddToStorage(CrystallineConflictMatch match) {
        await Plugin.Storage.AddCCMatch(match);
        await MirrorToSqlite(match);
    }

    protected override async Task UpdateToStorage(CrystallineConflictMatch match) {
        await Plugin.Storage.UpdateCCMatch(match);
        await MirrorToSqlite(match);
    }

    protected override async Task UpdateManyToStorage(IEnumerable<CrystallineConflictMatch> matches) {
        await Plugin.Storage.UpdateCCMatches(matches);
        foreach (var m in matches) {
            await MirrorToSqlite(m);
        }
    }

    private async Task MirrorToSqlite(CrystallineConflictMatch match) {
        if (Plugin.SqliteStorage == null) return;
        try {
            await Plugin.SqliteStorage.RecordCCMatchAsync(match);
        } catch (System.Exception ex) {
            // SQLite mirroring is best-effort; never let it break the LiteDB write path.
            Plugin.Log.Warning(ex, $"SQLite mirror failed for CC match {match.Id}.");
        }
    }

    internal override CrystallineConflictMatchTimeline? GetTimeline(CrystallineConflictMatch match) {
        if(match.TimelineId == null) return null;
        return Plugin.Storage.GetCCTimelines().Query().Where(x => x.Id.Equals(match.TimelineId)).FirstOrDefault();
    }
}
