using System.Data;
using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;

namespace Querithm.Tests;

/// <summary>MetadataRefresher (§8) scheduling, with a provider whose catalog read can be held open.</summary>
public class MetadataRefreshTests
{
    internal sealed class FakeConnection : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "db";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    /// <summary>Counts catalog reads; the first one waits for <see cref="Gate"/>.</summary>
    internal sealed class SlowMetadata : IMetadataProvider
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads;

        public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Reads) == 1) await Gate.Task.WaitAsync(ct);
            return [];
        }

        public Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => Task.FromResult(new MetadataBundle([], [], [], []));
        public Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public string ToEditableScript(string definition, Capabilities caps) => definition;
        public Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Connections that never hold a transaction (helpers return them straight to the pool).</summary>
    internal sealed class NoTransactions : ITransactionControl
    {
        public TransactionTraits Traits { get; } = new()
        {
            AutocommitDefault = true, DdlCommitsImplicitly = false, Savepoints = false, ReleaseSavepoint = false, ReadOnlyTransactions = false,
            FailedStatementAbortsTransaction = false, UserStatements = [],
        };
        public Task BeginAsync(DbConnection c, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> BeginReadOnlyAsync(DbConnection c, CancellationToken ct) => Task.FromResult(false);
        public Task CommitAsync(DbConnection c, CancellationToken ct) => throw new NotSupportedException();
        public Task RollbackAsync(DbConnection c, CancellationToken ct) => Task.CompletedTask;
        public Task SavepointAsync(DbConnection c, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task RollbackToSavepointAsync(DbConnection c, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task ReleaseSavepointAsync(DbConnection c, string name, CancellationToken ct) => Task.CompletedTask;
        public Task<TxState> StateAsync(DbConnection c, CancellationToken ct) => Task.FromResult(TxState.None);
    }

    internal sealed class FakeProvider(IMetadataProvider metadata, Exception? openFails = null) : IDbProvider
    {
        public Engine Engine => Engine.Pg;
        public Dialect Dialect => Dialect.Pg;
        public bool DatabaseScopedConnections => true;
        public IDialectService Language => throw new NotSupportedException();
        public bool StopOnBatchError => true;
        public IMetadataProvider Metadata => metadata;
        public IPlanProvider Plans => throw new NotSupportedException();
        public IObjectScripter Scripter => throw new NotSupportedException();
        public ITraceProvider? Trace => null;
        public IDebugProvider? Debugger => null;
        public IDataEditDialect DataEdit => throw new NotSupportedException();
        public IImportDialect Import => throw new NotSupportedException();
        public ISchemaCompareDialect? SchemaCompare => null;
        public IDataCompareDialect? DataCompare => null;
        public IReferenceFinder? References => null;
        public IActivityProvider? Activity => null;
        public string? FeatureRefusal(Feature feature) => null;
        public string? ValidateProfile(Profile profile) => null;
        public ProductInfo Product(DbConnection connection) => new() { Name = "Fake" };
        public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.Pooled;
        public EngineTraits Traits { get; } = new() { Name = "Fake", DefaultDatabase = "postgres", DefaultSchema = "public", DefaultAuth = AuthKind.Password, AuthKinds = Enum.GetValues<AuthKind>(), Locations = Enum.GetValues<LocationKind>(), NameMatch = StringComparer.Ordinal };
        public string BuildConnectionString(Profile profile, string? secret, string? database) => "";
        public (Profile Profile, string? Note) RouteThrough(Profile profile, string host, int port) => (profile, null);
        public (string Host, int Port, string? Refusal) TunnelTarget(Profile profile) => (profile.Host ?? "", profile.Port ?? 0, null);
        public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis) => throw new NotSupportedException();
        public TracedOrigins ResultOrigins(IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot, string? scope) => TracedOrigins.None;
        public Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct) =>
            openFails != null ? Task.FromException<DbConnection>(openFails) : Task.FromResult<DbConnection>(new FakeConnection());
        public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct) => Task.CompletedTask;
        public string? CurrentDatabase(DbConnection connection) => "db";
        public string ServerVersion(DbConnection connection) => "Fake 1";
        public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<int?>(1);
        public ITransactionControl Transactions { get; } = new NoTransactions();
        public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage) => throw new NotSupportedException();
        public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null) => [new ProviderMessage(Severity.Error, ex.Message)];
        public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);
        public void AddParameter(DbCommand command, QueryParameter parameter) => throw new NotSupportedException();
        public ColumnHint GetColumnHint(DbColumn column) => ColumnHint.Default;
        public string? ReadAsString(DbDataReader reader, int ordinal) => null;
        public bool IsPlanResultSet(DbDataReader reader) => false;
        public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct) => throw new NotSupportedException();
        public Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(Array.Empty<DatabaseInfo>());
        public Task ValidateAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;
        public void Cancel(DbCommand command) { }
    }

    [Fact]
    public async Task A_refresh_the_last_run_left_unfinished_does_not_block_every_later_one()
    {
        // Regression: the app closed (or the engine restarted) while a refresh ran, so its row stayed 'refreshing' in
        // store.db. meta.snapshot starts no refresh for a 'refreshing' database and the webview refuses a manual one, so
        // that database's metadata was frozen — and tables changed since stayed read-only in the grid — until some DDL
        // ran through the app.
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" });
        var metaStore = new MetadataStore(store);
        metaStore.SetStatus(profile.Id, "db", SnapshotStatus.Refreshing, refreshedAt: 1234); // read before, then interrupted
        metaStore.SetStatus(profile.Id, "never", SnapshotStatus.Refreshing); // its first read was interrupted
        metaStore.SetStatus(profile.Id, "fresh", SnapshotStatus.Fresh, refreshedAt: 1);
        metaStore.SetStatus(profile.Id, "failed", SnapshotStatus.Error, "no route");

        var slow = new SlowMetadata();
        slow.Gate.SetResult();
        var sessions = new SessionManager(profiles, new ProviderRegistry([new FakeProvider(slow)]), new CapturingNotifier(), Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var metadata = new MetadataService(sessions, metaStore, new SchemaCache(metaStore), new CapturingNotifier(), Log.Null);

        Assert.Equal(SnapshotStatus.Stale, metaStore.GetSnapshotRow(profile.Id, "db")!.Status);
        Assert.Equal(SnapshotStatus.Empty, metaStore.GetSnapshotRow(profile.Id, "never")!.Status);
        Assert.Equal(SnapshotStatus.Fresh, metaStore.GetSnapshotRow(profile.Id, "fresh")!.Status); // untouched
        Assert.Equal((SnapshotStatus.Error, "no route"), (metaStore.GetSnapshotRow(profile.Id, "failed")!.Status, metaStore.GetSnapshotRow(profile.Id, "failed")!.Error));

        // The first read of the snapshot refreshes it, as for any stale database.
        metadata.Snapshot(new SnapshotParams { ProfileId = profile.Id, Database = "db" });
        await metadata.WaitAsync(profile.Id, "db").WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, slow.Reads);
        Assert.Equal(SnapshotStatus.Fresh, metaStore.GetSnapshotRow(profile.Id, "db")!.Status);
    }

    [Fact]
    public async Task Refreshed_async_waits_for_the_read_it_asked_for_and_reports_a_failure()
    {
        // Grid editing decides a result set again after this: it must cover a catalog read that starts after the call,
        // also when one is already running (that one may have read the catalog before the change).
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" });
        var slow = new SlowMetadata();
        var sessions = new SessionManager(profiles, new ProviderRegistry([new FakeProvider(slow)]), new CapturingNotifier(), Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var metaStore = new MetadataStore(store);
        var metadata = new MetadataService(sessions, metaStore, new SchemaCache(metaStore), new CapturingNotifier(), Log.Null);
        var session = sessions.Get(sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Secret = "x" }).SessionId);

        metadata.Refresh(new RefreshParams { ProfileId = profile.Id, Database = "db" });
        while (Volatile.Read(ref slow.Reads) == 0) await Task.Delay(5);
        using var limit = new CancellationTokenSource(TestTime.Of(TimeSpan.FromSeconds(10)));
        var refreshed = metadata.RefreshedAsync(session, "db", limit.Token);
        await Task.Delay(50);
        Assert.False(refreshed.IsCompleted); // still behind the running read
        slow.Gate.SetResult();
        Assert.Null(await refreshed);
        Assert.Equal(2, slow.Reads); // the running one, then the one asked for

        // A caller's limit ends the wait, never the refresh itself.
        using var none = new CancellationTokenSource();
        none.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => metadata.RefreshedAsync(session, "db", none.Token));
        await metadata.WaitAsync(profile.Id, "db").WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));

        // A read that fails is reported in words, scrubbed.
        var failing = new SessionManager(profiles, new ProviderRegistry([new FakeProvider(slow, new InvalidOperationException("no route to host; Password=hunter2"))]), new CapturingNotifier(), Log.Null);
        failing.RememberSecret(profile.Id, "x");
        var broken = new MetadataService(failing, metaStore, new SchemaCache(metaStore), new CapturingNotifier(), Log.Null);
        var why = await broken.RefreshedAsync(session, "db", limit.Token);
        Assert.StartsWith("no route to host", why);
        Assert.DoesNotContain("hunter2", why);

        // A pooled tab with its transaction open: the read on another connection cannot see that transaction's DDL, so it is
        // no final answer (regression: BEGIN; CREATE TABLE x …; SELECT * FROM x stayed read-only, "checked against freshly
        // read metadata", for the settle window after the COMMIT).
        session.TxState = TxState.Open;
        var open = await metadata.RefreshedAsync(session, "db", limit.Token);
        Assert.Contains("transaction is open", open);
        session.TxState = TxState.None;
        Assert.Null(await metadata.RefreshedAsync(session, "db", limit.Token));
    }

    [Fact]
    public async Task A_refresh_asked_for_while_one_runs_is_queued_not_dropped()
    {
        // Regression: DDL right after DDL asked for a refresh while the first one ran; the request was dropped, and the
        // running refresh had read the catalog before the second change, so the new table never appeared.
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" });
        var slow = new SlowMetadata();
        var sessions = new SessionManager(profiles, new ProviderRegistry([new FakeProvider(slow)]), new CapturingNotifier(), Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var metaStore = new MetadataStore(store);
        var metadata = new MetadataService(sessions, metaStore, new SchemaCache(metaStore), new CapturingNotifier(), Log.Null);
        var p = new RefreshParams { ProfileId = profile.Id, Database = "db" };

        var first = metadata.Refresh(p);
        while (Volatile.Read(ref slow.Reads) == 0) await Task.Delay(5);
        var second = metadata.Refresh(p);
        var third = metadata.Refresh(p);
        Assert.Equal(first.JobId, second.JobId);
        slow.Gate.SetResult();
        await metadata.WaitAsync(profile.Id, "db").WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        // One more read after the running one: the two later requests coalesce into a single re-run.
        Assert.Equal(2, slow.Reads);
        Assert.Equal(third.JobId, first.JobId);
        // Once idle, a new request starts a new job.
        var fourth = metadata.Refresh(p);
        await metadata.WaitAsync(profile.Id, "db").WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(first.JobId, fourth.JobId);
        Assert.Equal(3, slow.Reads);
    }
}
