using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Diagnostics;
using Querithm.Core.Execution;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;

namespace Querithm.Host;

/// <summary>All sidecar services, composed once. Tests build this without a process or socket.</summary>
public sealed class Services : IAsyncDisposable
{
    public required LocalStore Store { get; init; }
    public required ProfileStore Profiles { get; init; }
    public required SettingsStore Settings { get; init; }
    public required HistoryStore History { get; init; }
    public required SnippetStore Snippets { get; init; }
    public required SessionManager Sessions { get; init; }
    public required ExecutionService Executions { get; init; }
    public required MetadataService Metadata { get; init; }
    public required ObjectService Objects { get; init; }
    public required LanguageService Language { get; init; }
    public required TraceService Trace { get; init; }
    public required DebugService Debug { get; init; }
    public required DataEditorService DataEditor { get; init; }
    public required Core.Import.ImportService Imports { get; init; }
    public required Core.Compare.SchemaCompareService SchemaCompare { get; init; }
    public required Core.Compare.DataCompareService DataCompare { get; init; }
    public required Core.Refactor.RefactorService Refactor { get; init; }
    public required Core.Metadata.DefinitionFetcher Definitions { get; init; }
    public required Core.Monitoring.MonitorService Monitors { get; init; }
    public required Core.Monitoring.BlockingBannerService Banner { get; init; }
    /// <summary>Startup notices, webview error reports and the log tail (M17, M21, M22).</summary>
    public required SidecarDiagnostics Diagnostics { get; init; }

    public static Services Create(LocalStore store, IFrameSink frames, INotifier notifier, ILoggerFactory logs, ProviderRegistry providers, string? dataDir = null)
    {
        var profiles = new ProfileStore(store);
        var history = new HistoryStore(store, logs.CreateLogger("history"));
        var metaStore = new MetadataStore(store);
        var dir = dataDir ?? Path.Combine(Path.GetTempPath(), "querithm");
        var sessions = new SessionManager(profiles, providers, notifier, logs.CreateLogger("sessions"))
        {
            Tunnels = new Core.Ssh.SshTunnelManager(new Core.Ssh.KnownHosts(Path.Combine(dir, "known_hosts")), logs.CreateLogger("ssh")),
        };
        var cache = new SchemaCache(metaStore);
        var metadata = new MetadataService(sessions, metaStore, cache, notifier, logs.CreateLogger("metadata"));
        var executions = new ExecutionService(sessions, frames, notifier, history, logs.CreateLogger("execution"), cache);
        var dataEditor = new DataEditorService(sessions, executions, metaStore, cache, logs.CreateLogger("dataedit"));
        executions.Editability = dataEditor.Decide;
        executions.DdlExecuted += s =>
        {
            // Phase D0d: a session-owned session reads its catalog on its own connection (after the execution releases it).
            if (s.Database != null) metadata.Refresh(new RefreshParams { ProfileId = s.Profile.Id, Database = s.Database, SessionId = s.SessionOwned ? s.Id : null });
        };
        sessions.SessionOpened += s => { if (s.SessionOwned) cache.AddScope(s.Id, s.Profile.Id, s.InMemory); };
        sessions.SessionClosed += metadata.ForgetSession;
        sessions.TransactionEnded += metadata.OnTransactionEnded;
        // JIT warm-up of the parser and completion paths, off the RPC thread, once the user starts working.
        sessions.FirstSessionOpened += () => Task.Run(() =>
        {
            foreach (var provider in providers.All)
            {
                var sql = "WITH c AS (SELECT a.x FROM dbo.t a WHERE a.y = 1) SELECT c.x FROM c JOIN dbo.u u ON u.x = c.x;";
                try
                {
                    provider.Language.Classify(sql);
                    provider.Language.Complete(new CompleteParams { DocId = "__warmup", Text = sql, Offset = 40 }, cache);
                }
                catch { /* warm-up only */ }
            }
        });
        var monitors = new Core.Monitoring.MonitorService(sessions, notifier, logs.CreateLogger("monitor"), dir, cache);
        var banner = new Core.Monitoring.BlockingBannerService(sessions, notifier, logs.CreateLogger("banner"), monitors.AnswersBannerFor);
        monitors.MonitorStarted += m => m.Snapshot += banner.OnSnapshot;
        var objects = new ObjectService(sessions, metadata);
        var definitions = new Core.Metadata.DefinitionFetcher(sessions, objects, metadata, logs.CreateLogger("definitions"));
        var diagnostics = new SidecarDiagnostics(Path.Combine(dir, "logs"), logs.CreateLogger("webview"));
        // A notice met after start waits in system.notices like the startup ones; the webview is told to take it.
        history.Notice += n =>
        {
            diagnostics.AddNotice(n);
            notifier.Notify("system.noticeAdded", n);
        };
        return new Services
        {
            Store = store, Profiles = profiles, Settings = new SettingsStore(store), History = history, Snippets = new SnippetStore(store), Sessions = sessions,
            Executions = executions, Metadata = metadata, Objects = objects,
            Language = new LanguageService(providers, cache),
            Trace = new TraceService(sessions, notifier, logs.CreateLogger("trace")),
            Debug = new DebugService(sessions, notifier, logs.CreateLogger("debug"), executions.Guard),
            DataEditor = dataEditor,
            Imports = new Core.Import.ImportService(sessions, executions, metadata, notifier, dir, logs.CreateLogger("import")),
            SchemaCompare = new Core.Compare.SchemaCompareService(sessions, objects, providers, definitions, logs.CreateLogger("compare")),
            Definitions = definitions,
            DataCompare = new Core.Compare.DataCompareService(sessions, metadata, executions, notifier, logs.CreateLogger("datacompare")),
            Refactor = new Core.Refactor.RefactorService(sessions, metadata, objects, definitions, providers),
            Monitors = monitors,
            Banner = banner,
            Diagnostics = diagnostics,
        };
    }

    public async ValueTask DisposeAsync()
    {
        // Monitors first: their connections close before the pools and tunnels they were opened through (L9).
        await Monitors.DisposeAsync().ConfigureAwait(false);
        await Banner.DisposeAsync().ConfigureAwait(false);
        await Trace.DisposeAsync().ConfigureAwait(false);
        await Debug.DisposeAsync().ConfigureAwait(false);
        await Executions.DisposeAsync().ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>JSON-RPC surface (§5). Each method maps 1:1 to IQuerithmRpc; logic lives in Core services.</summary>
public sealed class RpcServer(Services s, Action shutdown) : IQuerithmRpc
{
    static readonly long StartedAt = Stopwatch.GetTimestamp();

    /// <summary>
    /// How long an interactive RPC that waits on a server may take (catalog reads, scripting, row counts, state reads,
    /// switching database, BEGIN, starting a trace or a debug session) before it ends with
    /// <c>timeout</c>. These answer in milliseconds to a few seconds on a server that is working (SMO scripting of a table
    /// with many indexes and partitions is the slowest); waiting longer means a lock or a server that stopped answering,
    /// and the webview sends no $/cancelRequest, so without a deadline the call — and the pooled connection under it —
    /// would wait for good (the drivers' command timeout defaults to none, §13 rule 8). Settable for tests.
    /// </summary>
    public TimeSpan InteractiveDeadline { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The deadline of long reads the user waits for with a progress view but that have no cancel RPC: schema compare (and
    /// its model export), find usages and the rename preview, which may first fetch every definition of a large database.
    /// Each catalog read of a schema compare already has a 300 s command timeout; this bounds the whole call, far above
    /// what the §14 fixture needs (metrics #21, #24: seconds), so only a server that stopped answering reaches it.
    /// </summary>
    public TimeSpan LongDeadline { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Handlers whose wait is bounded by something other than a deadline — each listed with that bound in
    /// <c>ArchitectureRulesTests.RpcOwnBounds</c>: work with its own cancel RPC, or a debugger step that ends when the user
    /// stops the session.
    /// </summary>
    static CancellationToken OwnBound => CancellationToken.None;

    /// <summary>After the deadline, how long the call may take to stop before the RPC answers anyway (its connection is closed or broken by then).</summary>
    static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    Task<T> Interactive<T>(string what, Func<CancellationToken, Task<T>> work) => Bounded(what, InteractiveDeadline, work);
    Task Interactive(string what, Func<CancellationToken, Task> work) => Bounded(what, InteractiveDeadline, async ct => { await work(ct).ConfigureAwait(false); return true; });

    /// <summary>
    /// Runs <paramref name="work"/> with a token that fires at <paramref name="deadline"/>. The driver then cancels its
    /// command (Npgsql and MySqlConnector break the connection after their cancellation timeout; on SQL Server the
    /// metadata path closes a connection whose cancel is not acknowledged, ObjectService); the RPC answers with
    /// <c>timeout</c> at most <see cref="StopGrace"/> later even if the call has not stopped.
    /// </summary>
    static async Task<T> Bounded<T>(string what, TimeSpan deadline, Func<CancellationToken, Task<T>> work)
    {
        using var cts = new CancellationTokenSource(deadline);
        try
        {
            return await work(cts.Token).WaitAsync(deadline + StopGrace).ConfigureAwait(false);
        }
        // Whatever the driver throws once its command was cancelled (an OperationCanceledException, a server error for the
        // cancelled statement, a broken connection) is the deadline's doing; Querithm's own refusals pass through.
        catch (Exception ex) when (cts.IsCancellationRequested && ex is not Core.QuerithmException)
        {
            throw TimedOut(what, deadline);
        }
    }

    internal static Core.QuerithmException TimedOut(string what, TimeSpan deadline) => new("timeout",
        $"{what} did not finish within {Describe(deadline)}: the server did not answer (a lock held by another session, a server that stopped responding, or this tab's connection busy with a long query). Try again, or check the server.");

    static string Describe(TimeSpan t) => t.TotalMinutes >= 1 && t.Seconds == 0 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";

    public Task<PingResult> Ping() => Task.FromResult(new PingResult
    {
        Version = ContractInfo.Version,
        Pid = Environment.ProcessId,
        UptimeMs = (long)Stopwatch.GetElapsedTime(StartedAt).TotalMilliseconds,
        WorkingSetBytes = Environment.WorkingSet,
    });

    public Task<EngineInfo[]> Engines() => Task.FromResult(
        s.Sessions.Providers.All.Select(p => Info(p, true)).Concat(s.Sessions.Providers.Hidden.Select(p => Info(p, false))).ToArray());

    static EngineInfo Info(Contract.Providers.IDbProvider p, bool exposed) => new()
    {
        Engine = p.Engine, Dialect = p.Dialect, Name = p.Traits.Name, Exposed = exposed, DefaultPort = p.Traits.DefaultPort,
        DefaultDatabase = p.Traits.DefaultDatabase, DefaultSchema = p.Traits.DefaultSchema, DefaultAuth = p.Traits.DefaultAuth,
        AuthKinds = p.Traits.AuthKinds, Locations = p.Traits.Locations,
    };

    public Task<SystemNotice[]> Notices() => Task.FromResult(s.Diagnostics.TakeNotices());

    public Task ReportError(ErrorReport p)
    {
        s.Diagnostics.Report(p);
        return Task.CompletedTask;
    }

    public Task<LogTailResult> LogTail(LogTailParams p) => Task.FromResult(s.Diagnostics.Read(p));

    public Task Shutdown()
    {
        _ = Task.Run(async () => { await Task.Delay(50); shutdown(); });
        return Task.CompletedTask;
    }

    // ---- profiles ----
    /// <summary>Every stored profile; one for an engine this build does not offer is listed as unsupported, never as another engine (Phase D0a).</summary>
    public Task<Profile[]> ProfileList() => Task.FromResult(s.Profiles.List().Select(Describe).Select(Listable).ToArray());

    Profile Describe(Profile p) =>
        p.Unsupported == null && s.Sessions.Providers.TryGet(p.Engine) == null ? p with { Unsupported = Core.Sessions.ProfileRules.Unavailable(p.Engine) } : p;

    static readonly System.Text.Json.JsonSerializerOptions WireJson = Json.CreateRpc();

    internal const string UnlistableProfile =
        "This profile holds a setting this version of Querithm cannot show (it may have been saved by a newer version, or edited outside Querithm). Delete it and create it again.";

    /// <summary>
    /// PT8-6: one stored row the reply cannot carry (an undefined enum value, say) must not fail the whole list, which the
    /// connection dialog then never shows again: that profile is listed as unsupported with only its id and name (it can be
    /// deleted), and the sidecar log says so.
    /// </summary>
    static Profile Listable(Profile p)
    {
        try
        {
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(p, WireJson);
            return p;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            var name = Core.Sessions.ProfileRules.Text(p.Name, "name", "name", Core.Sessions.ProfileRules.MaxNameLength, required: true) == null ? p.Name : "(unreadable profile)";
            Console.Error.WriteLine($"[warn] profile {(Core.Sessions.ProfileRules.ValidId(p.Id) ? p.Id : "(bad id)")} cannot be listed as stored ({ex.GetType().Name}); it is shown as unsupported");
            return new Profile
            {
                Id = p.Id, Name = name, Engine = Enum.IsDefined(p.Engine) ? p.Engine : Engine.Mssql, Auth = Enum.IsDefined(p.Auth) ? p.Auth : AuthKind.Sql,
                Location = p.Location is { } l && Enum.IsDefined(l) ? l : null, Unsupported = UnlistableProfile, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt,
            };
        }
    }

    /// <summary>
    /// A new profile has an empty id (the sidecar assigns a ULID); any other id must name a stored profile, in the shape the
    /// sidecar assigns (PT8-4). A caller-chosen id could look like a path, outgrow the shell's keyring accounts, or alias
    /// another profile's keyring entry ("&lt;id&gt;:ssh").
    /// </summary>
    Profile? Existing(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (!Core.Sessions.ProfileRules.ValidId(id))
            throw new QuerithmException("invalidProfile", "This profile id was not assigned by Querithm. Save the profile as a new one.", new Dictionary<string, string> { ["field"] = "id" });
        return s.Profiles.Get(id) ?? throw new QuerithmException("notFound", "This connection profile no longer exists (it may have been deleted). Save it as a new one.");
    }

    public async Task<Profile> ProfileSave(Profile profile)
    {
        if (profile == null) throw QuerithmException.Invalid("profile.save needs a profile.");
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new QuerithmException("invalid", "A profile needs a name.", new Dictionary<string, string> { ["field"] = "name" });
        var before = Existing(profile.Id);
        // A stored profile this build cannot read keeps what a newer build wrote: it is never overwritten from here.
        if (before?.Unsupported is { } unreadable) throw new QuerithmException("unsupportedProfile", unreadable);
        // A refusal about one field (host, port, file, SSH host or port) names it, so the editor shows it there (L25).
        if (Core.Sessions.ProfileRules.Check(profile with { Unsupported = null }, s.Sessions.Providers.TryGet(profile.Engine)) is { } invalid)
            throw new QuerithmException("invalidProfile", invalid.Message, invalid.Field is { } field ? new Dictionary<string, string> { ["field"] = field } : null);
        // Retire credentials before publishing the new endpoint to background work. Pinned transactions keep their
        // already authenticated connection, and a failed save may ask for credentials again but never misroute them.
        var credentialsChanged = before != null && !Core.Sessions.SessionManager.SameConnectionSettings(before, profile);
        if (credentialsChanged) s.Sessions.ForgetCredentials(before!.Id);
        var saved = s.Profiles.Save(profile);
        // A running monitor keeps the profile it started with (host, options) while kills read the environment fresh: when
        // the connection would now be built differently it stops first (not for a rename or a password-retention change).
        if (before != null && ConnectionChanged(before, saved))
            await s.Monitors.StopForProfileAsync(saved.Id, "The connection profile was changed; start the monitor again to use the new settings.");
        // A first-run confirm on Prod was given for the old target or environment: the edited profile asks again (L1).
        if (before != null && (ConnectionChanged(before, saved) || before.Environment != saved.Environment)) s.Sessions.ForgetProdConfirms(saved.Id);
        // Connection settings may have changed: drop pooled connections built from the old settings.
        if (!credentialsChanged) s.Sessions.ForgetPools(saved.Id);
        s.Objects.InvalidateCapabilities();
        return saved;
    }

    /// <summary>The settings a connection is built from (the environment is read fresh wherever it matters).</summary>
    static bool ConnectionChanged(Profile a, Profile b) =>
        a.Engine != b.Engine || a.Location != b.Location || a.File != b.File || a.Host != b.Host || a.Port != b.Port || a.Auth != b.Auth || a.Username != b.Username
        || a.DatabaseDefault != b.DatabaseDefault || a.TlsMode != b.TlsMode || a.TrustCert != b.TrustCert || a.CaPath != b.CaPath
        || a.TimeoutS != b.TimeoutS || a.Ssh != b.Ssh
        || !(a.Options ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal).SequenceEqual((b.Options ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal));

    /// <summary>
    /// Deleting a profile (L2) first closes every session on it, refused like <see cref="GrantRevoke"/> (code
    /// <c>profileBusy</c>) while one has an open or unknown transaction or a running query, so the UI settles those tabs
    /// first; then its monitors, secrets, pools, driver state, grant, metadata and history go. The shell deletes its
    /// keyring entries only after this succeeds.
    /// </summary>
    public async Task ProfileDelete(ProfileRef p)
    {
        if (string.IsNullOrEmpty(p?.ProfileId)) throw QuerithmException.Invalid("profile.delete needs a profileId.");
        await CloseProfileSessionsAsync(p.ProfileId, () => new QuerithmException("profileBusy",
            "A tab on this connection has an open transaction or a running query. Commit or roll back (or cancel the query) first, then delete the profile."));
        await s.Monitors.StopForProfileAsync(p.ProfileId);
        s.Sessions.ForgetProfile(p.ProfileId);
        s.Sessions.ForgetProdConfirms(p.ProfileId);
        await s.Metadata.ForgetProfileAsync(p.ProfileId);
        s.Profiles.Delete(p.ProfileId);
        s.Objects.InvalidateCapabilities();
    }

    public async Task<ProfileTestResult> ProfileTest(ProfileTestParams p)
    {
        if (p == null) throw QuerithmException.Invalid("profile.test needs a profileId or a profile.");
        var profile = p.Profile ?? (p.ProfileId != null ? s.Sessions.GetProfile(p.ProfileId) : throw QuerithmException.Invalid("profile.test needs a profileId or a profile."));
        // PT3-1: with a profileId the shell fills a missing secret from that profile's keyring entry, so the edited copy
        // may be tested with it only while it still points where the stored profile does (the webview sends no profileId
        // once the endpoint changed, and the user types the secret).
        if (p.Profile != null && p.ProfileId != null && (p.Secret != null || p.SshSecret != null) && EndpointRefusal(p.ProfileId, p.Profile) is { } moved)
            return new ProfileTestResult { Ok = false, Error = moved };
        // An unsaved profile from the editor carries no Unsupported of its own; a stored one may (Phase D0a). The editor's
        // copy also meets the input rules profile.save applies.
        if (Core.Sessions.ProfileRules.Check(profile, s.Sessions.Providers.TryGet(profile.Engine), input: p.Profile != null) is { } invalid)
            return new ProfileTestResult { Ok = false, Error = invalid.Message };
        var provider = s.Sessions.Providers.Get(profile.Engine);
        Core.Logging.LogScrubber.RegisterSecret(p.Secret);
        Core.Logging.LogScrubber.RegisterSecret(p.SshSecret);
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        Core.Ssh.SshTunnelManager.Tunnel? tunnel = null;
        try
        {
            using var cts = new CancellationTokenSource(TestDeadline(profile));
            if (profile.Ssh != null)
            {
                // A tunnel of its own: an unsaved profile must not reuse (or leave behind) a shared one.
                var (host, port, refusal) = provider.TunnelTarget(profile);
                if (refusal != null) return new ProfileTestResult { Ok = false, Error = refusal, ElapsedMs = sw.Elapsed.TotalMilliseconds };
                tunnel = await s.Sessions.Tunnels!.OpenTemporaryAsync(profile, p.SshSecret, host, port, cts.Token);
                if (provider.RouteThrough(profile, "127.0.0.1", 1).Note is { } note) notes.Add(note);
            }
            // A file profile opens only the grant the shell injected for it (Phase D0c).
            if (Core.Sessions.ProfileRules.LocationOf(profile) == LocationKind.File && (p.FileGrant is not { } g || g.GrantId != profile.File?.GrantId))
                return new ProfileTestResult { Ok = false, Error = "Pick the database file first (no access to it was granted).", ElapsedMs = sw.Elapsed.TotalMilliseconds };
            if (p.FileGrant != null) Core.Logging.LogScrubber.RegisterPath(p.FileGrant.Path);
            await using var conn = await s.Sessions.OpenConnectionAsync(profile, provider, p.Secret, p.SshSecret, profile.DatabaseDefault, cts.Token, tunnel, file: p.FileGrant);
            return new ProfileTestResult { Ok = true, ServerVersion = provider.ServerVersion(conn), ElapsedMs = sw.Elapsed.TotalMilliseconds, Notes = notes.ToArray() };
        }
        catch (Core.QuerithmException q) when (q.DataPayload is SshHostKey key)
        {
            return new ProfileTestResult { Ok = false, Error = q.Message, UntrustedHostKey = key, ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }
        catch (Exception ex)
        {
            var msg = ex is Core.QuerithmException ? ex.Message : provider.TranslateException(ex).FirstOrDefault()?.Message ?? ex.Message;
            return new ProfileTestResult { Ok = false, Error = QuerithmJsonRpc.Describe(ex, Core.Logging.LogScrubber.Scrub(msg)), ElapsedMs = sw.Elapsed.TotalMilliseconds, Notes = notes.ToArray() };
        }
        finally { tunnel?.Dispose(); }
    }

    internal const string EndpointChangedMessage = "The connection settings changed: type the password again to test them.";

    /// <summary>
    /// PT3-1 (defence in depth for the shell's keyring injection): null while <paramref name="edited"/> still names the
    /// stored profile's endpoint, else the reason. The endpoint is what a saved secret is bound to, as the editor's
    /// endpointKey (ProfileEditor.tsx) has it: engine, host (trimmed, any case; none for a file or memory database), port,
    /// user, sign-in, TLS mode, trustCert, CA file, hostNameInCertificate, and the SSH host, port, user, sign-in and key
    /// file. Name, group, environment, color, timeouts and the other options may differ.
    /// </summary>
    string? EndpointRefusal(string profileId, Profile edited)
    {
        var stored = Core.Sessions.ProfileRules.ValidId(profileId) ? s.Profiles.Get(profileId) : null;
        if (stored == null) return "This connection profile no longer exists (it may have been deleted). Type the password to test the settings.";
        return EndpointKey(stored) == EndpointKey(edited) ? null : EndpointChangedMessage;
    }

    static string EndpointKey(Profile p)
    {
        var local = Core.Sessions.ProfileRules.LocationOf(p) != LocationKind.Server;
        var host = local || string.IsNullOrWhiteSpace(p.Host) ? null : p.Host.Trim();
        // DNS names ignore case; PostgreSQL filesystem and abstract socket names do not.
        var socket = p.Engine == Engine.Pg && host != null && host.Split(',').Any(h => h.TrimStart().StartsWith('/') || h.TrimStart().StartsWith('@'));
        if (!socket) host = host?.ToLowerInvariant();
        var ssh = p.Ssh is { } t ? new object?[] { t.Host?.Trim().ToLowerInvariant(), t.Port ?? 22, t.Username, t.Auth, t.KeyPath } : null;
        return System.Text.Json.JsonSerializer.Serialize(new object?[]
        {
            p.Engine, host, p.Port, p.Username, p.Auth, p.TlsMode, p.TrustCert, p.CaPath, p.Options?.GetValueOrDefault("hostNameInCertificate"), ssh,
        }, WireJson);
    }

    /// <summary>
    /// PT8-2 / PT1-3: how long profile.test may take, computed in long seconds so no timeoutS overflows it: the profile's
    /// connect timeout capped at <see cref="Core.Sessions.ProfileRules.MaxTestTimeoutS"/> (0, the drivers' "no limit", and
    /// anything negative add nothing, as before), twice through an SSH tunnel, plus 5 s. Never unbounded.
    /// </summary>
    internal static TimeSpan TestDeadline(Profile profile)
    {
        long connect = profile.TimeoutS switch
        {
            null => 15,
            <= 0 => 0, // as before: SqlClient reads 0 as "retry until the deadline", so 0 gets only the 5 s below
            { } t => Math.Min(t, Core.Sessions.ProfileRules.MaxTestTimeoutS),
        };
        return TimeSpan.FromSeconds(connect * (profile.Ssh != null ? 2L : 1L) + 5L);
    }

    /// <summary>
    /// Only after the user compared and confirmed the fingerprint in the UI (never automatic), and only a key a bastion
    /// presented to this sidecar in the last <see cref="Core.Ssh.PresentedHostKeys.Lifetime"/>, each presentation used once (M2).
    /// </summary>
    public Task SshTrustHostKey(SshHostKey p)
    {
        if (p == null || string.IsNullOrEmpty(p.Host) || string.IsNullOrEmpty(p.Algorithm) || string.IsNullOrEmpty(p.Fingerprint))
            throw QuerithmException.Invalid("ssh.trustHostKey needs the host, port, algorithm and fingerprint the SSH server presented.");
        if (p.Host.Length > Core.Sessions.ProfileRules.MaxHostLength || p.Algorithm.Length > 64 || p.Fingerprint.Length > 128 || p.Port is < 1 or > 65535)
            throw QuerithmException.Invalid("This is not a host key an SSH server presented to Querithm.");
        s.Sessions.Tunnels!.Trust(p);
        return Task.CompletedTask;
    }

    /// <summary>A pasted connection string is a few hundred characters; anything past this is refused unread.</summary>
    internal const int MaxConnectionStringChars = 8 * 1024;

    /// <summary>
    /// Reads a pasted connection string with each driver's own rules and ranks the drivers that read it, in order:
    /// fewest keywords the driver does not know; most signals only that engine's strings carry (its own keyword spelling,
    /// its default port, SQL Server's "host,port"); fewest keywords Querithm does not apply; the editor's engine (a new
    /// profile sends a placeholder, so it decides only between equals); most spellings conventional for the engine;
    /// registration order. "Server=h;Port=3306;User ID=u" is thus MySQL even though Npgsql reads every keyword of it.
    /// The text may hold a secret: no error repeats any of it.
    /// </summary>
    public Task<ParseConnectionStringResult> ProfileParseConnectionString(ParseConnectionStringParams p)
    {
        if (p?.Profile is not { } basis) throw QuerithmException.Invalid("profile.parseConnectionString needs the editor's profile.");
        var text = p.ConnectionString;
        if (string.IsNullOrWhiteSpace(text)) throw QuerithmException.Invalid("Paste a connection string first.");
        if (text.Length > MaxConnectionStringChars)
            throw QuerithmException.Invalid($"This connection string could not be read: it is longer than {MaxConnectionStringChars / 1024} KB.");

        var keywords = Contract.Providers.ConnectionStringKeywords.Tokenize(text);
        var ranked = new List<(ParseConnectionStringResult Result, Contract.Providers.ConnectionStringAffinity Affinity, int Order, bool Basis)>();
        var order = 0;
        foreach (var provider in s.Sessions.Providers.All)
        {
            var isBasis = provider.Engine == basis.Engine;
            try
            {
                var r = provider.ParseConnectionString(text, isBasis ? basis : Rebase(basis, provider));
                ranked.Add((r, provider.ConnectionStringAffinity(keywords), order, isBasis));
            }
            catch (ArgumentException) { /* this driver cannot read it; the reason given below never quotes the text */ }
            order++;
        }
        if (ranked.Count == 0)
        {
            // Only file engines here (§13 rule 6: their files are picked, never named by a string).
            var fileOnly = s.Sessions.Providers.All.All(x => !x.Traits.Locations.Contains(LocationKind.Server));
            var reason = fileOnly ? "databases on this computer are picked with the file dialog, not read from a connection string." : UnreadableReason(text, keywords);
            throw QuerithmException.Invalid($"This connection string could not be read: {reason}");
        }
        var best = ranked
            .OrderBy(x => x.Result.Unknown.Length)
            .ThenByDescending(x => x.Affinity.Distinctive)
            .ThenBy(x => x.Result.NotApplied.Length)
            .ThenByDescending(x => x.Basis)
            .ThenByDescending(x => x.Affinity.Conventional)
            .ThenBy(x => x.Order)
            .First().Result;
        Core.Logging.LogScrubber.RegisterSecret(best.Secret);
        return Task.FromResult(best);
    }

    static string UnreadableReason(string text, IReadOnlyDictionary<string, string> keywords)
    {
        var t = text.TrimStart();
        if (t.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return "it is not a valid postgres:// URI (multi-host URIs are not supported).";
        if (t.Contains("://", StringComparison.Ordinal)) return "only postgres:// URIs are read; paste a keyword string (Key=value;…) for other engines.";
        return keywords.Count == 0
            ? "it is not a list of Key=value settings (a setting without \"=\", or a quote that is not closed)."
            : "none of its keywords is a connection setting Querithm knows.";
    }

    /// <summary>Engine-specific fields reset as the editor does when the server type changes.</summary>
    static Profile Rebase(Profile p, Contract.Providers.IDbProvider provider) => p with
    {
        Engine = provider.Engine, Port = null, Auth = provider.Traits.DefaultAuth,
        TlsMode = TlsMode.Default, TrustCert = false, CaPath = null, Options = null,
    };

    const string GroupsKey = "profile.groups";

    public Task<ProfileGroup[]> GroupList() => Task.FromResult(s.Settings.Read(GroupsKey, Json.Context.ProfileGroupArray) ?? []);

    public Task<ProfileGroup[]> GroupSave(ProfileGroup g)
    {
        // PT8-3: the same text rules as a profile's group name (size, control and invisible characters), a known environment and a CSS color.
        if (Core.Sessions.ProfileRules.CheckGroup(g) is { } invalid)
            throw new QuerithmException("invalidGroup", invalid.Message, invalid.Field is { } field ? new Dictionary<string, string> { ["field"] = field } : null);
        var list = (s.Settings.Read(GroupsKey, Json.Context.ProfileGroupArray) ?? []).Where(x => !string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase)).Append(g)
            .OrderBy(x => x.Name).ToArray();
        s.Settings.Write(GroupsKey, list, Json.Context.ProfileGroupArray);
        return Task.FromResult(list);
    }

    public Task<ProfileGroup[]> GroupDelete(ProfileGroup g)
    {
        var list = (s.Settings.Read(GroupsKey, Json.Context.ProfileGroupArray) ?? []).Where(x => !string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        s.Settings.Write(GroupsKey, list, Json.Context.ProfileGroupArray);
        return Task.FromResult(list);
    }

    // ---- sessions ----
    public Task<SessionOpenResult> SessionOpen(SessionOpenParams p) => Task.FromResult(s.Sessions.Open(p));

    /// <summary>
    /// Phase D0c: revoking a file grant closes every session on it, but never one with an open or unknown transaction or a
    /// running query (the tab-close Commit / Roll back / Cancel prompt handles those first, §6 rule 5). Each session's
    /// execution lock is taken before anything closes, so no query starts in between.
    /// </summary>
    public async Task<GrantRevokeResult> GrantRevoke(GrantRevokeParams p)
    {
        if (string.IsNullOrEmpty(p?.ProfileId)) throw QuerithmException.Invalid("grant.revoke needs a profileId.");
        var closed = await CloseProfileSessionsAsync(p.ProfileId, () => new QuerithmException("grantBusy",
            "A tab on this file has an open transaction or a running query. Commit or roll back (or cancel the query) and close the tab first."));
        s.Sessions.ForgetFileGrant(p.ProfileId);
        s.Objects.InvalidateCapabilities();
        return new GrantRevokeResult { ClosedSessions = closed };
    }

    /// <summary>
    /// Closes every session of the profile, or none: refused with <paramref name="busy"/> while any has an open or unknown
    /// transaction or a running query (the tab-close Commit / Roll back / Cancel prompt handles those first, §6 rule 5).
    /// Each session's execution lock is taken before anything closes, so no query starts in between.
    /// </summary>
    async Task<int> CloseProfileSessionsAsync(string profileId, Func<QuerithmException> busy)
    {
        var affected = s.Sessions.All.Where(x => x.Profile.Id == profileId && !x.Closed).ToList();
        var locked = new List<Core.Sessions.Session>();
        try
        {
            foreach (var x in affected)
            {
                if (!x.ExecLock.Wait(0)) throw busy();
                locked.Add(x);
                if (x.ActiveExecutionId != null || x.TxState != TxState.None) throw busy();
            }
            foreach (var x in affected)
            {
                await s.Trace.StopForSessionAsync(x.Id);
                await s.Sessions.CloseAsync(new SessionCloseParams { SessionId = x.Id, Force = true }, lockHeld: true);
                s.Executions.CloseForSession(x.Id);
            }
        }
        finally
        {
            foreach (var x in locked) x.ExecLock.Release();
        }
        return affected.Count;
    }

    public async Task SessionClose(SessionCloseParams p)
    {
        await s.Trace.StopForSessionAsync(p.SessionId);
        await s.Sessions.CloseAsync(p);
        s.Executions.CloseForSession(p.SessionId);
    }

    public Task<ChangeDatabaseResult> SessionChangeDatabase(ChangeDatabaseParams p) => Interactive("Switching the database", ct => s.Sessions.ChangeDatabaseAsync(p, ct));
    public Task<SessionState> SessionStateGet(SessionRef p) => Interactive("Reading the session state", ct => s.Sessions.StateAsync(p.SessionId, ct));
    public Task<Capabilities> SessionCapabilities(SessionRef p) => Interactive("Reading what the server supports", ct => s.Objects.CapabilitiesAsync(p, ct));

    // ---- queries ----
    public Task<QueryExecuteResult> QueryExecute(QueryExecuteParams p) => Task.FromResult(s.Executions.Execute(p));
    public Task<CancelResult> QueryCancel(ExecutionRef p) => Task.FromResult(s.Executions.Cancel(p.ExecutionId));
    public Task<CellResult> QueryCell(CellParams p) => Task.FromResult(s.Executions.Cell(p));

    public Task QueryClose(ExecutionRef p)
    {
        s.Executions.Close(p.ExecutionId);
        return Task.CompletedTask;
    }

    // ---- grid editing ----
    public Task<RowTokensResult> DataRowTokens(RowTokensParams p) => Interactive("Reading the edited rows' versions", ct => s.DataEditor.RowTokensAsync(p, ct));
    public Task<GridPreviewResult> DataPreviewPatch(GridPatchParams p) => Task.Run(() => s.DataEditor.Preview(p));
    public Task<GridApplyResult> DataApplyPatch(GridPatchParams p) => s.DataEditor.ApplyAsync(p);

    // ---- import ----
    public Task<ImportInspectResult> ImportInspect(ImportInspectParams p) => s.Imports.InspectAsync(p, OwnBound);
    public Task<ImportTargetResult> ImportCheckTarget(ImportTargetParams p) => Task.Run(() => s.Imports.CheckTarget(p));
    public Task<ImportRunResult> ImportRun(ImportRunParams p) => s.Imports.RunAsync(p);
    public Task<ImportCancelResult> ImportCancel(ImportJobRef p) => Task.FromResult(s.Imports.Cancel(p));

    // ---- schema compare ----
    public Task<SchemaCompareResult> SchemaCompareRun(SchemaCompareParams p) => Bounded("The schema compare", LongDeadline, ct => s.SchemaCompare.RunAsync(p, ct));
    public Task<CompareObjectSql> SchemaCompareObjectSql(CompareObjectParams p) => Task.Run(() => s.SchemaCompare.ObjectSql(p));
    public Task<SchemaMigrationResult> SchemaCompareMigrationSql(SchemaMigrationParams p) => Task.Run(() => s.SchemaCompare.MigrationSql(p));
    public Task<SchemaModelFile> SchemaCompareExportModel(SchemaExportParams p) => Bounded("Exporting the schema model", LongDeadline, ct => s.SchemaCompare.ExportAsync(p, ct));

    // ---- data compare ----
    public Task<DataCompareStart> DataCompareStart(DataCompareParams p) => Interactive("Starting the data compare", ct => s.DataCompare.StartAsync(p, ct));
    public Task<CancelResult> DataCompareCancel(DataCompareRef p) => Task.FromResult(s.DataCompare.Cancel(p));
    public Task<DataSyncSqlResult> DataCompareSyncSql(DataSyncSqlParams p) => Task.Run(() => s.DataCompare.SyncSql(p));

    // ---- find usages / rename ----
    public Task<SymbolAtResult> RefactorSymbolAt(SymbolAtParams p) => Task.Run(() => s.Refactor.SymbolAt(p));
    public Task<FindUsagesResult> RefactorFindUsages(FindUsagesParams p) => Bounded("Find usages", LongDeadline, ct => s.Refactor.FindUsagesAsync(p, ct));
    public Task<RenamePreviewResult> RefactorPreviewRename(RenameParams p) => Bounded("The rename preview", LongDeadline, ct => s.Refactor.PreviewRenameAsync(p, ct));

    public Task<SessionState> TxBegin(SessionRef p) => Interactive("BEGIN", ct => s.Sessions.TxAsync(p.SessionId, "begin", ct));
    // No deadline: a COMMIT cut short by one has an unknown outcome (SessionManager.TxEndLimit and the session's close bound them).
    public Task<SessionState> TxCommit(SessionRef p) => s.Sessions.TxAsync(p.SessionId, "commit", OwnBound);
    public Task<SessionState> TxRollback(SessionRef p) => s.Sessions.TxAsync(p.SessionId, "rollback", OwnBound);

    // ---- metadata ----
    public Task<DatabaseInfo[]> MetaDatabases(SessionRef p) => Interactive("Listing the databases", ct => s.Objects.DatabasesAsync(p, ct));
    public Task<SchemaSnapshot> MetaSnapshot(SnapshotParams p) => Task.Run(() => s.Metadata.Snapshot(p));
    public Task<SnapObject[]> MetaObjects(ObjectsParams p) => Task.Run(() => s.Metadata.Objects(p));
    public Task<JobRef> MetaRefresh(RefreshParams p) => Task.FromResult(s.Metadata.Refresh(p));
    public Task<ObjectDetails> MetaObjectDetails(ObjectDetailsParams p) => Task.Run(() => s.Metadata.Details(p));
    public Task<DefinitionResult> MetaDefinition(DefinitionParams p) => Interactive("Reading the definition", ct => s.Objects.EditableDefinitionAsync(p, ct));
    public Task<Dependency[]> MetaDependencies(DefinitionParams p) => Interactive("Reading the dependencies", ct => s.Objects.DependenciesAsync(p, ct));
    public Task<ObjectRef[]> MetaSearch(SearchParams p) => Task.Run(() => s.Metadata.Search(p));
    public Task<SqlText> MetaScript(ScriptParams p) => Interactive("Scripting the object", ct => s.Objects.ScriptAsync(p, ct));
    public Task<RowCount[]> MetaRowCounts(RowCountsParams p) => Interactive("Loading row counts", ct => s.Objects.RowCountsAsync(p, ct));

    // ---- language ----
    public Task<CompletionItem[]> LangComplete(CompleteParams p) => s.Language.CompleteAsync(p);
    public Task<Diagnostic[]> LangDiagnostics(DiagnosticsParams p) => s.Language.DiagnosticsAsync(p);
    public Task<FormatResult> LangFormat(FormatParams p) => s.Language.FormatAsync(p);
    public Task<SignatureHelp> LangSignature(SignatureParams p) => s.Language.SignatureAsync(p);
    public Task<FoldingRange[]> LangFolding(FoldingParams p) => s.Language.FoldingAsync(p);
    public Task<Classification> LangClassify(ClassifyParams p) => s.Language.ClassifyAsync(p);
    public Task LangForget(LangForgetParams p)
    {
        s.Language.Forget(p.DocId);
        return Task.CompletedTask;
    }

    // ---- plans / trace / debug ----
    /// <summary>Returns at once: the plan runs as an execution (query.plan, query.done; query.cancel ends it, §11).</summary>
    public Task<QueryExecuteResult> PlanGet(PlanParams p) => Task.FromResult(s.Executions.Plan(p));
    // CPU-bound (up to 10 MB of text): off the RPC dispatcher thread.
    public Task<PlanParseResult> PlanParse(PlanParseParams p) => Task.Run(() => Core.Metadata.PlanParsing.Parse(s.Sessions.Providers, p));
    public Task<TraceRef> TraceStart(TraceStartParams p) => Interactive("Starting the trace", ct => s.Trace.StartAsync(p, ct));
    public Task TraceStop(TraceRef p) => s.Trace.StopAsync(p);
    public Task<DebugAttachResult> DebugAttach(DebugAttachParams p) => Interactive("Starting the debugger (the call did not reach the function)", ct => s.Debug.AttachAsync(p, ct));
    public Task DebugSetBreakpoint(DebugBreakpointParams p) => Interactive("Setting the breakpoint", ct => s.Debug.SetBreakpointAsync(p, ct));
    public Task DebugDropBreakpoint(DebugBreakpointParams p) => Interactive("Removing the breakpoint", ct => s.Debug.DropBreakpointAsync(p, ct));
    public Task DebugStep(DebugStepParams p) => s.Debug.StepAsync(p, OwnBound);
    public Task<DebugSource> DebugSourceGet(DebugSourceParams p) => Interactive("Reading the function source", ct => s.Debug.SourceAsync(p, ct));
    public Task DebugDetach(DebugRef p) => s.Debug.DetachAsync(p);

    // ---- history / settings ----
    public Task<HistoryEntry[]> HistoryList(HistoryListParams p) => Task.Run(() => s.History.List(p));
    public Task<HistoryEntry[]> HistorySearch(HistorySearchParams p) => Task.Run(() => s.History.Search(p));
    public Task<HistoryEntry> HistoryGet(HistoryGetParams p) => Task.Run(() => s.History.Get(p.Id));
    public Task<HistoryRetention> HistoryRetentionGet() => Task.Run(s.History.Retention);
    public Task<HistoryRetention> HistorySetRetention(HistoryRetention p) => Task.Run(() => s.History.SetRetention(p));
    public Task<SettingEntry[]> SettingsGet(SettingsGetParams p) => Task.FromResult(s.Settings.Get(p.Keys));

    public Task SettingsSet(SettingEntry p)
    {
        s.Settings.Set(p.Key, p.ValueJson);
        return Task.CompletedTask;
    }

    // ---- snippets (user data in store.db, never logged) ----
    public Task<Snippet[]> SnippetList() => Task.Run(s.Snippets.List);

    public Task<Snippet> SnippetSave(SnippetSaveParams p)
    {
        if (p?.Snippet == null) throw QuerithmException.Invalid("snippet.save needs a snippet.");
        return Task.Run(() => s.Snippets.Save(p.Snippet));
    }

    public Task SnippetDelete(SnippetRef p)
    {
        if (string.IsNullOrEmpty(p?.Id)) throw QuerithmException.Invalid("snippet.delete needs an id.");
        return Task.Run(() => s.Snippets.Delete(p.Id));
    }

    // ---- activity monitor (Phase B) ----
    public Task<MonitorStartResult> MonitorStart(MonitorStartParams p) => Task.FromResult(s.Monitors.Start(p));
    public Task MonitorStop(MonitorRef p) => s.Monitors.StopAsync(p.MonitorId);
    public Task MonitorSetPanes(MonitorSetPanesParams p) { s.Monitors.SetPanes(p); return Task.CompletedTask; }
    public Task MonitorSetInterval(MonitorSetIntervalParams p) { s.Monitors.SetInterval(p); return Task.CompletedTask; }
    public Task MonitorSetTopQueries(MonitorTopQueriesParams p) { s.Monitors.SetTopQueries(p); return Task.CompletedTask; }
    public Task MonitorPause(MonitorRef p) { s.Monitors.Get(p.MonitorId).Pause(); return Task.CompletedTask; }
    public Task MonitorResume(MonitorRef p) { s.Monitors.Get(p.MonitorId).Resume(); return Task.CompletedTask; }
    public Task<MonitorTextsResult> MonitorTexts(MonitorTextsParams p) => Task.FromResult(s.Monitors.Texts(p));
    public Task<KillResult> MonitorKill(MonitorKillParams p) => s.Monitors.KillAsync(p);
    public Task<KillPreview> MonitorKillPreview(MonitorSessionParams p) => s.Monitors.KillPreviewAsync(p);
    public Task<Diagnosis> MonitorDiagnose(MonitorSessionParams p) => s.Monitors.DiagnoseAsync(p);
    public Task<WaitInfo> MonitorWaitInfo(WaitInfoParams p) => Task.FromResult(s.Monitors.WaitInfo(p));
    public Task<DeadlocksResult> MonitorDeadlocks(DeadlocksParams p) => s.Monitors.DeadlocksAsync(p);
    public Task<MonitorFileResult> MonitorDeadlockXml(DeadlockXmlParams p) => s.Monitors.DeadlockXmlAsync(p);
    public Task<RequestDetail> MonitorRequestDetail(MonitorSessionParams p) => s.Monitors.RequestDetailAsync(p);
    public Task<HistoryResult> MonitorHistory(HistoryParams p) => Task.FromResult(s.Monitors.History(p));
    public Task MonitorHistorySettings(HistorySettingsParams p) { s.Monitors.HistorySettings(p); return Task.CompletedTask; }
    public Task<MonitorFileResult> MonitorExport(MonitorExportParams p) => Task.Run(() => s.Monitors.Export(p));

    public Task<QueryExecuteResult> BenchSynthetic(SyntheticParams p) => Task.FromResult(s.Executions.Synthetic(p));
}
