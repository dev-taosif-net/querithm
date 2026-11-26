using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Code audit follow-up 2026-11-26, sessions and metadata: a refresh deferred by DDL inside a transaction is never lost —
/// not to a store that cannot be written, a forced close, a COMMIT with no pinned connection or a lost in-memory
/// database — and the store drops a foreign key whose referenced table went. Engine-neutral (fakes): the same code path
/// serves every engine.
/// </summary>
public class AuditFollowUpSessionTests
{
    static readonly TimeSpan Wait = TestTime.Of(TimeSpan.FromSeconds(10));

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public SessionManager Sessions { get; }
        public ExecutionService Executions { get; }
        public MetadataService Metadata { get; }
        public MetadataStore Store { get; }
        public LocalStore Local { get; } = LocalStore.InMemory();
        public int Ended;
        readonly Profile _profile;

        public Rig(string? retention = null)
        {
            var profiles = new ProfileStore(Local);
            _profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u", PasswordRetention = retention });
            var notifier = new CapturingNotifier();
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), notifier, Log.Null);
            Sessions.RememberSecret(_profile.Id, "x");
            Store = new MetadataStore(Local);
            Metadata = new MetadataService(Sessions, Store, new SchemaCache(Store), notifier, Log.Null);
            Sessions.TransactionEnded += _ => Interlocked.Increment(ref Ended);
            Sessions.TransactionEnded += Metadata.OnTransactionEnded; // as the host wires it (RpcServer)
            Executions = new ExecutionService(Sessions, new CapturingFrames(), notifier, new HistoryStore(Local), Log.Null);
        }

        public Session Open()
        {
            var s = Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = _profile.Id, Secret = "x" }).SessionId);
            s.Database = "db";
            return s;
        }

        public async ValueTask DisposeAsync()
        {
            await Executions.DisposeAsync();
            await Sessions.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_store_that_cannot_be_written_does_not_lose_the_deferred_read()
    {
        // The Stale write ran before the deferral and threw (store busy or locked): the deferral was never registered, so
        // the COMMIT read nothing and the snapshot stayed without the new table.
        await using var rig = new Rig();
        var s = rig.Open();
        s.TxState = TxState.Open;
        using (var c = rig.Local.Open()) c.Cmd("DROP TABLE schema_snapshot").ExecuteNonQuery();
        Assert.Null(Record.Exception(() => rig.Metadata.OnDdlExecuted(s, "db", "db")));
        Assert.True(s.RefreshDeferred);
        Assert.True(s.DefersRefreshOf("db"));
    }

    [Fact]
    public async Task A_forced_close_of_a_transaction_that_deferred_a_read_still_reads()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        s.TxState = TxState.Open;
        Assert.True(s.DeferIfInTransaction(["db"]));
        await rig.Sessions.CloseAsync(new SessionCloseParams { SessionId = s.Id, Force = true });
        Assert.Equal(1, Volatile.Read(ref rig.Ended));
        Assert.False(s.RefreshDeferred); // taken: the handler read it

        // The handler reads a closed tab's deferral as the shared view (before, it returned at once on Closed).
        var t = rig.Open();
        t.TxState = TxState.Open;
        rig.Metadata.OnDdlExecuted(t, "db", "db");
        Assert.True(t.RefreshDeferred);
        t.Closed = true;
        rig.Metadata.OnTransactionEnded(t);
        await rig.Metadata.WaitAsync(t.Profile.Id, "db").WaitAsync(Wait);
        Assert.NotNull(rig.Store.GetSnapshotRow(t.Profile.Id, "db")); // a read ran (it writes the status)
    }

    [Fact]
    public async Task A_forced_close_that_forgets_the_password_drops_the_deferred_read_instead_of_failing_it()
    {
        // Gemini review 2026-11-26: the read started on the closed tab's deferral just as the close forgot the profile's
        // password (temporary retention, last tab): it could not sign in, marked the snapshot failed, and a strict server
        // counted the failed sign-in.
        await using var rig = new Rig(retention: "session");
        var s = rig.Open();
        s.TxState = TxState.Open;
        Assert.True(s.DeferIfInTransaction(["db"]));
        await rig.Sessions.CloseAsync(new SessionCloseParams { SessionId = s.Id, Force = true });
        Assert.Equal(0, Volatile.Read(ref rig.Ended));
        Assert.False(s.RefreshDeferred);
    }

    [Fact]
    public async Task Language_requests_without_params_are_invalid_not_internal_errors()
    {
        await using var h = new TestHost();
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.LangComplete(null!))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.LangForget(null!))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.LangFormat(null!))).Code);
    }

    [Fact]
    public void A_cleared_marker_never_equals_a_real_one()
    {
        var local = LocalStore.InMemory();
        var store = new MetadataStore(local);
        store.ApplyRefresh("p", "main", true, [new ObjectStamp(10, "main", "t", ObjectKind.Table, 0, null)], [], new MetadataBundle([], [], [], []), "1");
        Assert.Equal(0L, store.GetMarkers("p", "main")[10]); // a real marker of 0 (a hash can be)
        using (var c = local.Open()) c.Cmd("UPDATE obj SET modify_date = NULL").ExecuteNonQuery();
        Assert.Equal(long.MinValue, store.GetMarkers("p", "main")[10]); // cleared: unequal to 0, so the object is read again
    }

    [Fact]
    public void Completion_filtering_never_changes_the_dialects_array()
    {
        var item = DeferredDocs.Defer(new CompletionItem { Label = "t", Kind = CompletionKind.Table }, () => "doc");
        CompletionItem[] items = [item];
        var kept = CompletionFilter.Apply(items, "", 0);
        Assert.Same(item, items[0]);
        Assert.Equal("doc", kept[0].Documentation);
    }

    [Fact]
    public async Task A_commit_with_no_pinned_connection_ends_the_transaction_for_the_deferred_read()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        s.TxState = TxState.Open;
        Assert.True(s.DeferIfInTransaction(["db"]));
        await rig.Sessions.TxAsync(s.Id, "commit", CancellationToken.None);
        Assert.Equal(TxState.None, s.TxState);
        Assert.Equal(1, Volatile.Read(ref rig.Ended));
    }

    [Fact]
    public async Task A_lost_in_memory_database_drops_its_deferral_without_reading()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        s.TxState = TxState.Open;
        Assert.True(s.DeferIfInTransaction(["db"]));
        s.MemoryLost = true;
        s.TxState = TxState.None;
        rig.Metadata.OnTransactionEnded(s);
        Assert.False(s.RefreshDeferred);
        Assert.Null(rig.Store.GetSnapshotRow(s.Profile.Id, "db")); // nothing started a read
    }

    public static TheoryData<string> ImplicitCommitEngines() => new() { "mysql", "oracle" };

    [Theory, MemberData(nameof(ImplicitCommitEngines))]
    public async Task Ddl_that_commits_at_once_is_read_now_even_inside_a_transaction(string engine)
    {
        // Second review 2026-11-26: MySQL and Oracle commit DDL at once (CREATE TABLE t; START TRANSACTION; INSERT …), so
        // every connection sees it; deferring the read kept every tab's snapshot without t until the user committed.
        await using var rig = new Rig();
        rig.Provider.Transactions = engine == "mysql" ? new Querithm.Providers.MySql.MySqlTransactions() : new Querithm.Providers.Oracle.OracleTransactions();
        var s = rig.Open();
        s.TxState = TxState.Open;
        rig.Metadata.OnDdlExecuted(s, "db", "db");
        Assert.False(s.RefreshDeferred);
        await rig.Metadata.WaitAsync(s.Profile.Id, "db").WaitAsync(Wait);
        Assert.NotNull(rig.Store.GetSnapshotRow(s.Profile.Id, "db")); // a read ran
    }

    [Fact]
    public async Task Still_transactional_ddl_waits_for_the_commit()
    {
        await using var rig = new Rig(); // FakeTransactions: SQL Server's traits, DDL is transactional
        var s = rig.Open();
        s.TxState = TxState.Open;
        rig.Metadata.OnDdlExecuted(s, "db", "db");
        Assert.True(s.RefreshDeferred);
    }

    [Fact]
    public async Task A_session_closed_before_its_ddl_is_read_still_reads_the_shared_snapshot()
    {
        // Second review 2026-11-26: a file database's tab closed mid-run; the DDL's refresh asked for the closed session,
        // ScopeOf threw NotFound, and the shared snapshot stayed without the new table.
        await using var rig = new Rig();
        rig.Provider.Lifetime = ConnectionLifetime.SessionOwned;
        var s = rig.Open();
        s.Closed = true;
        Assert.Null(Record.Exception(() => rig.Metadata.OnDdlExecuted(s, "db", "db")));
        await rig.Metadata.WaitAsync(s.Profile.Id, "db").WaitAsync(Wait);
        Assert.NotNull(rig.Store.GetSnapshotRow(s.Profile.Id, "db")); // a read was asked for and ran
    }

    [Fact]
    public async Task A_lost_in_memory_database_is_not_read_after_its_ddl()
    {
        // Round-3 review 2026-11-26: the read leased the lost connection, failed, and wrote an Error over the tab's
        // "database lost" state on every DDL attempt.
        await using var rig = new Rig();
        rig.Provider.Lifetime = ConnectionLifetime.SessionOwned;
        var s = rig.Open();
        s.MemoryLost = true;
        rig.Metadata.OnDdlExecuted(s, "db", "db");
        await rig.Metadata.WaitAsync(s.Profile.Id, "db").WaitAsync(Wait);
        Assert.Null(rig.Store.GetSnapshotRow(s.Profile.Id, "db"));
    }

    [Fact]
    public async Task A_close_whose_discard_fails_still_raises_its_events()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        var closed = 0;
        rig.Sessions.SessionClosed += _ => Interlocked.Increment(ref closed);
        // Pin a connection whose pool discard throws.
        rig.Provider.Handler = (_, _) => Task.FromResult(FakeResult.None);
        var (conn, _) = await rig.Sessions.AcquireAsync(s, null, CancellationToken.None);
        s.Pinned = conn;
        s.TxState = TxState.Open;
        Assert.True(s.DeferIfInTransaction(["db"]));
        ((FakeDbConnection)conn.Db).DisposeGate = new TaskCompletionSource();
        ((FakeDbConnection)conn.Db).DisposeGate!.SetException(new InvalidOperationException("discard failed"));
        await Record.ExceptionAsync(() => rig.Sessions.CloseAsync(new SessionCloseParams { SessionId = s.Id, Force = true }));
        Assert.Equal(1, closed);
        Assert.Equal(1, Volatile.Read(ref rig.Ended));
    }

    [Fact]
    public void A_foreign_key_to_a_removed_table_stays_until_its_own_table_changes()
    {
        var store = new MetadataStore(LocalStore.InMemory());
        static ObjectStamp T(long id, string name) => new(id, "main", name, ObjectKind.Table, id, null);
        var pair = new FkColumnPair { Parent = "customer_id", Referenced = "id" };
        store.ApplyRefresh("p", "main", true, [T(10, "orders"), T(20, "customers")], [],
            new MetadataBundle([], [], [], [new FkRow("fk_orders_customers", 10, 20, [pair])]), "1");
        Assert.Single(store.ForeignKeysOf("p", "main", [10]));
        // DROP TABLE customers: SQLite keeps the key in orders' DDL, and ids come from names, so a customers created again
        // has the same id and the key resolves again — orders is never read again to bring it back. Round-3 review
        // 2026-11-26: deleting it here lost it for good. (PostgreSQL's DROP … CASCADE changes orders, which is read again.)
        store.ApplyRefresh("p", "main", false, [], [20], new MetadataBundle([], [], [], []), "1");
        Assert.Single(store.ForeignKeysOf("p", "main", [10]));
        store.ApplyRefresh("p", "main", false, [T(20, "customers")], [], new MetadataBundle([], [], [], []), "1");
        Assert.Equal(20, store.ForeignKeysOf("p", "main", [10]).Single().Referenced);
    }

    [Fact]
    public void Meta_requests_without_params_are_invalid_not_internal_errors()
    {
        var store = new MetadataStore(LocalStore.InMemory());
        var profiles = new ProfileStore(LocalStore.InMemory());
        var sessions = new SessionManager(profiles, new ProviderRegistry([new FakeDbProvider()]), new CapturingNotifier(), Log.Null);
        var metadata = new MetadataService(sessions, store, new SchemaCache(store), new CapturingNotifier(), Log.Null);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => metadata.ForeignKeys(null!)).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => metadata.Objects(null!)).Code);
    }
}
