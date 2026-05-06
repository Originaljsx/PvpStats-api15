using LiteDB;
using PvpStats.Types.Match;
using PvpStats.Types.Match.Timeline;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace PvpStats.Services.DataCache;
internal abstract class MatchCacheService<T> where T : PvpMatch {
    protected readonly Plugin Plugin;
    //private readonly StorageService _storage;
    private List<T> _matches = [];

    internal bool CachingEnabled { get; private set; }
    internal ReadOnlyCollection<T> Matches {
        get {
            if(CachingEnabled) {
                return _matches.AsReadOnly();
            } else {
                return SafeGetFromStorage().AsReadOnly();
            }
        }
    }

    private List<T> SafeGetFromStorage() {
        try {
            return GetFromStorage().ToList();
        } catch (Exception ex) {
            Plugin.Log.Error(ex, $"Failed to read {typeof(T).Name} matches from storage; returning empty list. The database may be corrupt — check the {Plugin.PluginInterface.GetPluginConfigDirectory()} directory.");
            return [];
        }
    }

    protected abstract IEnumerable<T> GetFromStorage();
    protected abstract Task AddToStorage(T match);
    protected abstract Task UpdateToStorage(T match);
    protected abstract Task UpdateManyToStorage(IEnumerable<T> matches);
    internal abstract PvpMatchTimeline? GetTimeline(T match);

    internal MatchCacheService(Plugin plugin) {
        Plugin = plugin;
    }

    internal void EnableCaching() {
        RebuildCache();
        CachingEnabled = true;
    }

    internal void DisableCaching() {
        ClearCache();
        CachingEnabled = false;
    }

    private void ClearCache() {
        _matches = [];
    }

    private void RebuildCache() {
        _matches = SafeGetFromStorage();
    }

    internal async Task AddMatch(T match) {
        if(CachingEnabled) {
            _matches.Add(match);
        }
        await AddToStorage(match);
    }

    internal async Task UpdateMatch(T match) {
        if(CachingEnabled && _matches.RemoveAll(x => x.Id == match.Id) > 0) {
            _matches.Add(match);
        }
        await UpdateToStorage(match);
    }

    internal async Task UpdateMatches(IEnumerable<T> matches) {
        if(CachingEnabled) {
            foreach(var match in matches) {
                if(_matches.RemoveAll(x => x.Id == match.Id) > 0) {
                    _matches.Add(match);
                }
            }
        }
        await UpdateManyToStorage(matches);
    }
}
