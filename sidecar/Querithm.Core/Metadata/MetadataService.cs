using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Sessions;
using Querithm.Core.Store;

namespace Querithm.Core.Metadata;

/// <summary>
/// In-memory snapshots for tier-2 intelligence; reloaded from SQLite after each refresh. Phase D0d: a session-owned
/// session also has a view of its own in <see cref="Ephemeral"/> (an in-memory SQLite store, never on disk): the whole
/// database for an in-memory one, its temporary objects otherwise, laid over the shared snapshot.
/// </summary>
public sealed class SchemaCache(MetadataStore store, MetadataStore? ephemeral = null) : ISchemaSource
{
    readonly ConcurrentDictionary<string, SchemaSnapshot> _snapshots = new();
    readonly ConcurrentDictionary<string, SchemaSnapshot> _session = new();

    /// <summary>
    /// Bumped by every invalidation. A fill loads outside any lock, so a refresh can apply and invalidate between its
    /// load and its store; the fill then caches nothing (its snapshot may predate the refresh, and the status is already
    /// Fresh, so nothing would read it again) and the next read loads again.
    /// </summary>
    long _epoch;
    readonly object _fillGate = new();

    /// <summary>Tests only: runs after a snapshot was loaded from SQLite and before it is cached.</summary>
    internal Action<string>? AfterLoad { get; set; }

    SchemaSnapshot Fill(ConcurrentDictionary<string, SchemaSnapshot> map, string key, Func<SchemaSnapshot> load)
    {
        if (map.TryGetValue(key, out var hit)) return hit;
        var epoch = Volatile.Read(ref _epoch);
        var loaded = load();
        AfterLoad?.Invoke(key);
        lock (_fillGate)
            return _epoch == epoch ? map.GetOrAdd(key, loaded) : loaded;
    }

    void Drop(Action remove)
    {
        lock (_fillGate)
        {
            _epoch++;
            remove();
        }
    }
    readonly ConcurrentDictionary<string, (string ProfileId, bool InMemory)> _scopes = new();

    /// <summary>Session views (in-memory SQLite, dropped when the session closes).</summary>
    public MetadataStore Ephemeral { get; } = ephemeral ?? new MetadataStore(LocalStore.InMemory());

    static string Key(string p, string d) => $"{p}|{d}"; // Phase D0e: exact names, never merged

    /// <summary>The storage key of a session's own view of a database (only ever in <see cref="Ephemeral"/>).</summary>
    public static string SessionDatabase(string database, string sessionId) => $"{database}\u001fsession:{sessionId}";

    public SchemaSnapshot? Get(string? profileId, string? database)
    {
        if (profileId == null || database == null) return null;
        return Fill(_snapshots, Key(profileId, database), () => store.Load(profileId, database));
    }

    /// <summary>
    /// The session's view: an in-memory database's own snapshot, or the shared one with the session's temporary objects
    /// added. A session that is not registered, or belongs to another profile, sees the shared snapshot only.
    /// </summary>
    public SchemaSnapshot? Get(string? profileId, string? database, string? sessionId)
    {
        if (profileId == null || database == null) return null;
        if (sessionId == null || !_scopes.TryGetValue(sessionId, out var scope) || scope.ProfileId != profileId) return Get(profileId, database);
        var own = SessionView(profileId, database, sessionId);
        var shared = scope.InMemory ? null : Get(profileId, database)!;
        if (shared != null && own.Objects.Length == 0) return shared;
        // The same instance while neither input changed: tier 2 caches its schema index per snapshot instance
        // (PgSchema.For), so a new record per request rebuilt the index on every keystroke for SQLite / DuckDB tabs.
        var key = Key(profileId, SessionDatabase(database, sessionId));
        if (_merged.TryGetValue(key, out var m) && ReferenceEquals(m.Own, own) && ReferenceEquals(m.Shared, shared)) return m.Merged;
        var merged = shared == null
            ? own with { Database = database, SessionId = sessionId }
            : shared with { Objects = [.. shared.Objects, .. own.Objects], Fks = [.. shared.Fks, .. own.Fks], SessionId = sessionId };
        _merged[key] = (shared, own, merged);
        return merged;
    }

    /// <summary>A session's merged view, kept while the shared snapshot and the session's own are the same instances.</summary>
    readonly ConcurrentDictionary<string, (SchemaSnapshot? Shared, SchemaSnapshot Own, SchemaSnapshot Merged)> _merged = new();

    public SchemaSnapshot SessionView(string profileId, string database, string sessionId)
    {
        var db = SessionDatabase(database, sessionId);
        return Fill(_session, Key(profileId, db), () => Ephemeral.Load(profileId, db));
    }

    public bool IsScoped(string? sessionId, out bool inMemory)
    {
        inMemory = false;
        if (sessionId == null || !_scopes.TryGetValue(sessionId, out var s)) return false;
        inMemory = s.InMemory;
        return true;
    }

    public void AddScope(string sessionId, string profileId, bool inMemory) => _scopes[sessionId] = (profileId, inMemory);

    /// <summary>Drops a closed session's views (memory and disk caches).</summary>
    public void RemoveScope(string sessionId, string profileId)
    {
        _scopes.TryRemove(sessionId, out _);
        var marker = $"\u001fsession:{sessionId}";
        Drop(() =>
        {
            foreach (var key in _session.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal) && k.EndsWith(marker, StringComparison.Ordinal)).ToList())
                _session.TryRemove(key, out _);
            foreach (var key in _merged.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal) && k.EndsWith(marker, StringComparison.Ordinal)).ToList())
                _merged.TryRemove(key, out _);
        });
        foreach (var db in Ephemeral.KnownDatabases(profileId).Where(d => d.EndsWith(marker, StringComparison.Ordinal))) Ephemeral.Forget(profileId, db);
    }

    public IReadOnlyList<string> Databases(string? profileId) => profileId == null ? [] : store.KnownDatabases(profileId);

    public void Invalidate(string profileId, string database) => Drop(() => _snapshots.TryRemove(Key(profileId, database), out _));

    public void InvalidateSession(string profileId, string database, string sessionId) => Drop(() => _session.TryRemove(Key(profileId, SessionDatabase(database, sessionId)), out _));

    public void InvalidateProfile(string profileId) => Drop(() =>
    {
        foreach (var key in _snapshots.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal)).ToList()) _snapshots.TryRemove(key, out _);
        foreach (var key in _session.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal)).ToList()) _session.TryRemove(key, out _);
        foreach (var key in _merged.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal)).ToList()) _merged.TryRemove(key, out _);
    });
}

/// <summary>A schema source fixed to one session's view (Phase D0d), for the dialect services.</summary>
public sealed class ScopedSchemaSource(SchemaCache cache, string? sessionId) : ISchemaSource
{
    public SchemaSnapshot? Get(string? profileId, string? database) => cache.Get(profileId, database, sessionId);
    public IReadOnlyList<string> Databases(string? profileId) => cache.Databases(profileId);
}

/// <summary>MetadataRefresher (§8): stamps diff, chunked detail fetch, one SQLite transaction, meta.updated.</summary>
public sealed class MetadataService
{
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    readonly SessionManager _sessions;
    readonly MetadataStore _store;
    readonly SchemaCache _cache;
    readonly INotifier _notifier;
    readonly ILogger _log;
    readonly ConcurrentDictionary<string, (string JobId, Task Task)> _running = new();
    readonly ConcurrentDictionary<string, byte> _refreshedThisLaunch = new();
    /// <summary>Per profile, bumped by <see cref="ForgetConnection"/>: a refresh begun under an older value writes nothing.</summary>
    readonly ConcurrentDictionary<string, long> _generation = new();
    /// <summary>Held while a refresh publishes into the shared store and while <see cref="ForgetConnection"/> clears it.</summary>
    readonly object _publish = new();

    long GenerationOf(string profileId) => _generation.TryGetValue(profileId, out var g) ? g : 0;

    public MetadataService(SessionManager sessions, MetadataStore store, SchemaCache cache, INotifier notifier, ILogger log)
    {
        _sessions = sessions;
        _store = store;
        _cache = cache;
        _notifier = notifier;
        _log = log;
        // No refresh of this process can be running yet: a row still 'refreshing' was left by one that never finished.
        try
        {
            if (_store.ResetInterruptedRefreshes() is > 0 and var n) _log.LogInformation("Metadata: {Count} interrupted refresh(es) from the last run marked stale", n);
        }
        catch (Exception ex) { _log.LogWarning("Metadata: could not reset interrupted refreshes: {Message}", ex.Message); }
    }

    public SchemaCache Cache => _cache;
    public MetadataStore Store => _store;

    static string Key(string p, string d) => $"{p}|{d}"; // Phase D0e: exact names, never merged

    /// <summary>
    /// Phase D0d: the session a scoped request names must exist and belong to the profile; null = no session view (a
    /// pooled session needs none: its objects are all in the shared snapshot).
    /// </summary>
    Session? ScopeOf(string profileId, string? sessionId)
    {
        if (sessionId == null) return null;
        var s = _sessions.All.FirstOrDefault(x => x.Id == sessionId && !x.Closed)
                ?? throw QuerithmException.NotFound("Session", sessionId.Length > 64 ? sessionId[..64] : sessionId);
        if (s.Profile.Id != profileId) throw QuerithmException.Invalid("The session belongs to another connection.");
        return s.SessionOwned ? s : null;
    }

    /// <summary>Cached rows immediately; stale (older than 10 min or not refreshed since launch) triggers a background refresh.</summary>
    public SchemaSnapshot Snapshot(SnapshotParams p)
    {
        var scope = ScopeOf(p.ProfileId, p.SessionId);
        if (scope is { InMemory: true }) return SessionSnapshot(scope, p.Database);
        // An in-memory profile has no shared snapshot: only a session holds its database.
        if (scope == null && _sessions.TryGetProfile(p.ProfileId) is { } mem && ProfileRules.LocationOf(mem) == LocationKind.Memory)
            return new SchemaSnapshot { ProfileId = p.ProfileId, Database = p.Database, Status = SnapshotStatus.Empty, Objects = [], Fks = [] };
        // Objects come from the in-memory copy of the SQLite cache (reloaded after each refresh); status is read fresh.
        var row = _store.GetSnapshotRow(p.ProfileId, p.Database);
        var cached = (scope != null ? _cache.Get(p.ProfileId, p.Database, scope.Id) : _cache.Get(p.ProfileId, p.Database)) ?? _store.Load(p.ProfileId, p.Database);
        var snap = cached with
        {
            Status = row?.Status ?? SnapshotStatus.Empty, RefreshedAt = row?.RefreshedAt, ServerVersion = row?.ServerVersion, Error = row?.Error, Notes = row?.Notes,
        };
        var stale = snap.Status == SnapshotStatus.Empty
                    || !_refreshedThisLaunch.ContainsKey(Key(p.ProfileId, p.Database))
                    || snap.RefreshedAt is null
                    || Time.NowMs() - snap.RefreshedAt > StaleAfter.TotalMilliseconds;
        // Readiness is auth-aware (Phase D0a): a Windows or Kerberos profile refreshes before its first open and after a
        // sidecar restart; a memory database is never refreshed by a helper that would open a new, empty one.
        if (stale && snap.Status != SnapshotStatus.Refreshing && _sessions.TryGetProfile(p.ProfileId) is { } profile && (scope != null || _sessions.CanOpen(profile)))
        {
            Refresh(new RefreshParams { ProfileId = p.ProfileId, Database = p.Database, Full = snap.Status == SnapshotStatus.Empty, SessionId = scope?.Id });
            if (snap.Status == SnapshotStatus.Fresh) snap = snap with { Status = SnapshotStatus.Stale };
        }
        return snap;
    }

    /// <summary>An in-memory database's snapshot: the session's alone, refreshed through its own connection.</summary>
    SchemaSnapshot SessionSnapshot(Session s, string database)
    {
        var db = SchemaCache.SessionDatabase(database, s.Id);
        var row = _cache.Ephemeral.GetSnapshotRow(s.Profile.Id, db);
        var snap = _cache.SessionView(s.Profile.Id, database, s.Id) with
        {
            Database = database, SessionId = s.Id,
            Status = row?.Status ?? SnapshotStatus.Empty, RefreshedAt = row?.RefreshedAt, ServerVersion = row?.ServerVersion, Error = row?.Error, Notes = row?.Notes,
        };
        // Nothing to read before the tab's first execution: refreshing would open the database just to find it empty.
        if (snap.Status is SnapshotStatus.Empty or SnapshotStatus.Stale && s.Connected && !s.MemoryLost)
            Refresh(new RefreshParams { ProfileId = s.Profile.Id, Database = database, Full = snap.Status == SnapshotStatus.Empty, SessionId = s.Id });
        return snap;
    }

    readonly ConcurrentDictionary<string, RefreshParams> _again = new();
    /// <summary>Keys with a refresh chain running; changed only under the _again lock, together with the queue.</summary>
    readonly HashSet<string> _active = [];
    /// <summary>When each key's current read began (Stopwatch timestamp); changed only under the _again lock.</summary>
    readonly Dictionary<string, long> _readStarted = [];

    /// <summary>Keys whose current read is a full one (a request for a full read is not covered by an incremental one).</summary>
    readonly HashSet<string> _lastFull = [];

    /// <summary>Records the start of a key's next read. Called under the _again lock.</summary>
    void Began(string key, RefreshParams p)
    {
        _readStarted[key] = Stopwatch.GetTimestamp();
        if (p.Full) _lastFull.Add(key); else _lastFull.Remove(key);
    }

    static string JobKey(RefreshParams p) => p.SessionId == null ? Key(p.ProfileId, p.Database) : $"{Key(p.ProfileId, p.Database)}|s:{p.SessionId}";

    /// <summary>
    /// Starts a refresh, or — while one runs — queues exactly one more after it: the running one may have read the
    /// catalog before the change that asked for this one (DDL right after DDL), so it cannot stand in for it.
    /// </summary>
    public JobRef Refresh(RefreshParams p) => Refresh(p, null);

    /// <param name="changedAt">
    /// When the change that asks for this read happened (Stopwatch timestamp): a read that began after it already sees it,
    /// so none is queued behind it. After a run with DDL both its own refresh and its edit recheck asked, and the second
    /// was a whole catalog read more for nothing.
    /// </param>
    internal JobRef Refresh(RefreshParams p, long? changedAt)
    {
        if (p.SessionId != null && ScopeOf(p.ProfileId, p.SessionId) == null) p = p with { SessionId = null }; // a pooled session has no view of its own
        var key = JobKey(p);
        lock (_again)
        {
            if (_active.Contains(key) && _running.TryGetValue(key, out var existing))
            {
                var current = _readStarted.TryGetValue(key, out var began) && changedAt is { } at && began > at && !(p.Full && !_lastFull.Contains(key));
                if (!current) _again.AddOrUpdate(key, p, (_, q) => q with { Full = q.Full || p.Full });
                return new JobRef { JobId = existing.JobId };
            }
            _active.Add(key);
            Began(key, p);
            var jobId = Ulid.NewId();
            var task = Task.Run(() => RunChainAsync(p, key));
            _running[key] = (jobId, task);
            return new JobRef { JobId = jobId };
        }
    }

    async Task RunChainAsync(RefreshParams p, string key)
    {
        var next = p;
        while (true)
        {
            try { await RunAsync(next, key).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning("Metadata refresh failed for {Key}: {Message}", key, ex.Message); }
            lock (_again)
            {
                if (_again.TryRemove(key, out next!)) { Began(key, next); continue; }
                _active.Remove(key);
                _readStarted.Remove(key);
                _lastFull.Remove(key);
                return;
            }
        }
    }

    /// <summary>Phase D0d: a refresh deferred while its session's transaction was open runs now (the transaction ended).</summary>
    public void OnTransactionEnded(Session s)
    {
        if (!s.RefreshDeferred || s.Closed || s.Database == null) return;
        s.RefreshDeferred = false;
        Refresh(new RefreshParams { ProfileId = s.Profile.Id, Database = s.Database, SessionId = s.Id });
    }

    /// <summary>Phase D0d: a closed session's own view goes with it; a refresh still running for it writes nothing.</summary>
    public void ForgetSession(Session s) => _cache.RemoveScope(s.Id, s.Profile.Id);

    async Task RunAsync(RefreshParams p, string key)
    {
        var sw = Stopwatch.StartNew();
        // Taken before the profile is read: a forget from here on makes what this reads the old connection's.
        var generation = GenerationOf(p.ProfileId);
        var profile = _sessions.GetProfile(p.ProfileId);
        var provider = _sessions.Providers.Get(profile.Engine);
        var session = p.SessionId != null ? _sessions.All.FirstOrDefault(x => x.Id == p.SessionId) : null;
        if (p.SessionId != null && session is not { Closed: false }) return; // closed since: nothing to read, nothing to report
        var owned = provider.LifetimeFor(profile) == ConnectionLifetime.SessionOwned;
        var inMemory = session?.InMemory == true;
        if (owned && session == null && ProfileRules.LocationOf(profile) == LocationKind.Memory) return; // only its tab can read it
        // The store that takes the objects every tab sees: the shared one, or an in-memory database's own view.
        var mainStore = inMemory ? _cache.Ephemeral : _store;
        var mainDb = inMemory ? SchemaCache.SessionDatabase(p.Database, session!.Id) : p.Database;
        // Metadata read inside the owner's transaction is that transaction's: never published (Phase D0d). Marked stale,
        // and read again once the transaction ends.
        if (session is { SessionOwned: true } && session.TxState != TxState.None)
        {
            session.RefreshDeferred = true;
            // Refreshing too: a refresh still running would otherwise end Fresh over this transaction's pending DDL.
            if (mainStore.GetSnapshotRow(p.ProfileId, mainDb)?.Status is SnapshotStatus.Fresh or SnapshotStatus.Refreshing)
                mainStore.SetStatus(p.ProfileId, mainDb, SnapshotStatus.Stale);
            _notifier.Notify("meta.updated", new MetaUpdatedEvent { ProfileId = p.ProfileId, Database = p.Database, Status = SnapshotStatus.Stale, SessionId = inMemory ? session.Id : null });
            return;
        }
        mainStore.SetStatus(p.ProfileId, mainDb, SnapshotStatus.Refreshing);
        _notifier.Notify("meta.updated", new MetaUpdatedEvent { ProfileId = p.ProfileId, Database = p.Database, Status = SnapshotStatus.Refreshing, SessionId = inMemory ? session!.Id : null });
        PhysicalConnection? conn = null;
        SessionManager.SessionLease? lease = null;
        System.Data.Common.DbConnection? dedicated = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var ct = cts.Token;
            System.Data.Common.DbConnection db;
            if (session is { SessionOwned: true })
            {
                // The session's own connection, after its execution (Phase D0d: helpers wait for the execution lock).
                // Closing the tab cancels the reads, so the close is never left waiting on them.
                lease = await _sessions.LeaseAsync(session, cts.Token).ConfigureAwait(false);
                db = lease.Conn.Db;
                ct = lease.Token;
            }
            else if (owned)
            {
                // A file database without a tab: one short connection of its own, never a pooled one.
                dedicated = await _sessions.OpenDedicatedAsync(profile, provider, p.Database, cts.Token, Monitoring.ConnectionRegistry.Kinds.Dedicated).ConfigureAwait(false);
                db = dedicated;
            }
            else
            {
                conn = await _sessions.PoolFor(profile, provider, p.Database).BorrowAsync(p.Database, null, cts.Token).ConfigureAwait(false);
                db = conn.Db;
            }
            var stamps = await provider.Metadata.GetStampsAsync(db, ct).ConfigureAwait(false);
            // A connection's temporary objects belong to its session's view only; without a session there is nobody to show them to.
            var local = !inMemory && session != null ? stamps.Where(x => x.SessionLocal).ToList() : [];
            var main = inMemory ? stamps.ToList() : stamps.Where(x => !x.SessionLocal).ToList();
            var (mainChanged, mainRemoved) = Diff(mainStore.GetMarkers(p.ProfileId, mainDb), main, p.Full);
            var localDb = session != null ? SchemaCache.SessionDatabase(p.Database, session.Id) : null;
            var (localChanged, localRemoved) = localDb != null && !inMemory ? Diff(_cache.Ephemeral.GetMarkers(p.ProfileId, localDb), local, p.Full) : ([], []);
            var wanted = mainChanged.Concat(localChanged).Select(c => c.Id).ToList();
            var details = wanted.Count == 0 ? new MetadataBundle([], [], [], []) : await provider.Metadata.GetDetailsAsync(db, wanted, ct).ConfigureAwait(false);
            // What the login could not read (Phase D4, Oracle): kept with the snapshot and shown, instead of failing the refresh.
            var notes = provider.Metadata.Notes(db).ToArray();
            var version = provider.ServerVersion(db);
            if (lease != null) { await lease.DisposeAsync().ConfigureAwait(false); lease = null; }
            if (dedicated != null) { await dedicated.DisposeAsync().ConfigureAwait(false); dedicated = null; }
            if (conn != null) { var done = conn; conn = null; await done.Pool.ReturnAsync(done).ConfigureAwait(false); }

            // A session closed while this ran keeps nothing in memory (its view was dropped with it).
            if (session is { Closed: true }) { Interrupted(p, mainStore, mainDb, inMemory); return; }
            var status = SnapshotStatus.Fresh;
            lock (_publish)
            {
                // The profile's connection was re-pointed while this read the old one: nothing it read is kept.
                if (!inMemory && GenerationOf(p.ProfileId) != generation) { Superseded(p); return; }
                mainStore.ApplyRefresh(p.ProfileId, mainDb, p.Full, mainChanged, mainRemoved, Only(details, mainChanged), version, notes);
                // A tab of this file deferred its refresh while this one ran (DDL inside its transaction): the snapshot stays
                // Stale until that refresh runs, instead of this one calling it Fresh.
                if (!inMemory && owned && _sessions.All.Any(x => x.Profile.Id == p.ProfileId && !x.Closed && x.RefreshDeferred))
                {
                    mainStore.SetStatus(p.ProfileId, mainDb, SnapshotStatus.Stale);
                    status = SnapshotStatus.Stale;
                }
                if (inMemory) _cache.InvalidateSession(p.ProfileId, p.Database, session!.Id);
                else _cache.Invalidate(p.ProfileId, p.Database);
                if (localDb != null && !inMemory)
                {
                    _cache.Ephemeral.ApplyRefresh(p.ProfileId, localDb, p.Full, localChanged, localRemoved, Only(details, localChanged), version);
                    _cache.InvalidateSession(p.ProfileId, p.Database, session!.Id);
                }
                if (!inMemory) _refreshedThisLaunch[Key(p.ProfileId, p.Database)] = 0;
            }
            _notifier.Notify("meta.updated", new MetaUpdatedEvent
            {
                ProfileId = p.ProfileId, Database = p.Database, Status = status, Changed = mainChanged.Count + mainRemoved.Count,
                ChangedIds = p.Full ? null : mainChanged.Select(c => c.Id).ToArray(), RemovedIds = p.Full ? null : mainRemoved.ToArray(), Full = p.Full,
                ElapsedMs = sw.Elapsed.TotalMilliseconds, SessionId = inMemory ? session!.Id : null, Notes = notes.Length > 0 ? notes : null,
            });
            if (localChanged.Count + localRemoved.Count > 0)
                _notifier.Notify("meta.updated", new MetaUpdatedEvent
                {
                    ProfileId = p.ProfileId, Database = p.Database, Status = SnapshotStatus.Fresh, Changed = localChanged.Count + localRemoved.Count,
                    ChangedIds = localChanged.Select(c => c.Id).ToArray(), RemovedIds = localRemoved.ToArray(), ElapsedMs = sw.Elapsed.TotalMilliseconds, SessionId = session!.Id,
                });
            _log.LogInformation("Metadata refresh {Profile}/{Db}: {Changed} changed, {Removed} removed in {Ms} ms", p.ProfileId, p.Database, mainChanged.Count, mainRemoved.Count, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            if (lease != null) await lease.DisposeAsync().ConfigureAwait(false);
            if (dedicated != null) await dedicated.DisposeAsync().ConfigureAwait(false);
            if (conn != null) await conn.Pool.ReturnAsync(conn).ConfigureAwait(false);
            _log.LogWarning("Metadata refresh failed for {Profile}/{Db}: {Message}", p.ProfileId, p.Database, Logging.LogScrubber.Scrub(ex.Message));
            if (session is { Closed: true }) { Interrupted(p, mainStore, mainDb, inMemory); return; }
            lock (_publish)
            {
                // The old connection's failure is not the re-pointed profile's.
                if (!inMemory && GenerationOf(p.ProfileId) != generation) { Superseded(p); return; }
                mainStore.SetStatus(p.ProfileId, mainDb, SnapshotStatus.Error, ex.Message);
            }
            _notifier.Notify("meta.updated", new MetaUpdatedEvent
            {
                ProfileId = p.ProfileId, Database = p.Database, Status = SnapshotStatus.Error, Error = Logging.LogScrubber.Scrub(ex.Message), ElapsedMs = sw.Elapsed.TotalMilliseconds,
                SessionId = inMemory ? session!.Id : null,
            });
        }
    }

    /// <summary>
    /// A refresh that ends without a result because its session closed: a file database's shared row must not stay
    /// 'refreshing' — nothing would refresh that database again, on its own or when asked, until the engine restarts.
    /// An in-memory database's row went with its session.
    /// </summary>
    void Interrupted(RefreshParams p, MetadataStore mainStore, string mainDb, bool inMemory)
    {
        if (inMemory) return;
        try
        {
            if (mainStore.GetSnapshotRow(p.ProfileId, mainDb)?.Status != SnapshotStatus.Refreshing) return;
            mainStore.SetStatus(p.ProfileId, mainDb, SnapshotStatus.Stale);
            _notifier.Notify("meta.updated", new MetaUpdatedEvent { ProfileId = p.ProfileId, Database = p.Database, Status = SnapshotStatus.Stale });
        }
        catch (Exception ex) { _log.LogWarning("Metadata refresh of {Profile}/{Db}: could not clear its status: {Message}", p.ProfileId, p.Database, ex.Message); }
    }

    /// <summary>
    /// Reads <paramref name="database"/>'s metadata again for <paramref name="session"/> — its own view where it has one —
    /// and completes when that read (queued behind a running one) is done: null, or why the metadata is still not current.
    /// Grid editing decides a result set again after it (ExecutionService.RefreshMetadata).
    /// </summary>
    public Task<string?> RefreshedAsync(Session session, string database, CancellationToken ct) => RefreshedAsync(session, database, null, ct);

    /// <param name="changedAt">When the change to cover happened (see <see cref="Refresh(RefreshParams, long?)"/>).</param>
    public async Task<string?> RefreshedAsync(Session session, string database, long? changedAt, CancellationToken ct)
    {
        var sessionId = session.SessionOwned ? session.Id : null;
        Refresh(new RefreshParams { ProfileId = session.Profile.Id, Database = database, SessionId = sessionId }, changedAt);
        await WaitAsync(session.Profile.Id, database, sessionId).WaitAsync(ct).ConfigureAwait(false);
        var row = session.InMemory
            ? _cache.Ephemeral.GetSnapshotRow(session.Profile.Id, SchemaCache.SessionDatabase(database, session.Id))
            : _store.GetSnapshotRow(session.Profile.Id, database);
        return row?.Status switch
        {
            SnapshotStatus.Error => Logging.LogScrubber.Scrub(row.Error ?? "the metadata could not be read"),
            SnapshotStatus.Stale when session.RefreshDeferred => "it is read when the open transaction ends (commit or roll back, then run the query again)",
            // A pooled tab's open transaction: the read ran on another connection, which does not see that transaction's
            // uncommitted DDL (PostgreSQL, SQL Server). Not a final answer — so not settled, and the run after the commit
            // checks again — instead of "checked against freshly read metadata".
            _ when !session.SessionOwned && session.TxState != TxState.None =>
                "this tab's transaction is open and another connection does not see its uncommitted changes (commit or roll back, then run the query again)",
            _ => null,
        };
    }

    static (List<ObjectStamp> Changed, List<long> Removed) Diff(Dictionary<long, long> markers, List<ObjectStamp> stamps, bool full)
    {
        if (full) markers = [];
        var seen = new HashSet<long>(stamps.Count);
        var changed = new List<ObjectStamp>();
        foreach (var s in stamps)
        {
            seen.Add(s.Id);
            if (!markers.TryGetValue(s.Id, out var m) || m != s.Marker) changed.Add(s);
        }
        return (changed, markers.Keys.Where(id => !seen.Contains(id)).ToList());
    }

    static MetadataBundle Only(MetadataBundle b, List<ObjectStamp> objects)
    {
        var ids = objects.Select(o => o.Id).ToHashSet();
        return new MetadataBundle(b.Columns.Where(c => ids.Contains(c.ObjectId)).ToList(), b.Params.Where(x => ids.Contains(x.ObjectId)).ToList(),
            b.Indexes.Where(i => ids.Contains(i.ObjectId)).ToList(), b.Fks.Where(f => ids.Contains(f.Parent)).ToList(),
            b.Members?.Where(m => ids.Contains(m.ObjectId)).ToList());
    }

    // ---- scoped reads for meta.objects / meta.objectDetails / meta.search (Phase D0d) ----

    public SnapObject[] Objects(ObjectsParams p)
    {
        var scope = ScopeOf(p.ProfileId, p.SessionId);
        if (scope == null) return _store.Load(p.ProfileId, p.Database, p.Ids).Objects;
        var own = _cache.Ephemeral.Load(p.ProfileId, SchemaCache.SessionDatabase(p.Database, scope.Id), p.Ids).Objects;
        return scope.InMemory ? own : [.. _store.Load(p.ProfileId, p.Database, p.Ids).Objects, .. own];
    }

    public ObjectDetails Details(ObjectDetailsParams p)
    {
        var scope = ScopeOf(p.ProfileId, p.SessionId);
        if (scope == null) return _store.Details(p.ProfileId, p.Database, p.ObjectId);
        var ownDb = SchemaCache.SessionDatabase(p.Database, scope.Id);
        if (scope.InMemory || _cache.Ephemeral.GetMarkers(p.ProfileId, ownDb).ContainsKey(p.ObjectId)) return _cache.Ephemeral.Details(p.ProfileId, ownDb, p.ObjectId);
        return _store.Details(p.ProfileId, p.Database, p.ObjectId);
    }

    public ObjectRef[] Search(SearchParams p)
    {
        var limit = p.Limit ?? 200;
        var scope = ScopeOf(p.ProfileId, p.SessionId);
        if (scope == null) return _store.Search(p.ProfileId, p.Database, p.Text, p.Kinds, limit);
        var own = _cache.Ephemeral.Search(p.ProfileId, SchemaCache.SessionDatabase(p.Database, scope.Id), p.Text, p.Kinds, limit)
            .Select(o => o with { Database = p.Database }).ToArray();
        return scope.InMemory ? own : [.. own, .. _store.Search(p.ProfileId, p.Database, p.Text, p.Kinds, limit).Take(Math.Max(0, limit - own.Length))];
    }

    /// <summary>
    /// The profile's connection now goes elsewhere (another server, file, engine, login or tunnel): everything read
    /// through the old one goes, so the profile shows what a new one would (nothing until it refreshes), never the old
    /// server's objects. A refresh still reading the old connection writes nothing (no wait here: it ends by itself).
    /// Open explorers read the empty snapshot again.
    /// </summary>
    public void ForgetConnection(string profileId)
    {
        string[] databases;
        lock (_publish)
        {
            _generation.AddOrUpdate(profileId, 1, (_, g) => g + 1);
            databases = _store.KnownDatabases(profileId);
            _store.ForgetProfile(profileId);
            foreach (var key in _refreshedThisLaunch.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal)).ToList()) _refreshedThisLaunch.TryRemove(key, out _);
            _cache.InvalidateProfile(profileId);
        }
        foreach (var db in databases)
            _notifier.Notify("meta.updated", new MetaUpdatedEvent { ProfileId = profileId, Database = db, Status = SnapshotStatus.Empty, Full = true });
    }

    /// <summary>
    /// A refresh begun before <see cref="ForgetConnection"/> read the old connection: it keeps nothing, and the row it
    /// marked refreshing goes (its database shows empty, as the forget left it). Called under <see cref="_publish"/>.
    /// </summary>
    void Superseded(RefreshParams p)
    {
        if (_store.GetSnapshotRow(p.ProfileId, p.Database)?.Status == SnapshotStatus.Refreshing) _store.Forget(p.ProfileId, p.Database);
        _notifier.Notify("meta.updated", new MetaUpdatedEvent { ProfileId = p.ProfileId, Database = p.Database, Status = SnapshotStatus.Empty, Full = true });
    }

    /// <summary>
    /// Before a profile is deleted: lets running refreshes finish (so none writes rows after the delete) and drops the
    /// in-memory snapshots and per-launch markers.
    /// </summary>
    public async Task ForgetProfileAsync(string profileId)
    {
        var prefix = profileId + "|";
        foreach (var (key, job) in _running.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            try { await job.Task.ConfigureAwait(false); } catch { /* RunAsync reports its own failures */ }
            _running.TryRemove(key, out _);
        }
        foreach (var key in _refreshedThisLaunch.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _refreshedThisLaunch.TryRemove(key, out _);
        _cache.InvalidateProfile(profileId);
    }

    /// <summary>
    /// The running refresh of the database — of a session-owned session's own view when <paramref name="sessionId"/> has
    /// one (an in-memory database, temporary tables), as <see cref="Refresh"/> scopes it.
    /// </summary>
    public Task WaitAsync(string profileId, string database, string? sessionId = null)
    {
        var p = new RefreshParams { ProfileId = profileId, Database = database, SessionId = sessionId != null && ScopeOf(profileId, sessionId) != null ? sessionId : null };
        return _running.TryGetValue(JobKey(p), out var j) ? j.Task : Task.CompletedTask;
    }
}
