using Microsoft.Extensions.Logging;
using Querithm.Core.Sessions;

namespace Querithm.Core.Metadata;

/// <summary>
/// Fills definition_cache for the views and routines of a snapshot that are not cached yet, so find
/// usages and schema compare start from cached text. Refreshes already drop the rows of changed objects.
/// </summary>
public sealed class DefinitionFetcher(SessionManager sessions, ObjectService objects, MetadataService metadata, ILogger log)
{
    readonly Dictionary<string, Task<int>> _running = new();

    /// <summary>Fetches what is missing for (session's profile, database); concurrent calls share one fetch. Returns how many were stored.</summary>
    public Task<int> EnsureAsync(string sessionId, string database, CancellationToken ct)
    {
        var s = sessions.Get(sessionId);
        var key = $"{s.Profile.Id}\u0001{database}";
        lock (_running)
        {
            if (_running.TryGetValue(key, out var t) && !t.IsCompleted) return t;
            t = FetchAsync(s.Id, s.Profile.Id, database, ct);
            _running[key] = t;
            return t;
        }
    }

    async Task<int> FetchAsync(string sessionId, string profileId, string database, CancellationToken ct)
    {
        await Task.Yield();
        var missing = metadata.Store.MissingDefinitionIds(profileId, database);
        if (missing.Length == 0) return 0;
        var defs = await objects.OnConnectionAsync(sessionId, database,
            c => sessions.Get(sessionId).Provider.Metadata.GetDefinitionsAsync(c, missing, ct), ct).ConfigureAwait(false);
        // Ids the server no longer has are left out; an id it has without text is encrypted and recorded as such.
        metadata.Store.CacheDefinitions(profileId, database, defs);
        log.LogDebug("Cached {Count} definitions for {Database}", defs.Count, database);
        return defs.Count;
    }
}
