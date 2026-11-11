using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Logging;
using Querithm.Core.Store;

namespace Querithm.Core.Sessions;

/// <summary>
/// The providers of this build. One per engine (a second for the same engine is a build error). Phase D merge model: a
/// <paramref name="hidden"/> engine's provider is listed (<see cref="Hidden"/>) but not registered — not offered, not used to
/// read connection strings, and its profiles are unsupported — until the dev setting exposes it.
/// </summary>
public sealed class ProviderRegistry(IEnumerable<IDbProvider> providers, IEnumerable<Engine>? hidden = null)
{
    static readonly IReadOnlyList<IDbProvider> NoHidden = [];
    readonly IReadOnlyList<IDbProvider> _all = providers.ToList();
    readonly HashSet<Engine> _hidden = [.. hidden ?? []];
    Dictionary<Engine, IDbProvider>? _byEngineCache;
    Dictionary<Engine, IDbProvider> _byEngine => _byEngineCache ??= _all.Where(p => !_hidden.Contains(p.Engine)).ToDictionary(p => p.Engine);

    /// <summary>Providers present in the build but not exposed.</summary>
    public IReadOnlyList<IDbProvider> Hidden => _hidden.Count == 0 ? NoHidden : _all.Where(p => _hidden.Contains(p.Engine)).ToList();

    /// <summary>
    /// Which engines stay hidden: every one in <paramref name="behindFlag"/> unless the dev setting lists it
    /// (<c>QUERITHM_DEV_ENGINES</c> or the <c>dev.engines</c> setting, comma-separated engine names).
    /// </summary>
    public static Engine[] HiddenEngines(IEnumerable<Engine> behindFlag, string? exposedList)
    {
        var exposed = (exposedList ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Store.EnumText.TryParse<Engine>(t, out var e) ? e : (Engine?)null).OfType<Engine>().ToHashSet();
        return behindFlag.Where(e => !exposed.Contains(e)).ToArray();
    }

    public IDbProvider Get(Engine engine) =>
        _byEngine.TryGetValue(engine, out var p) ? p : throw new QuerithmException("unsupportedProfile", ProfileRules.Unavailable(engine));

    public IDbProvider? TryGet(Engine engine) => _byEngine.GetValueOrDefault(engine);

    public IDbProvider? ForDialect(Dialect dialect) => _byEngine.Values.FirstOrDefault(p => p.Dialect == dialect);

    public IEnumerable<IDbProvider> All => _byEngine.Values;
}

/// <summary>Owns profiles' in-memory secrets, pools and sessions (§6).</summary>
public sealed class SessionManager : IAsyncDisposable
{
    readonly ProfileStore _profiles;
    readonly ProviderRegistry _providers;
    readonly INotifier _notifier;
    readonly ILogger _log;
    readonly ConcurrentDictionary<string, Session> _sessions = new();
    readonly ConcurrentDictionary<string, Pool> _pools = new();
    readonly ConcurrentDictionary<string, string?> _secrets = new();
    readonly ConcurrentDictionary<string, string?> _sshSecrets = new();
    readonly ConcurrentDictionary<string, byte> _prodConfirmed = new();
    readonly Timer _sweeper;

    public int MaxPerProfile { get; set; } = 8;

    /// <summary>Querithm's own server sessions (B1b): filled by every physical open, read by the monitor's kill guard.</summary>
    public Monitoring.ConnectionRegistry Registry { get; } = new();

    /// <summary>SSH tunnels for profiles behind a bastion; null = no tunnelling (tests without a data directory).</summary>
    public Ssh.SshTunnelManager? Tunnels { get; init; }

    public SessionManager(ProfileStore profiles, ProviderRegistry providers, INotifier notifier, ILogger log)
    {
        _profiles = profiles;
        _providers = providers;
        _notifier = notifier;
        _log = log;
        _profiles.Changed += id =>
        {
            Interlocked.Increment(ref _storedEpoch);
            _stored.TryRemove(id, out _);
        };
        // §6: pools live while any session is open + 5 min idle.
        _sweeper = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public ProviderRegistry Providers => _providers;

    public Profile GetProfile(string profileId) => _profiles.Get(profileId) ?? throw QuerithmException.NotFound("Profile", profileId);

    public Profile? TryGetProfile(string profileId) => _profiles.Get(profileId);

    /// <summary>
    /// A connection can be opened now without asking the user (Phase D0a readiness): background metadata refresh, the
    /// monitor and other unattended work check this, never a bare "a secret is remembered". A secret sign-in needs its
    /// secret; Windows/Kerberos need nothing; a browser or device-code sign-in only after the user signed in once in this
    /// sidecar; a file database needs the grant the shell injected; a memory database is reachable only through the session
    /// that owns it, so nothing unattended may open one (it would be a new, empty database).
    /// </summary>
    public bool CanOpen(Profile profile)
    {
        if (profile.Unsupported != null || _providers.TryGet(profile.Engine) == null) return false;
        switch (ProfileRules.LocationOf(profile))
        {
            case LocationKind.Memory: return false;
            case LocationKind.File when !_fileGrants.ContainsKey(profile.Id): return false;
        }
        if (ProfileRules.NeedsSecret(profile)) return HasCredentials(profile.Id);
        // PT10-1: a sign-in counts only for the settings it was made with (host, user, tenant…): an edit asks again.
        return !ProfileRules.Interactive(profile.Auth) || (_signedIn.TryGetValue(profile.Id, out var by) && SameConnectionSettings(by, profile));
    }

    /// <summary>
    /// Profiles whose browser or device-code sign-in succeeded in this sidecar (the driver caches the token), with the
    /// profile it succeeded for.
    /// </summary>
    readonly ConcurrentDictionary<string, Profile> _signedIn = new();

    /// <summary>File grants the shell injected at session.open / profile.test (Phase D0c); in memory only, never stored.</summary>
    readonly ConcurrentDictionary<string, FileGrant> _fileGrants = new();

    /// <summary>
    /// Remembers the grant the shell injected for a file profile (Phase D0c). The path is checked for shape here (absolute,
    /// normalized, no link) and registered with the log scrubber; a changed grant (another mode, a re-picked file) closes the
    /// connections built under the old one.
    /// </summary>
    public void RememberFileGrant(string profileId, FileGrant grant)
    {
        CheckGrantShape(grant);
        LogScrubber.RegisterPath(grant.Path);
        if (_fileGrants.TryGetValue(profileId, out var previous) && previous != grant) ForgetPools(profileId);
        _fileGrants[profileId] = grant;
    }

    public FileGrant? FileGrantFor(string profileId) => _fileGrants.GetValueOrDefault(profileId);

    /// <summary>Forgets the profile's grant and closes its pooled connections (grant.revoke, after its sessions closed).</summary>
    public void ForgetFileGrant(string profileId)
    {
        _fileGrants.TryRemove(profileId, out _);
        ForgetPools(profileId);
    }

    static QuerithmException GrantRefused(string code, string message) => new(code, message);

    /// <summary>The injected path must be what the shell's canonicalization produces: absolute, normalized, not a link.</summary>
    static void CheckGrantShape(FileGrant g)
    {
        var p = g.Path;
        if (string.IsNullOrEmpty(g.GrantId) || string.IsNullOrEmpty(p) || p.Length > 4096 || p.Contains('\0') || !Path.IsPathFullyQualified(p)
            || !string.Equals(Path.GetFullPath(p), p, StringComparison.Ordinal) || !(g.Identity.StartsWith("u:", StringComparison.Ordinal) || g.Identity.StartsWith("w:", StringComparison.Ordinal)))
            throw GrantRefused("fileGrant", "The database file grant is not valid. Pick the file again (Re-pick file…).");
        if (!Enum.IsDefined(g.Mode)) throw GrantRefused("fileGrant", "The database file grant has an unknown access mode. Pick the file again (Re-pick file…).");
    }

    /// <summary>
    /// The file at the grant's path must be the picked one, before the driver touches it and again after it opened it
    /// (closing the gap between the shell's check and the open). Missing → never created; replaced → never opened.
    /// </summary>
    public static void VerifyFile(FileGrant g)
    {
        string? actual;
        try
        {
            if (new FileInfo(g.Path).LinkTarget != null)
                throw GrantRefused("fileMoved", "The database file is no longer at the path it was picked from. Pick it again (Re-pick file…).");
            actual = FileIdentity.Of(g.Path);
        }
        catch (PlatformNotSupportedException ex) { throw GrantRefused("fileIdentity", ex.Message + " File databases cannot be opened safely."); }
        catch (IOException) { actual = null; }
        catch (UnauthorizedAccessException) { actual = null; }
        if (actual == null) throw GrantRefused("fileMissing", "The database file is gone (deleted, moved or on a drive that is not connected). Pick it again (Re-pick file…).");
        if (actual != g.Identity) throw GrantRefused("fileReplaced", "The file at that path is not the one that was picked (it was replaced). Pick it again (Re-pick file…).");
    }

    /// <summary>Raised when a profile's secrets and pools are dropped (disconnect, delete): its monitors stop.</summary>
    public event Action<string>? ProfileForgotten;

    /// <summary>Remembers the secret Rust injected so pooled connections can be opened later.</summary>
    public void RememberSecret(string profileId, string? secret)
    {
        LogScrubber.RegisterSecret(secret);
        var changed = secret != null && _secrets.TryGetValue(profileId, out var previous) && previous != secret;
        if (secret != null || !_secrets.ContainsKey(profileId)) _secrets[profileId] = secret;
        // A changed password must authenticate instead of reusing an old login. Stored first: whatever sees the new
        // credential epoch (ForgetPools) opens with the new secret.
        if (changed) ForgetPools(profileId);
    }

    /// <summary>The SSH password or key passphrase Rust injected for the profile's tunnel.</summary>
    public void RememberSshSecret(string profileId, string? secret)
    {
        LogScrubber.RegisterSecret(secret);
        if (secret != null && _sshSecrets.TryGetValue(profileId, out var previous) && previous != secret) NextCredentialEpoch(profileId);
        if (secret != null || !_sshSecrets.ContainsKey(profileId)) _sshSecrets[profileId] = secret;
    }

    readonly ConcurrentDictionary<string, long> _credentialEpoch = new();

    /// <summary>
    /// Changes whenever the profile's connections are rebuilt (a new secret, a profile edit, a disconnect): background work
    /// muted after the server rejected its sign-in (the blocking check) may try again — never with the same secret.
    /// </summary>
    public long CredentialEpoch(string profileId) => _credentialEpoch.GetValueOrDefault(profileId);

    void NextCredentialEpoch(string profileId) => _credentialEpoch.AddOrUpdate(profileId, 1, (_, v) => v + 1);

    /// <summary>
    /// Opens a physical connection for the profile: directly, or through its SSH tunnel — the provider's
    /// profile is re-pointed at the local forward while the connect window that admits the driver's socket is held.
    /// </summary>
    public async Task<System.Data.Common.DbConnection> OpenConnectionAsync(Profile profile, IDbProvider provider, string? secret, string? sshSecret, string? database,
        CancellationToken ct, Ssh.SshTunnelManager.Tunnel? tunnel = null, string? kind = Monitoring.ConnectionRegistry.Kinds.Pool, FileGrant? file = null)
    {
        var conn = await OpenPhysicalAsync(profile, provider, secret, sshSecret, database, ct, tunnel, file).ConfigureAwait(false);
        if (kind == null || provider.Activity is not { } activity) return conn;
        // B1b: identity before anyone gets the connection. A failed connection (e.g. killed during its registration window)
        // is an ordinary open failure (K9).
        try
        {
            var key = await activity.IdentityAsync(conn, ct).ConfigureAwait(false);
            Registry.Register(conn, key, kind, activity.ConnectionToken(conn), activity.ConnectionToken);
            return conn;
        }
        // The server refused the read itself on a working connection (an explicit DENY, a compatible server without the
        // view): the connection still serves queries, and every kill is refused while it is open (§6 rule 7). Not for the
        // monitor's own connections: the monitor must recognise itself (K6), so it reports the failure instead.
        catch (IdentityUnavailableException ex) when (conn.State == System.Data.ConnectionState.Open
            && kind is Monitoring.ConnectionRegistry.Kinds.Pool or Monitoring.ConnectionRegistry.Kinds.Dedicated)
        {
            Registry.RegisterUnidentified(conn, kind, activity.ConnectionToken(conn), ex.Message);
            _log.LogWarning("Connection for profile {Profile} is not identified ({Reason}); the activity monitor refuses kills while it is open", profile.Id, ex.Message);
            return conn;
        }
        catch
        {
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* already broken */ }
            throw;
        }
    }

    async Task<System.Data.Common.DbConnection> OpenPhysicalAsync(Profile profile, IDbProvider provider, string? secret, string? sshSecret, string? database,
        CancellationToken ct, Ssh.SshTunnelManager.Tunnel? tunnel, FileGrant? file)
    {
        if (ProfileRules.LocationOf(profile) == LocationKind.File)
        {
            // Phase D0c: only the grant the shell injected for this profile, checked around the open.
            file ??= _fileGrants.GetValueOrDefault(profile.Id);
            if (file == null || file.GrantId != profile.File?.GrantId)
                throw GrantRefused("fileGrant", "This connection has no access to its database file. Pick the file again (Re-pick file…).");
            VerifyFile(file);
            var opened = await provider.OpenAsync(profile, null, database, file, ct).ConfigureAwait(false);
            try { VerifyFile(file); }
            catch
            {
                try { await opened.DisposeAsync().ConfigureAwait(false); } catch { /* closing a refused file */ }
                throw;
            }
            return opened;
        }
        var conn = await OpenTransportAsync(profile, provider, secret, sshSecret, database, ct, tunnel).ConfigureAwait(false);
        if (ProfileRules.Interactive(profile.Auth) && !string.IsNullOrEmpty(profile.Id)) _signedIn[profile.Id] = profile;
        return conn;
    }

    async Task<System.Data.Common.DbConnection> OpenTransportAsync(Profile profile, IDbProvider provider, string? secret, string? sshSecret, string? database,
        CancellationToken ct, Ssh.SshTunnelManager.Tunnel? tunnel)
    {
        if (profile.Ssh == null) return await provider.OpenAsync(profile, secret, database, null, ct).ConfigureAwait(false);
        var (host, port, refusal) = provider.TunnelTarget(profile);
        if (refusal != null) throw new QuerithmException("sshTarget", refusal);
        if (tunnel == null)
        {
            if (Tunnels == null) throw new QuerithmException("sshUnavailable", "SSH tunnels are not available in this build.");
            tunnel = await Tunnels.EnsureAsync(profile, sshSecret, host, port, ct).ConfigureAwait(false);
        }
        var (routed, _) = provider.RouteThrough(profile, tunnel.Local.Address.ToString(), tunnel.Local.Port);
        using (tunnel.Expect(provider.TunnelConnectsPerOpen(routed)))
        {
            var conn = await provider.OpenAsync(routed, secret, database, null, ct).ConfigureAwait(false);
            _viaTunnel.AddOrUpdate(conn, tunnel);
            return conn;
        }
    }

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Data.Common.DbConnection, Ssh.SshTunnelManager.Tunnel> _viaTunnel = new();

    /// <summary>False for a connection whose SSH tunnel has closed: the pool drops it instead of failing the next query.</summary>
    public bool TransportAlive(System.Data.Common.DbConnection connection) =>
        !_viaTunnel.TryGetValue(connection, out var t) || t.Alive;

    /// <summary>A connection outside the pools (debugger target/control, monitor) with the remembered secrets.</summary>
    public Task<System.Data.Common.DbConnection> OpenDedicatedAsync(Profile profile, IDbProvider provider, string? database, CancellationToken ct,
        string kind = Monitoring.ConnectionRegistry.Kinds.Dedicated, Func<Profile, Profile>? configure = null) =>
        // Phase D0d: a helper never opens a fresh in-memory database in place of the tab's.
        ProfileRules.LocationOf(profile) == LocationKind.Memory
            ? throw new QuerithmException("memoryDedicated", "An in-memory database is reachable only through the tab that owns it.")
            : OpenRememberedAsync(profile, provider, database, ct, kind, configure);

    Task<System.Data.Common.DbConnection> OpenRememberedAsync(Profile profile, IDbProvider provider, string? database, CancellationToken ct,
        string kind = Monitoring.ConnectionRegistry.Kinds.Pool, Func<Profile, Profile>? configure = null)
    {
        // Capture credentials first, then validate their epoch and endpoint. An edit after this check can only leave
        // us with the old endpoint's old credentials; a queued pool factory can never read a new endpoint's secret.
        var epoch = CredentialEpoch(profile.Id);
        var secret = SecretFor(profile.Id);
        var sshSecret = SshSecretFor(profile.Id);
        profile = Current(profile);
        if (epoch != CredentialEpoch(profile.Id)) throw ProfileChanged();
        if (configure != null) profile = configure(profile);
        return OpenConnectionAsync(profile, provider, secret, sshSecret, database, ct, kind: kind);
    }

    /// <summary>Reads the identity again when the driver reconnected underneath a reused pool connection (O17).</summary>
    /// <summary>
    /// Reads the identity again when the driver reconnected underneath the connection (no I/O otherwise): on reuse (O17)
    /// and right after every execution, the moment a reconnect inside a run can have happened (O2).
    /// </summary>
    public async Task ReidentifyAsync(PhysicalConnection p, CancellationToken ct)
    {
        if (p.Pool.Provider.Activity is not { } activity) return;
        var token = activity.ConnectionToken(p.Db);
        if (Registry.TokenOf(p.Db) == token) return;
        try
        {
            var key = await activity.IdentityAsync(p.Db, ct).ConfigureAwait(false);
            Registry.Register(p.Db, key, Monitoring.ConnectionRegistry.Kinds.Pool, token, activity.ConnectionToken);
        }
        catch (IdentityUnavailableException ex) when (p.Db.State == System.Data.ConnectionState.Open)
        {
            Registry.RegisterUnidentified(p.Db, Monitoring.ConnectionRegistry.Kinds.Pool, token, ex.Message);
        }
        Registry.Attach(p);
    }

    public string? SshSecretFor(string profileId) => _sshSecrets.TryGetValue(profileId, out var s) ? s : null;

    public bool HasCredentials(string profileId) => _secrets.ContainsKey(profileId);

    /// <summary>For dedicated (non-pooled) connections such as the debugger's target/control pair.</summary>
    public string? SecretFor(string profileId) => _secrets.TryGetValue(profileId, out var s) ? s : null;

    public void ForgetProfile(string profileId)
    {
        ForgetCredentials(profileId);
        _fileGrants.TryRemove(profileId, out _);
        ProfileForgotten?.Invoke(profileId);
    }

    /// <summary>Discard credentials bound to a previous endpoint without disturbing pinned transactions or file grants.</summary>
    public void ForgetCredentials(string profileId)
    {
        _secrets.TryRemove(profileId, out _);
        _sshSecrets.TryRemove(profileId, out _);
        _signedIn.TryRemove(profileId, out _);
        ForgetPools(profileId);
    }

    /// <summary>Closes idle pooled connections for a profile; busy ones finish and are dropped on return.</summary>
    public void ForgetPools(string profileId)
    {
        NextCredentialEpoch(profileId);
        foreach (var (key, pool) in _pools.Where(kv => kv.Value.Profile.Id == profileId).ToList())
        {
            _pools.TryRemove(key, out _);
            _ = pool.CloseAllAsync();
        }
        // Settings may have changed (bastion, key): the next connection opens a new tunnel.
        Tunnels?.Close(profileId);
        // Driver state built with the old secret or settings goes too (L19). Hidden engines can hold some as well.
        foreach (var provider in _providers.All.Concat(_providers.Hidden)) provider.ForgetConnections(profileId);
    }

    /// <summary>Raised after a session is registered (Phase D0d: session-owned sessions get a metadata scope).</summary>
    public event Action<Session>? SessionOpened;
    /// <summary>Raised when a close is accepted, before the connection closes: the session's executions are cancelled then.</summary>
    public event Action<Session>? SessionClosing;
    /// <summary>Raised after a session closed: its connection is gone and its session-local metadata is dropped.</summary>
    public event Action<Session>? SessionClosed;
    /// <summary>Raised when a session's transaction ended (commit, rollback, implicit end) — deferred metadata refreshes run then.</summary>
    public event Action<Session>? TransactionEnded;

    /// <summary>
    /// How long a close waits for the session's runs and helpers to let go of its connection before closing it anyway:
    /// longer than the cancel escalation (§7: two grace periods). Settable for tests.
    /// </summary>
    internal TimeSpan CloseWaitLimit { get; set; } = TimeSpan.FromSeconds(15);

    public const string MemoryLostMessage =
        "The in-memory database is gone: its connection closed, and its data went with it. Open a new tab on the connection to start an empty one.";

    /// <summary>Runs once, on the first session.open (startup stays lazy, §15 rule 9).</summary>
    public event Action? FirstSessionOpened;
    int _opened;

    public SessionOpenResult Open(SessionOpenParams p)
    {
        if (Interlocked.Exchange(ref _opened, 1) == 0) FirstSessionOpened?.Invoke();
        var profile = GetProfile(p.ProfileId);
        var provider = _providers.TryGet(profile.Engine);
        if (ProfileRules.Validate(profile, provider) is { } invalid)
            throw new QuerithmException(profile.Unsupported != null || provider == null ? "unsupportedProfile" : "invalidProfile", invalid);
        if (profile.PasswordRetention is "once" or "session" or "duration" && ProfileRules.NeedsSecret(profile) && p.Secret == null)
            throw new QuerithmException("needsSecret", "Enter the password again to open a new connection.");
        if (ProfileRules.LocationOf(profile) == LocationKind.File)
        {
            // The shell injects the profile's own grant; anything else (none, another profile's) is refused here too.
            if (p.FileGrant is not { } grant || grant.GrantId != profile.File?.GrantId)
                throw GrantRefused("fileGrant", "This connection has no access to its database file. Pick the file again (Re-pick file…).");
            RememberFileGrant(profile.Id, grant);
        }
        RememberSecret(profile.Id, p.Secret);
        if (profile.Ssh != null) RememberSshSecret(profile.Id, p.SshSecret);
        var database = p.Database ?? profile.DatabaseDefault ?? provider!.Traits.DefaultDatabase;
        var owned = provider!.LifetimeFor(profile) == ConnectionLifetime.SessionOwned;
        var inMemory = ProfileRules.LocationOf(profile) == LocationKind.Memory;
        // A memory database lives on one connection: a pool would hand every execution a new, empty one.
        if (inMemory && !owned) throw new QuerithmException("invalidProfile", "This engine cannot keep an in-memory database for a tab.");
        var session = new Session(Ulid.NewId(), profile, provider, database) { SessionOwned = owned, InMemory = inMemory };
        _sessions[session.Id] = session;
        SessionOpened?.Invoke(session);
        // §6 rule 1: lazy — nothing is dialed here.
        return new SessionOpenResult { SessionId = session.Id, Database = database, ServerVersion = owned ? null : PoolFor(profile, provider, database).ServerVersion };
    }

    public Session Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) && !s.Closed ? s : throw QuerithmException.NotFound("Session", sessionId);

    public IEnumerable<Session> All => _sessions.Values;

    /// <summary>The refusal for a tab (or a helper working for it) whose profile's connection settings were edited since it opened.</summary>
    public const string ProfileChangedMessage = "The connection settings of this profile changed: reconnect the tab.";

    static QuerithmException ProfileChanged() => new("profileChanged", ProfileChangedMessage);

    /// <summary>
    /// The settings a connection is built from — where it goes, as whom and how (engine, location, file, host, port, sign-in,
    /// user, TLS, SSH, options). Not the name, colour, environment, retention, timeouts or default database: those never send
    /// a connection somewhere else.
    /// </summary>
    public static bool SameConnectionSettings(Profile a, Profile b) =>
        a.Id == b.Id && a.Engine == b.Engine && (a.Location ?? LocationKind.Server) == (b.Location ?? LocationKind.Server) && a.File == b.File
        && a.Host == b.Host && a.Port == b.Port && a.Auth == b.Auth && a.Username == b.Username
        && a.TlsMode == b.TlsMode && a.TrustCert == b.TrustCert && a.CaPath == b.CaPath && a.Ssh == b.Ssh
        && (a.Options ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal).SequenceEqual((b.Options ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal));

    /// <summary>
    /// PT10-1: the profile a pool is built from is the STORED one, never a caller's copy. A copy whose connection settings
    /// differ from the stored profile (a tab opened before a host edit) is refused: its connections would go to the old
    /// server with the current secret, or serve a new tab from the old server. A profile that is not stored (tests, a
    /// deleted profile whose tabs are closing) is used as given.
    /// </summary>
    Profile Current(Profile snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.Id) || Stored(snapshot.Id) is not { } stored) return snapshot;
        return SameConnectionSettings(snapshot, stored) ? stored : throw ProfileChanged();
    }

    /// <summary>
    /// The pool for the profile's current settings. Keyed by the profile's credential epoch (bumped by every
    /// <see cref="ForgetPools"/>: an edit, a new secret, a changed grant), so a pool built before an edit is never found
    /// again under the current key; one created by a borrow that raced the edit is removed here instead of being shared.
    /// Nothing here waits or does I/O.
    /// </summary>
    public Pool PoolFor(Profile profile, IDbProvider provider, string? database)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var epoch = CredentialEpoch(profile.Id);
            var current = Current(profile);
            // Phase D0e: exact names — PostgreSQL's "Sales" and "sales" are two databases and never share a pool.
            var key = provider.DatabaseScopedConnections ? $"{profile.Id}#{epoch}|{database}" : $"{profile.Id}#{epoch}";
            var pool = _pools.GetOrAdd(key, k =>
            {
                var created = new Pool(k, current, provider, provider.DatabaseScopedConnections ? database : null,
                    (db, ct) => OpenRememberedAsync(current, provider, db, ct), MaxPerProfile, _log, TransportAlive,
                    ReidentifyAsync);
                created.Opened += Registry.Attach;
                return created;
            });
            if (CredentialEpoch(profile.Id) == epoch && SameConnectionSettings(pool.Profile, Current(profile))) return pool;
            // An edit or a new secret landed meanwhile: this pool belongs to the old settings (busy connections finish and
            // are dropped on return).
            if (_pools.TryRemove(new KeyValuePair<string, Pool>(key, pool))) _ = pool.CloseAllAsync();
        }
        throw ProfileChanged();
    }

    /// <summary>The pool for a pooled session; refused once its profile's connection settings changed (PT10-1).</summary>
    public Pool PoolFor(Session s) => PoolFor(s.Profile, s.Provider, s.Database);

    /// <summary>
    /// Phase D0d: the one-slot pool behind a session-owned connection, so it goes through the same probing and lifecycle
    /// rules (identity read, no I/O under the lock, discard once) as pooled ones. Removed when the session closes.
    /// </summary>
    Pool PoolForSession(Session s) => _pools.GetOrAdd($"{s.Profile.Id}|session:{s.Id}", k =>
    {
        var pool = new Pool(k, s.Profile, s.Provider, s.Database,
            (db, ct) => OpenRememberedAsync(s.Profile, s.Provider, db, ct), 1, _log, TransportAlive, ReidentifyAsync);
        pool.Opened += Registry.Attach;
        return pool;
    });

    /// <summary>
    /// A unit of work on the session's connection (Phase D0d). For a session-owned connection it holds the session's
    /// execution lock, so helpers (definitions, metadata refresh, compare reads) never run a command beside an execution
    /// and wait for it instead; <paramref name="lockHeld"/> = the caller already holds it. Pooled sessions borrow as before.
    /// Closing the session cancels the wait and <see cref="SessionLease.Token"/>.
    /// </summary>
    public async Task<SessionLease> LeaseAsync(Session s, CancellationToken ct, bool lockHeld = false)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Closing.Token);
        var locked = false;
        try
        {
            if (s.SessionOwned && !lockHeld)
            {
                await s.ExecLock.WaitAsync(linked.Token).ConfigureAwait(false);
                locked = true;
                // Granted as the close began: the close is waiting for this lock to close the connection.
                if (s.Closed) throw QuerithmException.NotFound("Session", s.Id);
            }
            var (conn, borrowed) = await AcquireAsync(s, null, linked.Token).ConfigureAwait(false);
            return new SessionLease(this, s, conn, borrowed, locked, linked);
        }
        catch
        {
            if (locked) s.ExecLock.Release();
            linked.Dispose();
            throw;
        }
    }

    public sealed class SessionLease(SessionManager owner, Session session, PhysicalConnection conn, bool borrowed, bool locked, CancellationTokenSource token) : IAsyncDisposable
    {
        public PhysicalConnection Conn { get; } = conn;

        /// <summary>The caller's token, also cancelled when the session starts closing: use it for the work on <see cref="Conn"/>.</summary>
        public CancellationToken Token { get; } = token.Token;

        public async ValueTask DisposeAsync()
        {
            try { await owner.ReleaseAsync(session, Conn, borrowed).ConfigureAwait(false); }
            finally
            {
                if (locked) session.ExecLock.Release();
                token.Dispose();
            }
        }
    }

    /// <summary>
    /// A pinned connection that closed under its session (Phase D0d): discarded once; an in-memory database is marked lost,
    /// so the next use reports it instead of silently opening an empty one. Returns the message for the tab.
    /// </summary>
    public async Task<string> ConnectionLostAsync(Session s, PhysicalConnection conn)
    {
        if (s.Pinned == conn) s.Pinned = null;
        s.TxState = TxState.None;
        s.TxStartedAt = null;
        await conn.Pool.DiscardAsync(conn).ConfigureAwait(false);
        if (s.InMemory)
        {
            s.MemoryLost = true;
            return MemoryLostMessage;
        }
        return "The connection was lost; any open transaction was rolled back by the server.";
    }

    /// <summary>A connection for one execution: the pinned one, or a borrowed one set to the session database.</summary>
    public async Task<(PhysicalConnection Conn, bool Borrowed)> AcquireAsync(Session s, Action? onQueued, CancellationToken ct)
    {
        if (s.Pinned != null) return (s.Pinned, false);
        if (s.SessionOwned)
        {
            // Phase D0d: opened on first use and pinned for the session's life; never returned to a pool.
            if (s.MemoryLost) throw new QuerithmException("memoryLost", MemoryLostMessage);
            var owned = await PoolForSession(s).BorrowAsync(s.Database, onQueued, ct).ConfigureAwait(false);
            owned.LeasedBy = s.Id;
            MarkConnected(s, owned);
            Pin(s, owned);
            if (s.Closed)
            {
                s.Pinned = null;
                await owned.Pool.DiscardAsync(owned).ConfigureAwait(false);
                throw QuerithmException.NotFound("Session", s.Id);
            }
            return (owned, false);
        }
        // PT10-1: a tab opened before its profile's connection settings were edited gets no new connection: neither to the
        // old server (with the current secret) nor silently to the new one (its transaction and state belong to the old).
        // Its pinned connection (an open transaction) stays usable above, so the user can still commit or roll back.
        if (Stored(s.Profile.Id) is { } stored && !SameConnectionSettings(s.Profile, stored)) throw ProfileChanged();
        if (!HasCredentials(s.Profile.Id) && NeedsSecret(s.Profile))
            throw new QuerithmException("needsSecret", "The connection's credentials are not loaded. Reconnect the profile.");
        var conn = await PoolFor(s).BorrowAsync(s.Database, onQueued, ct).ConfigureAwait(false);
        conn.LeasedBy = s.Id;
        MarkConnected(s, conn);
        return (conn, true);
    }

    static bool NeedsSecret(Profile p) => ProfileRules.NeedsSecret(p);

    void MarkConnected(Session s, PhysicalConnection conn)
    {
        // Phase D0e: the server switched to the database it calls by this name (SQL Server's default collations ignore
        // case), so the session adopts that spelling and later borrows match the connection exactly.
        if (!s.Provider.DatabaseScopedConnections && s.Provider.CurrentDatabase(conn.Db) is { } current && s.Database is { } wanted
            && current != wanted && string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
            s.Database = current;
        s.Connected = true;
        s.ServerVersion ??= s.Provider.ServerVersion(conn.Db);
        s.Product ??= s.Provider.Product(conn.Db);
        s.Database ??= s.Provider.CurrentDatabase(conn.Db);
    }

    /// <summary>
    /// Gives a borrowed connection back (Phase D0b): with <paramref name="known"/> = None after a state read the caller just
    /// did, straight to the pool; otherwise the pool reads the transaction state first and discards a connection left in an
    /// open or unknown transaction instead of handing it to the next borrower.
    /// </summary>
    public Task ReleaseAsync(Session s, PhysicalConnection conn, bool borrowed, TxState? known = null)
    {
        if (!borrowed || s.Pinned == conn) return Task.CompletedTask;
        if (known == TxState.None)
        {
            conn.Pool.Return(conn);
            return Task.CompletedTask;
        }
        return conn.Pool.ReturnAsync(conn);
    }

    public async Task<SessionState> StateAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId);
        if (!s.Connected && s.ActiveExecutionId == null)
        {
            await using var lease = await LeaseAsync(s, ct).ConfigureAwait(false);
            s.Spid = await s.Provider.GetSpidAsync(lease.Conn.Db, ct).ConfigureAwait(false);
        }
        return s.State();
    }

    public async Task<ChangeDatabaseResult> ChangeDatabaseAsync(ChangeDatabaseParams p, CancellationToken ct)
    {
        var s = Get(p.SessionId);
        FeatureGuard.Require(s.Provider, Feature.ChangeDatabase);
        if (s.ActiveExecutionId != null) throw new QuerithmException("busy", "The session is executing a query.");
        var sw = Stopwatch.StartNew();
        if (s.Pinned != null)
        {
            if (s.Provider.DatabaseScopedConnections)
                throw new QuerithmException("txOpen", "Commit or roll back the open transaction before switching database.");
            await s.Provider.ChangeDatabaseAsync(s.Pinned.Db, p.Database, ct).ConfigureAwait(false);
            s.Database = s.Provider.CurrentDatabase(s.Pinned.Db) ?? p.Database;
        }
        else
        {
            var old = s.Database;
            s.Database = p.Database;
            try
            {
                // SQL Server: one USE roundtrip on a pooled connection. PostgreSQL: connects to the new database's pool.
                var (conn, borrowed) = await AcquireAsync(s, null, ct).ConfigureAwait(false);
                s.Database = s.Provider.CurrentDatabase(conn.Db) ?? p.Database;
                await ReleaseAsync(s, conn, borrowed).ConfigureAwait(false);
            }
            catch
            {
                s.Database = old;
                throw;
            }
        }
        _notifier.Notify("session.stateChanged", s.State());
        return new ChangeDatabaseResult { Database = s.Database!, ElapsedMs = sw.Elapsed.TotalMilliseconds };
    }

    public Task CloseAsync(SessionCloseParams p) => CloseAsync(p, lockHeld: false);

    /// <param name="lockHeld">The caller holds the session's execution lock (grant.revoke): nothing else uses its connection.</param>
    public async Task CloseAsync(SessionCloseParams p, bool lockHeld)
    {
        if (!_sessions.TryGetValue(p.SessionId, out var s)) return;
        // An unknown state counts as open (M9): the connection may hold uncommitted work, so only a forced close ends it.
        if (s.TxState != TxState.None && !p.Force)
            throw new QuerithmException("txOpen", s.TxState == TxState.Open
                ? "The session has an open transaction. Commit, roll back, or keep it open."
                : "The session may have an open transaction (its state cannot be read). Commit, roll back, or keep it open.");
        s.Closed = true;
        _sessions.TryRemove(p.SessionId, out _);
        // Whoever uses the connection lets go first: helpers' leases and the session's runs are cancelled (the §7 escalation
        // ends a run), and the close takes the execution lock, so the connection never closes under a command still reading
        // on it — DuckDB on Windows keeps the file locked until that reader goes, and the tab could not open it again.
        try
        {
            s.Closing.Cancel();
            SessionClosing?.Invoke(s);
        }
        catch (Exception ex) { _log.LogWarning("Session {Id}: stopping its work before the close failed ({Message}); the close goes ahead", s.Id, ex.Message); }
        var locked = false;
        if (!lockHeld && (s.Pinned != null || s.SessionOwned))
        {
            locked = await s.ExecLock.WaitAsync(CloseWaitLimit).ConfigureAwait(false);
            if (!locked) _log.LogWarning("Session {Id}: its connection was still in use {Seconds:0} s after the close began; it is closed anyway", s.Id, CloseWaitLimit.TotalSeconds);
        }
        try
        {
            if (s.Pinned is { } pinned)
            {
                s.Pinned = null;
                // A forced close of an open transaction discards the connection; the server rolls back. A session-owned
                // connection always closes with its tab (an in-memory database ends here, as the user asked).
                await pinned.Pool.DiscardAsync(pinned).ConfigureAwait(false);
            }
            if (s.SessionOwned && _pools.TryRemove($"{s.Profile.Id}|session:{s.Id}", out var own)) await own.CloseAllAsync().ConfigureAwait(false);
        }
        finally
        {
            if (locked) s.ExecLock.Release();
        }
        SessionClosed?.Invoke(s);
        // The shell owns temporary retention; the sidecar needs credentials only while sessions exist.
        if (_profiles.Get(s.Profile.Id)?.PasswordRetention is "once" or "session" or "duration"
            && !_sessions.Values.Any(other => other.Profile.Id == s.Profile.Id && !other.Closed))
            ForgetProfile(s.Profile.Id);
    }

    /// <summary>
    /// The bound on a COMMIT / ROLLBACK the user asked for. tx.commit and tx.rollback have no RPC deadline: a large
    /// rollback, or a commit waiting for a synchronous standby, legitimately takes minutes, and a deadline that cut a
    /// COMMIT short left the outcome unknown behind a "try again". Closing the tab (forced), disconnecting, restarting the
    /// engine or quitting ends the wait sooner (<see cref="Session.Closing"/>). Settable for tests.
    /// </summary>
    internal TimeSpan TxEndLimit { get; set; } = TimeSpan.FromMinutes(30);

    static QuerithmException OutcomeUnknown(string what) => new("txOutcomeUnknown",
        $"{what}, so whether the transaction committed is unknown: the server may have rolled it back. Check the data before running the work again.");

    static QuerithmException NoTransaction(bool afterCutShort) => new("txNone", afterCutShort
        ? "No transaction was open on this tab's connection, so nothing was committed now: the COMMIT that was stopped earlier either committed it or the server rolled it back. Check the data before running the work again."
        : "No transaction was open on this tab's connection, so nothing was committed: it had already ended (committed or rolled back by an earlier statement).");

    public async Task<SessionState> TxAsync(string sessionId, string op, CancellationToken ct)
    {
        var s = Get(sessionId);
        FeatureGuard.Require(s.Provider, Feature.Transactions);
        if (s.ActiveExecutionId != null) throw new QuerithmException("busy", "The session is executing a query.");
        var end = op != "begin";
        using var bound = new CancellationTokenSource(end ? TxEndLimit : Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Closing.Token, bound.Token);
        var token = linked.Token;
        await s.ExecLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!end)
            {
                if (s.TxState == TxState.Open) return s.State();
                var (conn, borrowed) = await AcquireAsync(s, null, token).ConfigureAwait(false);
                try { await s.Provider.Transactions.BeginAsync(conn.Db, token).ConfigureAwait(false); }
                catch
                {
                    // A failed BEGIN may still have left something open: the pool reads the state before reusing it.
                    await ReleaseAsync(s, conn, borrowed).ConfigureAwait(false);
                    throw;
                }
                Pin(s, conn);
                if (s.Closed)
                {
                    // Closed while BEGIN ran: never pin to a session that no longer exists (the server rolls back).
                    s.Pinned = null;
                    await conn.Pool.DiscardAsync(conn).ConfigureAwait(false);
                    return s.State();
                }
                s.TxState = TxState.Open;
                s.TxStartedAt = Time.NowMs();
                s.CommitOutcomeUnknown = false;
            }
            else
            {
                var commit = op == "commit";
                if (s.Pinned == null)
                {
                    s.TxState = TxState.None;
                    // After a COMMIT whose connection was lost, a second COMMIT must not read as the one that committed.
                    var earlier = s.CommitOutcomeUnknown;
                    s.CommitOutcomeUnknown = false;
                    if (commit && earlier) throw NoTransaction(afterCutShort: true);
                    return s.State();
                }
                var pinned = s.Pinned;
                var tx = s.Provider.Transactions;
                if (commit && (s.CommitOutcomeUnknown || s.TxState == TxState.Unknown))
                {
                    // A COMMIT that finds no transaction still succeeds on PostgreSQL (a warning) and MySQL (silently), so
                    // after a COMMIT that was stopped (or with the state unknown) the state is read first: "nothing was
                    // open" is never reported as committed.
                    using (var limit = Cleanup.Start())
                        try { await RefreshTxStateAsync(s, pinned, limit.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { /* unreadable: the COMMIT goes ahead, as asked */ }
                    if (s.TxState == TxState.None)
                    {
                        var earlier = s.CommitOutcomeUnknown;
                        s.CommitOutcomeUnknown = false;
                        _notifier.Notify("session.stateChanged", s.State());
                        throw NoTransaction(earlier);
                    }
                }
                try
                {
                    if (commit) await tx.CommitAsync(pinned.Db, token).ConfigureAwait(false);
                    else await tx.RollbackAsync(pinned.Db, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var what = commit ? "COMMIT" : "ROLLBACK";
                    if (!pinned.IsOpen)
                    {
                        // The connection went with it: nothing is open any more, but a COMMIT may or may not have reached
                        // the server before it did.
                        await ConnectionLostAsync(s, pinned).ConfigureAwait(false);
                        TransactionEnded?.Invoke(s);
                        _notifier.Notify("session.stateChanged", s.State());
                        if (commit)
                        {
                            s.CommitOutcomeUnknown = true;
                            throw OutcomeUnknown("The connection was lost during COMMIT");
                        }
                        throw new QuerithmException("connectionLost", "The connection was lost during ROLLBACK: the server rolls back whatever was open, so the transaction is gone.");
                    }
                    var cutShort = ex is OperationCanceledException || token.IsCancellationRequested;
                    // A failed commit (the server had already ended the transaction, a deferred constraint) is reported as
                    // it is; either way the state is read again so the tab shows what the server now holds (§6 rule 5).
                    using (var limit = Cleanup.Start())
                        try { await RefreshTxStateAsync(s, pinned, limit.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { if (s.TxState == TxState.Open) s.TxState = TxState.Unknown; }
                    _notifier.Notify("session.stateChanged", s.State());
                    if (!cutShort) throw;
                    var why = s.Closing.IsCancellationRequested ? "the tab closed" : bound.IsCancellationRequested ? $"it did not finish within {TxEndLimit.TotalMinutes:0} min" : "it was cancelled";
                    if (s.TxState == TxState.Open)
                        throw new QuerithmException("cancelled", $"{what} was stopped before it ran ({why}): the transaction is still open. Commit or roll back again.");
                    if (!commit && s.TxState == TxState.None) return s.State(); // rolled back: what was asked
                    if (!commit)
                        throw new QuerithmException("txUnknown", $"ROLLBACK was stopped ({why}) and the transaction's state cannot be read. Roll back again, or close the tab: its connection closes and the server rolls back.");
                    s.CommitOutcomeUnknown = true;
                    throw OutcomeUnknown(s.TxState == TxState.None
                        ? $"COMMIT was stopped before it finished ({why}) and the transaction has ended"
                        : $"COMMIT was stopped before it finished ({why}) and the transaction's state cannot be read");
                }
                s.CommitOutcomeUnknown = false;
                // The COMMIT / ROLLBACK succeeded: the state read after it has the cleanup bound, not the request's.
                using (var limit = Cleanup.Start())
                    try { await RefreshTxStateAsync(s, pinned, limit.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { if (s.Pinned == pinned) s.TxState = TxState.Unknown; }
                if (s.TxState == TxState.Unknown && s.Pinned == pinned && !s.SessionOwned && !s.TracePinned)
                {
                    // The COMMIT / ROLLBACK the user asked for succeeded, but the state cannot be read: the connection is
                    // closed (the server ends anything still open on it), so the tab is never stuck showing TX ?.
                    s.Pinned = null;
                    s.Spid = null;
                    s.TxState = TxState.None;
                    s.TxStartedAt = null;
                    await pinned.Pool.DiscardAsync(pinned).ConfigureAwait(false);
                    TransactionEnded?.Invoke(s);
                }
            }
            var st = s.State();
            _notifier.Notify("session.stateChanged", st);
            return st;
        }
        finally { s.ExecLock.Release(); }
    }

    public void Pin(Session s, PhysicalConnection conn)
    {
        s.Pinned = conn;
        s.Spid = null;
    }

    /// <summary>The savepoint a read on the user's pinned connection runs inside (<see cref="PinnedReadAsync{T}"/>).</summary>
    internal const string ReadSavepoint = "querithm_read";

    /// <summary>
    /// A read Querithm runs on the session's pinned connection while a transaction is (or may be) open — a definition,
    /// dependencies, a script, row counts, a compare model, grid row tokens — under its RPC's deadline. Where a failed or
    /// cancelled statement aborts the transaction (PostgreSQL 25P02: <see cref="TransactionTraits.FailedStatementAbortsTransaction"/>
    /// with savepoints) it runs inside SAVEPOINT … RELEASE and is rolled back to on any failure, so the deadline never
    /// leaves the user's transaction unusable; the savepoint commands have the cleanup bound, never the deadline. After a
    /// read that failed inside the savepoint, or was cancelled on any engine (SQL Server with XACT_ABORT ON rolls the
    /// transaction back on a cancel), the state is read again and the tab told; a transaction that ended is said in the
    /// error. A savepoint that cannot be set (the connection is busy, the transaction already failed) leaves the read as
    /// it was: unguarded, and no state read on a connection Querithm may not hold.
    /// </summary>
    public async Task<T> PinnedReadAsync<T>(Session s, PhysicalConnection conn, Func<Task<T>> work, CancellationToken ct)
    {
        if (conn != s.Pinned || s.TxState == TxState.None) return await work().ConfigureAwait(false);
        var tx = s.Provider.Transactions;
        var wasOpen = s.TxState == TxState.Open;
        var guarded = false;
        if (tx.Traits.FailedStatementAbortsTransaction && tx.Traits.Savepoints)
        {
            using var limit = Cleanup.Start();
            try
            {
                await tx.SavepointAsync(conn.Db, ReadSavepoint, limit.Token).ConfigureAwait(false);
                guarded = true;
            }
            catch (Exception ex) { _log.LogDebug("Session {Id}: no savepoint around the read ({Message}); it runs unguarded", s.Id, ex.Message); }
        }
        T result;
        try { result = await work().ConfigureAwait(false); }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException || ct.IsCancellationRequested;
            if (!guarded && !cancelled) throw;
            using var limit = Cleanup.Start();
            var undone = !guarded;
            if (guarded && conn.IsOpen)
                try
                {
                    await tx.RollbackToSavepointAsync(conn.Db, ReadSavepoint, limit.Token).ConfigureAwait(false);
                    await tx.ReleaseSavepointAsync(conn.Db, ReadSavepoint, limit.Token).ConfigureAwait(false);
                    undone = true;
                }
                catch (Exception undo) { _log.LogWarning("Session {Id}: rolling back to the read's savepoint failed: {Message}", s.Id, undo.Message); }
            var what = cancelled ? "The read on this tab's connection did not finish in time and was stopped" : "The read on this tab's connection failed";
            if (!conn.IsOpen)
            {
                await ConnectionLostAsync(s, conn).ConfigureAwait(false);
                TransactionEnded?.Invoke(s);
                _notifier.Notify("session.stateChanged", s.State());
                throw new QuerithmException("txLost", $"{what}, and its connection broke: the transaction that was open on this tab is gone (the server rolled it back).");
            }
            try { await RefreshTxStateAsync(s, conn, limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (s.TxState == TxState.Open) s.TxState = TxState.Unknown; }
            _notifier.Notify("session.stateChanged", s.State());
            if (wasOpen && s.TxState == TxState.None)
                throw new QuerithmException("txLost", $"{what}, and the transaction that was open on this tab has ended on the server (a cancel rolls it back with XACT_ABORT ON): its changes were rolled back.");
            if (!undone)
                throw new QuerithmException("txAborted", $"{what}, and Querithm could not undo it inside your transaction: the transaction may now refuse every statement until it is rolled back.");
            throw;
        }
        if (guarded)
        {
            using var limit = Cleanup.Start();
            try { await tx.ReleaseSavepointAsync(conn.Db, ReadSavepoint, limit.Token).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning("Session {Id}: releasing the read's savepoint failed: {Message}", s.Id, ex.Message); }
        }
        return result;
    }

    /// <summary>Re-reads @@TRANCOUNT / tx status; pins on open, unpins and returns to the pool on none (§7 Transactions).</summary>
    public async Task RefreshTxStateAsync(Session s, PhysicalConnection conn, CancellationToken ct)
    {
        TxState state;
        try { state = await s.Provider.Transactions.StateAsync(conn.Db, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Could not read transaction state: {Message}", ex.Message);
            state = conn.IsOpen ? TxState.Unknown : TxState.None;
        }
        if (s.Closed && state != TxState.None)
        {
            // The tab was closed while this ran and the batch left a transaction open. Nobody can end it any more, so the
            // connection is closed (the server rolls back) instead of being pinned to a session that no longer exists.
            _log.LogWarning("Session closed while executing with an open transaction; its connection was closed and the server rolls back.");
            if (s.Pinned == conn) s.Pinned = null;
            s.TxState = TxState.None;
            s.TxStartedAt = null;
            await conn.Pool.DiscardAsync(conn).ConfigureAwait(false);
            return;
        }
        if (state == TxState.Open)
        {
            if (s.Pinned != conn) Pin(s, conn);
            if (s.TxState != TxState.Open) s.TxStartedAt = Time.NowMs();
            s.TxState = TxState.Open;
        }
        else if (state == TxState.None)
        {
            var ended = s.TxState != TxState.None;
            s.TxState = TxState.None;
            s.TxStartedAt = null;
            // A session-owned connection stays with its session after commit or rollback (Phase D0d).
            if (s.Pinned == conn && !s.TracePinned && !s.SessionOwned)
            {
                s.Pinned = null;
                conn.Pool.Return(conn);
            }
            if (ended || s.RefreshDeferred) TransactionEnded?.Invoke(s);
        }
        else
        {
            s.TxState = TxState.Unknown;
            if (s.Pinned != conn) Pin(s, conn);
        }
    }

    /// <summary>
    /// The stored profile as the guards read it (environment, Prod target), kept in memory so query.execute reads no
    /// store.db on the RPC thread (8a review: it read it 1–3 times per run). Every save or delete drops it
    /// (<see cref="ProfileStore.Changed"/>); an entry read under an older epoch than the latest change is read again, so a
    /// save racing a read never leaves a stale copy.
    /// </summary>
    readonly ConcurrentDictionary<string, (long Epoch, Profile? Profile)> _stored = new();
    long _storedEpoch;

    Profile? Stored(string profileId)
    {
        var epoch = Interlocked.Read(ref _storedEpoch);
        if (_stored.TryGetValue(profileId, out var hit) && hit.Epoch == epoch) return hit.Profile;
        var p = _profiles.Get(profileId);
        _stored[profileId] = (epoch, p);
        return p;
    }

    /// <summary>
    /// The environment the guards apply to the session (§13 rule 7): the stricter of the profile as it is stored now and the
    /// snapshot the session opened with — an edit to Prod guards open tabs at once, and a tab opened as Prod keeps its
    /// guards (its connections still go where that profile pointed) until it is opened again.
    /// </summary>
    public EnvironmentKind EnvironmentOf(Session s)
    {
        var stored = Stored(s.Profile.Id)?.Environment ?? s.Profile.Environment;
        return stored == EnvironmentKind.Prod || s.Profile.Environment == EnvironmentKind.Prod ? EnvironmentKind.Prod : stored;
    }

    public bool IsProd(Session s) => EnvironmentOf(s) == EnvironmentKind.Prod;

    /// <summary>
    /// What the first-run confirm on Prod was given for (§6): the profile, its stored environment and target, and the
    /// session's database. A changed environment, engine, host, port, file or default database — or a tab on another
    /// database — needs the confirm again.
    /// </summary>
    string ProdTarget(Session s)
    {
        var p = Stored(s.Profile.Id) ?? s.Profile;
        return string.Join('\u001f', p.Id, p.Environment, p.Engine, p.Location, p.Host, p.Port, p.File?.GrantId, p.DatabaseDefault, s.Database);
    }

    /// <summary>First execution on a Prod target per app launch needs confirmation (§6).</summary>
    public bool IsProdConfirmed(Session s) => _prodConfirmed.ContainsKey(ProdTarget(s));
    public void ConfirmProd(Session s) => _prodConfirmed.TryAdd(ProdTarget(s), 0);

    /// <summary>A profile edit drops its first-run confirms (the new settings ask again).</summary>
    public void ForgetProdConfirms(string profileId)
    {
        foreach (var key in _prodConfirmed.Keys.Where(k => k.StartsWith(profileId + '\u001f', StringComparison.Ordinal)).ToList()) _prodConfirmed.TryRemove(key, out _);
    }

    async Task SweepAsync()
    {
        foreach (var pool in _pools.Values)
        {
            try { await pool.SweepAsync(5 * 60_000).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug("Sweep failed: {Message}", ex.Message); }
        }
        Registry.Prune();
    }

    public async ValueTask DisposeAsync()
    {
        await _sweeper.DisposeAsync().ConfigureAwait(false);
        foreach (var s in _sessions.Values)
        {
            // Work still waiting on a session (a COMMIT the user asked for, a helper's lease) stops first.
            try { s.Closing.Cancel(); } catch (Exception ex) { _log.LogDebug("Stopping session {Id}'s work failed: {Message}", s.Id, ex.Message); }
            if (s.Pinned is { } p) await p.Pool.DiscardAsync(p).ConfigureAwait(false);
        }
        foreach (var pool in _pools.Values) await pool.CloseAllAsync().ConfigureAwait(false);
        if (Tunnels != null) await Tunnels.DisposeAsync().ConfigureAwait(false);
    }
}
