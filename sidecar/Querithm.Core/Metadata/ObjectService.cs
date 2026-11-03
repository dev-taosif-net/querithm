using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Sessions;

namespace Querithm.Core.Metadata;

/// <summary>Server-side object operations on a session: definitions, dependencies, scripting, capabilities (§10). Plans run as executions (§11).</summary>
public sealed class ObjectService(SessionManager sessions, MetadataService metadata)
{
    readonly ConcurrentDictionary<string, Capabilities> _caps = new();

    async Task<T> WithConnectionAsync<T>(Session s, Func<PhysicalConnection, Task<T>> work, CancellationToken ct, string? database = null, bool lockHeld = false)
    {
        if (s.SessionOwned)
        {
            // Phase D0d: the session's own connection only — never a pool connection, and after its execution.
            if (database != null && !string.Equals(database, s.Database, StringComparison.Ordinal))
                throw new QuerithmException("unsupported", "This connection reaches one database; open the other one in a tab of its own.");
            await using var lease = await sessions.LeaseAsync(s, ct, lockHeld).ConfigureAwait(false);
            return await sessions.PinnedReadAsync(s, lease.Conn, () => work(lease.Conn), ct).ConfigureAwait(false);
        }
        if (database != null && !string.Equals(database, s.Database, StringComparison.Ordinal) && s.Pinned == null)
        {
            // Object in another database of the same server: use a pool connection for that database.
            var pool = sessions.PoolFor(s.Profile, s.Provider, database);
            var c = await pool.BorrowAsync(database, null, ct).ConfigureAwait(false);
            try
            {
                using var stuck = CloseIfStuck(c, s.Provider, ct);
                return await work(c).ConfigureAwait(false);
            }
            finally { await pool.ReturnAsync(c).ConfigureAwait(false); }
        }
        var (conn, borrowed) = await sessions.AcquireAsync(s, null, ct).ConfigureAwait(false);
        try
        {
            if (!borrowed) return await sessions.PinnedReadAsync(s, conn, () => work(conn), ct).ConfigureAwait(false); // the user's transaction lives there
            using var stuck = CloseIfStuck(conn, s.Provider, ct);
            return await work(conn).ConfigureAwait(false);
        }
        finally { await sessions.ReleaseAsync(s, conn, borrowed).ConfigureAwait(false); }
    }

    /// <summary>How long a cancelled catalog read may take to stop before its borrowed connection is closed under it. Settable for tests.</summary>
    internal TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The RPC's deadline fired and the read is still on its connection <see cref="StopGrace"/> later: where closing aborts
    /// the command (SQL Server, whose driver waits for the server to acknowledge a cancel for good) the borrowed connection
    /// is closed, so the read fails and the pool discards it instead of the slot staying taken. Never a pinned or
    /// session-owned connection: the user's transaction or database lives there (the RPC still answers at its deadline).
    /// </summary>
    IDisposable? CloseIfStuck(PhysicalConnection c, IDbProvider provider, CancellationToken ct)
    {
        if (!provider.CloseAbortsCommand || !ct.CanBeCanceled) return null;
        var finished = new CancellationTokenSource();
        var registration = ct.Register(() => _ = Task.Delay(StopGrace, finished.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            try { c.Db.Close(); } catch { /* the read fails either way */ }
        }, TaskScheduler.Default));
        return new Stopper(registration, finished);
    }

    sealed class Stopper(CancellationTokenRegistration registration, CancellationTokenSource finished) : IDisposable
    {
        public void Dispose()
        {
            registration.Dispose();
            finished.Cancel();
            finished.Dispose();
        }
    }

    /// <summary>Runs <paramref name="work"/> on a connection of the session, on <paramref name="database"/> when it is another one.</summary>
    public Task<T> OnConnectionAsync<T>(string sessionId, string? database, Func<System.Data.Common.DbConnection, Task<T>> work, CancellationToken ct)
    {
        var s = sessions.Get(sessionId);
        if (database != null && s.Pinned != null && !s.SessionOwned && !string.Equals(database, s.Database, StringComparison.Ordinal))
            throw new QuerithmException("txOpen", "The session has an open transaction on another database. Commit or roll it back first.");
        return WithConnectionAsync(s, c => work(c.Db), ct, database);
    }

    public static string Hash(string definition)
    {
        // Line endings and trailing whitespace differ between catalog output and editors; hash the normalized text.
        var normalized = string.Join("\n", definition.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
    }

    public async Task<DefinitionResult> DefinitionAsync(DefinitionParams p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        var def = await WithConnectionAsync(s, c => s.Provider.Metadata.GetDefinitionAsync(c.Db, p.Object, ct), ct, p.Object.Database).ConfigureAwait(false)
                  ?? throw new QuerithmException("noDefinition", $"No definition is available for {p.Object.Schema}.{p.Object.Name}: it may be encrypted, not visible to this login (VIEW DEFINITION), or not a module.");
        if (p.Object.ObjectId is { } oid && (p.Object.Database ?? s.Database) is { } db)
            metadata.Store.CacheDefinition(s.Profile.Id, db, oid, def);
        return new DefinitionResult { Sql = def, DefinitionHash = Hash(def), ObjectId = p.Object.ObjectId };
    }

    /// <summary>Definition rewritten for editing: CREATE OR ALTER / CREATE OR REPLACE (§10 Open for edit).</summary>
    public async Task<DefinitionResult> EditableDefinitionAsync(DefinitionParams p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        var def = await DefinitionAsync(p, ct).ConfigureAwait(false);
        var caps = await CapabilitiesAsync(new SessionRef { SessionId = s.Id }, ct).ConfigureAwait(false);
        return def with { Sql = s.Provider.Metadata.ToEditableScript(def.Sql, caps) };
    }

    public async Task<Dependency[]> DependenciesAsync(DefinitionParams p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        return await WithConnectionAsync(s, c => s.Provider.Metadata.GetDependenciesAsync(c.Db, p.Object, ct), ct, p.Object.Database).ConfigureAwait(false);
    }

    public async Task<SqlText> ScriptAsync(ScriptParams p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        var sql = await WithConnectionAsync(s, c => s.Provider.Scripter.ScriptAsync(c.Db, p.Object, p.Kind, ct), ct, p.Object.Database).ConfigureAwait(false);
        return new SqlText { Sql = sql };
    }

    public async Task<RowCount[]> RowCountsAsync(RowCountsParams p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        if (p.ObjectIds.Length == 0) return [];
        return await WithConnectionAsync(s, c => s.Provider.Metadata.GetRowCountsAsync(c.Db, p.ObjectIds, ct), ct, p.Database).ConfigureAwait(false);
    }

    public async Task<DatabaseInfo[]> DatabasesAsync(SessionRef p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        return await WithConnectionAsync(s, c => s.Provider.ListDatabasesAsync(c.Db, ct), ct).ConfigureAwait(false);
    }

    public async Task<Capabilities> CapabilitiesAsync(SessionRef p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        // Capabilities depend on the product, version, permissions and access mode: a new credential epoch (secret, profile
        // edit, disconnect, a changed file grant) reads them again (Phase D0a).
        var key = $"{s.Profile.Id}|{s.Database}|{sessions.CredentialEpoch(s.Profile.Id)}";
        if (_caps.TryGetValue(key, out var cached)) return cached;
        var caps = await WithConnectionAsync(s, async c =>
        {
            var basic = await s.Provider.GetCapabilitiesAsync(c.Db, ct).ConfigureAwait(false);
            if (s.Provider.Trace is { } trace) basic = basic with { Trace = await trace.CheckAsync(c.Db, ct).ConfigureAwait(false) };
            if (s.Provider.Debugger is { } dbg) basic = basic with { Debugger = await dbg.CheckAsync(c.Db, ct).ConfigureAwait(false) };
            return FeatureGuard.Apply(s.Provider, basic with { Product = basic.Product ?? s.Provider.Product(c.Db) });
        }, ct).ConfigureAwait(false);
        _caps[key] = caps;
        return caps;
    }

    public void InvalidateCapabilities() => _caps.Clear();
}

/// <summary>lang.*: routes to the dialect service; CPU-bound work runs off the RPC reader thread.</summary>
public sealed class LanguageService(ProviderRegistry providers, ISchemaSource schema)
{
    IDialectService For(Dialect d) =>
        providers.ForDialect(d)?.Language ?? throw QuerithmException.Invalid($"No language service for dialect '{d}'.");

    /// <summary>Phase D0d: completion sees the tab's own view (an in-memory database, temporary tables).</summary>
    ISchemaSource For(string? sessionId) => sessionId != null && schema is SchemaCache cache ? new ScopedSchemaSource(cache, sessionId) : schema;

    public Task<CompletionItem[]> CompleteAsync(CompleteParams p) => Task.Run(() => For(p.Dialect).Complete(p, For(p.SessionId)));
    public Task<Diagnostic[]> DiagnosticsAsync(DiagnosticsParams p) => Task.Run(() => For(p.Dialect).Diagnostics(p));
    public Task<FormatResult> FormatAsync(FormatParams p) => Task.Run(() => For(p.Dialect).Format(p));
    public Task<SignatureHelp> SignatureAsync(SignatureParams p) => Task.Run(() => For(p.Dialect).Signature(p, For(p.SessionId)));
    public Task<FoldingRange[]> FoldingAsync(FoldingParams p) => Task.Run(() => For(p.Dialect).Folding(p));
    public Task<Classification> ClassifyAsync(ClassifyParams p) => Task.Run(() => For(p.Dialect).Classify(p.Text));

    /// <summary>M12, lang.forget: every engine's caches for the document, since a tab's dialect follows its connection.</summary>
    public void Forget(string docId)
    {
        if (string.IsNullOrEmpty(docId) || docId.Length > MaxDocIdLength) throw QuerithmException.Invalid($"docId must be 1–{MaxDocIdLength} characters.");
        foreach (var language in providers.All.Select(p => p.Language).Distinct()) language.Forget(docId);
    }

    /// <summary>A webview model key (a tab id, or a notebook id and cell id); far longer is not a document id.</summary>
    public const int MaxDocIdLength = 512;
}
