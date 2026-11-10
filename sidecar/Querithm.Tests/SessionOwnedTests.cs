using System.Collections.Concurrent;
using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Tests.Fakes;
using Querithm.Tests.Security;

namespace Querithm.Tests;

/// <summary>
/// Phase D0d: session-owned connections (one per tab, kept until it closes) and session-scoped metadata — on a fake
/// provider whose catalog behaves like a file database: committed objects every connection sees, a transaction's
/// uncommitted DDL only its connection sees, temporary tables per connection, and in-memory databases per connection.
/// </summary>
public class SessionOwnedTests
{
    /// <summary>The fake engine's catalog, driven by the statements the fake connections run.</summary>
    sealed class Catalog : IMetadataProvider
    {
        long _next = 1;
        public readonly ConcurrentDictionary<string, long> Committed = new();
        public readonly ConcurrentDictionary<FakeDbConnection, ConcurrentDictionary<string, long>> Pending = new();
        public readonly ConcurrentDictionary<FakeDbConnection, ConcurrentDictionary<string, long>> Temp = new();
        /// <summary>In-memory databases: every connection is its own database.</summary>
        public bool PerConnection { get; set; }
        public readonly ConcurrentDictionary<FakeDbConnection, ConcurrentDictionary<string, long>> Own = new();
        public int Reads;
        public TaskCompletionSource? ReadGate;
        /// <summary>Catalog reads in progress (waiting at <see cref="ReadGate"/> included).</summary>
        public int Reading;

        public Task<FakeResult> Handle(FakeDbCommand cmd, CancellationToken ct)
        {
            var text = cmd.CommandText.Trim();
            var c = cmd.Fake;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (text.StartsWith("CREATE TABLE #", StringComparison.Ordinal)) Temp.GetOrAdd(c, _ => new())[words[2].TrimStart('#')] = Interlocked.Increment(ref _next) + 1_000_000;
            else if (text.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
            {
                var id = Interlocked.Increment(ref _next);
                if (PerConnection) Own.GetOrAdd(c, _ => new())[words[2]] = id;
                else if (c.TxDepth > 0) Pending.GetOrAdd(c, _ => new())[words[2]] = id;
                else Committed[words[2]] = id;
            }
            else if (text.StartsWith("COMMIT", StringComparison.Ordinal) && Pending.TryRemove(c, out var p))
                foreach (var (k, v) in p) Committed[k] = v;
            else if (text.Contains("ROLLBACK", StringComparison.Ordinal)) Pending.TryRemove(c, out _);
            else if (text == "BREAK") { c.Break(); throw new InvalidOperationException("connection reset"); }
            return Task.FromResult(FakeResult.None);
        }

        public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct)
        {
            var c = (FakeDbConnection)connection;
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT catalog"; // a real command, so overlapping use of the connection is caught
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Interlocked.Increment(ref Reading);
            try { if (ReadGate is { } g) await g.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref Reading); }
            Interlocked.Increment(ref Reads);
            var list = new List<ObjectStamp>();
            IEnumerable<KeyValuePair<string, long>> visible = PerConnection
                ? Own.GetValueOrDefault(c) ?? new()
                : Committed.Concat(Pending.GetValueOrDefault(c) ?? new());
            foreach (var (name, id) in visible) list.Add(new ObjectStamp(id, "main", name, ObjectKind.Table, 1, null));
            foreach (var (name, id) in Temp.GetValueOrDefault(c) ?? new()) list.Add(new ObjectStamp(id, "temp", name, ObjectKind.Table, 1, null, SessionLocal: true));
            return list;
        }

        public Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) =>
            Task.FromResult(new MetadataBundle(objectIds.Select(id => new ColumnRow(id, 1, "id", "int", null, null, null, false, false, false, null)).ToList(), [], [], []));
        public Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public string ToEditableScript(string definition, Capabilities caps) => definition;
        public Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
    }

    sealed class Rig : IAsyncDisposable
    {
        public TestHostWithFake Host { get; } = new();
        public Catalog Catalog { get; } = new();
        public InvariantSink Invariants { get; } = FakeDbInvariants.Capture();
        public FakeDbProvider Provider => Host.Provider;

        public Rig(bool memory)
        {
            Provider.Lifetime = ConnectionLifetime.SessionOwned;
            Provider.MetadataImpl = Catalog;
            Provider.Handler = Catalog.Handle;
            Catalog.PerConnection = memory;
        }

        public Profile Profile(bool memory) => Host.Services.Profiles.Save(memory
            ? new Profile { Id = "", Name = "mem", Engine = Engine.Mssql, Auth = AuthKind.None, Location = LocationKind.Memory, DatabaseDefault = "main" }
            : new Profile { Id = "", Name = "file", Engine = Engine.Mssql, Auth = AuthKind.Windows, Host = "h", DatabaseDefault = "main" });

        public async Task<string> OpenAsync(Profile p) => (await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Database = "main" })).SessionId;

        public async Task<QueryDoneEvent> RunAsync(string sid, string sql)
        {
            var r = await Host.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql });
            return await Host.Notifier.WaitDone(r.ExecutionId!);
        }

        /// <summary>The refreshes asked for so far have finished (scoped job keys included).</summary>
        public async Task SettleAsync()
        {
            for (var i = 0; i < 200; i++)
            {
                await Task.Delay(10);
                var refreshing = Host.Notifier.All.Select(x => x.Payload).OfType<MetaUpdatedEvent>().ToList();
                if (refreshing.Count > 0 && refreshing[^1].Status != SnapshotStatus.Refreshing && i > 5) return;
            }
        }

        public SchemaSnapshot Snapshot(Profile p, string? sid) => Host.Services.Metadata.Snapshot(new SnapshotParams { ProfileId = p.Id, Database = "main", SessionId = sid });
        public string[] Names(Profile p, string? sid) => Snapshot(p, sid).Objects.Select(o => o.Name).Order().ToArray();

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Assert.Empty(Invariants.Violations);
        }
    }

    // An upper-bound wait: scaled for slow runners (TestTime) and measured on the clock, not counted in 10 ms steps (each
    // Task.Delay(10) sleeps longer than 10 ms). macOS Intel CI missed the unscaled 5 s twice.
    static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var limit = TestTime.Of(TimeSpan.FromMilliseconds(ms));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > limit) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    // ---------------------------------------------------------------------------------------------- ownership

    [Fact]
    public async Task Each_session_opens_one_connection_lazily_and_keeps_it_across_runs_and_transactions()
    {
        await using var rig = new Rig(memory: true);
        var p = rig.Profile(memory: true);
        var a = await rig.OpenAsync(p);
        var b = await rig.OpenAsync(p);
        Assert.Empty(rig.Provider.Opened); // §6 rule 1: nothing dialed at open
        await rig.RunAsync(a, "SELECT 1");
        await rig.RunAsync(a, "SELECT 2");
        await rig.Host.Rpc.TxBegin(new SessionRef { SessionId = a });
        await rig.Host.Rpc.TxCommit(new SessionRef { SessionId = a });
        await rig.Host.Rpc.TxBegin(new SessionRef { SessionId = a });
        await rig.Host.Rpc.TxRollback(new SessionRef { SessionId = a });
        await rig.RunAsync(a, "SELECT 3");
        Assert.Single(rig.Provider.Opened);
        var state = await rig.Host.Rpc.SessionStateGet(new SessionRef { SessionId = a });
        Assert.True(state.Pinned); // owned for the session's life, transaction or not
        Assert.Equal(TxState.None, state.TxState);
        await rig.RunAsync(b, "SELECT 1");
        Assert.Equal(2, rig.Provider.Opened.Count);
        Assert.NotSame(rig.Host.Services.Sessions.Get(a).Pinned, rig.Host.Services.Sessions.Get(b).Pinned);
    }

    [Fact]
    public async Task Closing_the_tab_closes_its_connection_and_drops_its_view()
    {
        await using var rig = new Rig(memory: true);
        var p = rig.Profile(memory: true);
        var a = await rig.OpenAsync(p);
        await rig.RunAsync(a, "CREATE TABLE t (id int)");
        await Until(() => rig.Names(p, a).SequenceEqual(["t"]));
        var conn = rig.Provider.Opened.Single();
        await rig.Host.Rpc.SessionClose(new SessionCloseParams { SessionId = a });
        Assert.Equal(1, conn.DisposeCount);
        Assert.Empty(rig.Host.Services.Metadata.Cache.Ephemeral.KnownDatabases(p.Id));
        Assert.Throws<QuerithmException>(() => rig.Snapshot(p, a)); // the session is gone
    }

    [Fact]
    public async Task A_refresh_still_running_when_the_tab_closes_writes_and_reports_nothing()
    {
        await using var rig = new Rig(memory: true);
        var p = rig.Profile(memory: true);
        var a = await rig.OpenAsync(p);
        await rig.RunAsync(a, "SELECT 1");
        rig.Catalog.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = "main", SessionId = a });
        await Until(() => rig.Provider.Opened.Single().Log.Contains("SELECT catalog"));
        var close = rig.Host.Rpc.SessionClose(new SessionCloseParams { SessionId = a, Force = true });
        rig.Catalog.ReadGate.SetResult();
        await close;
        await Task.Delay(100);
        Assert.Empty(rig.Host.Services.Metadata.Cache.Ephemeral.KnownDatabases(p.Id));
        Assert.DoesNotContain(rig.Host.Notifier.Of<MetaUpdatedEvent>(), e => e.SessionId == a && e.Status == SnapshotStatus.Fresh);
    }

    [Fact]
    public async Task Helpers_wait_for_the_execution_instead_of_sharing_the_connection()
    {
        await using var rig = new Rig(memory: true);
        var p = rig.Profile(memory: true);
        var a = await rig.OpenAsync(p);
        await rig.RunAsync(a, "SELECT 1");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.StartsWith("WAITFOR", StringComparison.Ordinal)) await gate.Task.WaitAsync(ct);
            return await rig.Catalog.Handle(cmd, ct);
        };
        var run = await rig.Host.Rpc.QueryExecute(new QueryExecuteParams { SessionId = a, Sql = "WAITFOR DELAY '00:01'" });
        await Until(() => rig.Host.Services.Sessions.Get(a).ActiveExecutionId != null);
        var reads = rig.Catalog.Reads;
        rig.Host.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = "main", SessionId = a, Full = true });
        await Task.Delay(150);
        Assert.Equal(reads, rig.Catalog.Reads); // waiting for the execution's lock
        gate.SetResult();
        await rig.Host.Notifier.WaitDone(run.ExecutionId!);
        await Until(() => rig.Catalog.Reads > reads);
        Assert.Single(rig.Provider.Opened); // the refresh used the tab's connection, never a new (empty) database
    }

    // ---------------------------------------------------------------------------------------------- session-scoped metadata

    [Fact]
    public async Task Two_memory_tabs_with_the_same_object_names_stay_isolated()
    {
        await using var rig = new Rig(memory: true);
        var p = rig.Profile(memory: true);
        var a = await rig.OpenAsync(p);
        var b = await rig.OpenAsync(p);
        await rig.RunAsync(a, "CREATE TABLE t (id int)");
        await rig.RunAsync(a, "CREATE TABLE only_a (id int)");
        await rig.RunAsync(b, "CREATE TABLE t (id int)");
        await Until(() => rig.Names(p, a).SequenceEqual(["only_a", "t"]) && rig.Names(p, b).SequenceEqual(["t"]));
        Assert.NotEqual(rig.Snapshot(p, a).Objects.Single(o => o.Name == "t").Id, rig.Snapshot(p, b).Objects.Single(o => o.Name == "t").Id);
        Assert.Equal(a, rig.Snapshot(p, a).SessionId);
        // No shared snapshot exists for an in-memory database, and nothing reached the store on disk.
        Assert.Empty(rig.Snapshot(p, null).Objects);
        Assert.Empty(rig.Host.Services.Metadata.Store.KnownDatabases(p.Id));
        // Scoped reads follow the same views.
        Assert.Single(rig.Host.Services.Metadata.Search(new SearchParams { ProfileId = p.Id, Database = "main", Text = "only", SessionId = a }));
        Assert.Empty(rig.Host.Services.Metadata.Search(new SearchParams { ProfileId = p.Id, Database = "main", Text = "only", SessionId = b }));
    }

    [Fact]
    public async Task A_scoped_request_must_name_a_live_session_of_the_same_profile()
    {
        await using var rig = new Rig(memory: true);
        var p = rig.Profile(memory: true);
        var other = rig.Profile(memory: true);
        var a = await rig.OpenAsync(p);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Snapshot(other, a)).Code);
        Assert.Equal("notFound", Assert.Throws<QuerithmException>(() => rig.Snapshot(p, "01JUNKNOWNSESSION")).Code);
        Assert.Throws<QuerithmException>(() => rig.Host.Services.Metadata.Refresh(new RefreshParams { ProfileId = other.Id, Database = "main", SessionId = a }));
    }

    [Fact]
    public async Task No_helper_ever_opens_a_fresh_in_memory_database()
    {
        await using var rig = new Rig(memory: true);
        var p = rig.Profile(memory: true);
        _ = await rig.OpenAsync(p);
        Assert.Equal(SnapshotStatus.Empty, rig.Snapshot(p, null).Status);
        await Task.Delay(50);
        Assert.Empty(rig.Provider.Opened);
        Assert.False(rig.Host.Services.Sessions.CanOpen(p));
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => rig.Host.Services.Sessions.OpenDedicatedAsync(p, rig.Provider, "main", default));
        Assert.Equal("memoryDedicated", ex.Code);
    }

    [Fact]
    public async Task Temporary_objects_belong_to_their_session_only()
    {
        await using var rig = new Rig(memory: false);
        var p = rig.Profile(memory: false);
        var a = await rig.OpenAsync(p);
        var b = await rig.OpenAsync(p);
        await rig.RunAsync(a, "CREATE TABLE shared_t (id int)");
        await rig.RunAsync(a, "CREATE TABLE #scratch (id int)");
        await Until(() => rig.Names(p, a).SequenceEqual(["scratch", "shared_t"]));
        await rig.RunAsync(b, "SELECT 1");
        rig.Host.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = "main", SessionId = b });
        await Until(() => rig.Names(p, b).SequenceEqual(["shared_t"]));
        Assert.Equal(["shared_t"], rig.Names(p, null));
        Assert.DoesNotContain(rig.Host.Services.Metadata.Store.Load(p.Id, "main").Objects, o => o.Name == "scratch");
    }

    [Fact]
    public async Task Uncommitted_ddl_never_reaches_the_shared_snapshot_and_rollback_leaves_no_phantom()
    {
        await using var rig = new Rig(memory: false);
        var p = rig.Profile(memory: false);
        var a = await rig.OpenAsync(p);
        var b = await rig.OpenAsync(p);
        await rig.RunAsync(a, "CREATE TABLE base (id int)");
        await Until(() => rig.Names(p, null).SequenceEqual(["base"]));
        await rig.Host.Rpc.TxBegin(new SessionRef { SessionId = a });
        await rig.RunAsync(a, "CREATE TABLE phantom (id int)"); // DDL inside the transaction: its refresh is deferred
        await Task.Delay(150);
        Assert.True(rig.Host.Services.Sessions.Get(a).RefreshDeferred);
        Assert.Equal(["base"], rig.Names(p, null));
        Assert.Equal(["base"], rig.Names(p, b));
        Assert.Equal(SnapshotStatus.Stale, rig.Host.Services.Metadata.Store.GetSnapshotRow(p.Id, "main")!.Status);
        var reads = rig.Catalog.Reads;
        await rig.Host.Rpc.TxRollback(new SessionRef { SessionId = a });
        await Until(() => rig.Catalog.Reads > reads); // the deferred refresh runs once the transaction ended
        await Task.Delay(50);
        Assert.Equal(["base"], rig.Names(p, null));
        Assert.Equal(["base"], rig.Names(p, a));
        Assert.False(rig.Host.Services.Sessions.Get(a).RefreshDeferred);
    }

    [Fact]
    public async Task Committed_ddl_reaches_every_tab_after_the_commit()
    {
        await using var rig = new Rig(memory: false);
        var p = rig.Profile(memory: false);
        var a = await rig.OpenAsync(p);
        var b = await rig.OpenAsync(p);
        await rig.Host.Rpc.TxBegin(new SessionRef { SessionId = a });
        await rig.RunAsync(a, "CREATE TABLE kept (id int)");
        await Task.Delay(100);
        Assert.Empty(rig.Names(p, b));
        await rig.Host.Rpc.TxCommit(new SessionRef { SessionId = a });
        await Until(() => rig.Names(p, b).SequenceEqual(["kept"]));
        Assert.Equal(["kept"], rig.Names(p, null));
    }

    // ---------------------------------------------------------------------------------------------- loss and reconnect

    [Fact]
    public async Task A_lost_in_memory_connection_is_reported_and_never_replaced_by_an_empty_database()
    {
        await using var rig = new Rig(memory: true);
        var p = rig.Profile(memory: true);
        var a = await rig.OpenAsync(p);
        await rig.RunAsync(a, "CREATE TABLE t (id int)");
        var lost = await rig.RunAsync(a, "BREAK");
        Assert.Contains(rig.Host.Notifier.Of<ServerMessage>(), m => m.ExecutionId == lost.ExecutionId && m.Message == Core.Sessions.SessionManager.MemoryLostMessage);
        Assert.True(rig.Host.Services.Sessions.Get(a).MemoryLost);
        var next = await rig.RunAsync(a, "SELECT 1");
        Assert.Contains("in-memory database is gone", next.Error?.Message ?? string.Join(" ", rig.Host.Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == next.ExecutionId).Select(m => m.Message)));
        Assert.Single(rig.Provider.Opened); // no second, empty database
    }

    [Fact]
    public async Task A_lost_file_connection_is_reopened_on_next_use()
    {
        await using var rig = new Rig(memory: false);
        var p = rig.Profile(memory: false);
        var a = await rig.OpenAsync(p);
        await rig.RunAsync(a, "BREAK");
        Assert.False(rig.Host.Services.Sessions.Get(a).MemoryLost);
        var ok = await rig.RunAsync(a, "SELECT 1");
        Assert.Null(ok.Error);
        Assert.Equal(2, rig.Provider.Opened.Count);
    }

    // ---------------------------------------------------------------------------------------------- closing the tab

    static readonly TimeSpan Wait = TestTime.Of(TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_the_tab_ends_the_refresh_reading_on_its_connection_before_the_connection_closes(bool memory)
    {
        // Regression: the refresh after DDL leased the tab's connection and session.close closed it under the read. DuckDB
        // kept the database (and on Windows the file lock) until that reader went, so the file could not be opened again.
        await using var rig = new Rig(memory);
        var p = rig.Profile(memory);
        var sid = await rig.OpenAsync(p);
        rig.Catalog.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // only the close ends the read
        Assert.Equal(ExecState.Done, (await rig.RunAsync(sid, "CREATE TABLE t (id int)")).State);
        await Until(() => Volatile.Read(ref rig.Catalog.Reading) == 1);
        var conn = Assert.Single(rig.Provider.Opened);

        await rig.Host.Rpc.SessionClose(new SessionCloseParams { SessionId = sid }).WaitAsync(Wait);

        Assert.Equal(0, Volatile.Read(ref rig.Catalog.Reading));
        Assert.Equal(1, conn.DisposeCount);
    }

    [Fact]
    public async Task Closing_the_tab_cancels_its_running_query_before_the_connection_closes()
    {
        // The close used to close the connection first; the run was cancelled only afterwards, on a closed connection.
        await using var rig = new Rig(memory: false);
        var sid = await rig.OpenAsync(rig.Profile(false));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedWhileRunning = -1;
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.Contains("WAITFOR", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { disposedWhileRunning = cmd.Fake.DisposeCount; }
            }
            return FakeResult.None;
        };
        var r = await rig.Host.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "WAITFOR DELAY '00:10:00'" });
        await entered.Task.WaitAsync(Wait);

        await rig.Host.Rpc.SessionClose(new SessionCloseParams { SessionId = sid }).WaitAsync(Wait);

        Assert.Equal(ExecState.Cancelled, (await rig.Host.Notifier.WaitDone(r.ExecutionId!, (int)Wait.TotalMilliseconds)).State);
        Assert.Equal(0, disposedWhileRunning);
        Assert.Equal(1, Assert.Single(rig.Provider.Opened).DisposeCount);
    }

    [Fact]
    public async Task The_close_waits_for_a_lease_on_the_connection_cancels_it_and_refuses_new_ones()
    {
        await using var rig = new Rig(memory: false);
        var sid = await rig.OpenAsync(rig.Profile(false));
        var sessions = rig.Host.Services.Sessions;
        var s = sessions.Get(sid);
        var lease = await sessions.LeaseAsync(s, CancellationToken.None);
        var conn = Assert.Single(rig.Provider.Opened);

        var close = rig.Host.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });

        Assert.True(lease.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sessions.LeaseAsync(s, CancellationToken.None));
        Assert.False(close.IsCompleted);
        Assert.Equal(0, conn.DisposeCount);
        await lease.DisposeAsync();
        await close.WaitAsync(Wait);
        Assert.Equal(1, conn.DisposeCount);
    }

    [Fact]
    public async Task A_helper_that_never_lets_go_holds_up_the_close_only_until_its_limit()
    {
        await using var rig = new Rig(memory: false);
        rig.Host.Services.Sessions.CloseWaitLimit = TimeSpan.FromMilliseconds(100);
        var sid = await rig.OpenAsync(rig.Profile(false));
        var lease = await rig.Host.Services.Sessions.LeaseAsync(rig.Host.Services.Sessions.Get(sid), CancellationToken.None); // ignores its token

        await rig.Host.Rpc.SessionClose(new SessionCloseParams { SessionId = sid }).WaitAsync(Wait);

        Assert.Equal(1, Assert.Single(rig.Provider.Opened).DisposeCount); // closed anyway, as before the wait existed
        await lease.DisposeAsync(); // letting go late is harmless
    }

    [Fact]
    public async Task A_failure_while_stopping_the_tabs_work_never_leaves_its_connection_open()
    {
        await using var rig = new Rig(memory: false);
        var sid = await rig.OpenAsync(rig.Profile(false));
        Assert.Null((await rig.RunAsync(sid, "SELECT 1")).Error);
        rig.Host.Services.Sessions.SessionClosing += _ => throw new InvalidOperationException("a broken listener");

        await rig.Host.Rpc.SessionClose(new SessionCloseParams { SessionId = sid }).WaitAsync(Wait);

        Assert.Equal(1, Assert.Single(rig.Provider.Opened).DisposeCount);
        Assert.Throws<QuerithmException>(() => rig.Host.Services.Sessions.Get(sid));
    }
}
