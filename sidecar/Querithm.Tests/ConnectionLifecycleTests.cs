using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Pooled-connection lifecycle (ARCHITECTURE §6 "Physical connection lifecycle"): ownership, hold (pin), release,
/// probe, discard and shutdown transitions, with gates instead of sleeps. Every bug found while making the lifecycle
/// explicit has a regression test here that failed before its fix.
/// </summary>
public class ConnectionLifecycleTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public Profile Profile { get; }
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public ExecutionService Executions { get; }
        public InvariantSink Invariants { get; } = FakeDbInvariants.Capture();

        public Rig(int max = 8)
        {
            var store = LocalStore.InMemory();
            var profiles = new ProfileStore(store);
            Profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Log.Null) { MaxPerProfile = max };
            Sessions.RememberSecret(Profile.Id, "x");
            Executions = new ExecutionService(Sessions, new CapturingFrames(), Notifier, new HistoryStore(store), Log.Null);
            Invariants.LockHeld = () => Pool.LockHeldByCurrentThread;
        }

        public Pool Pool => Sessions.PoolFor(Profile, Provider, null);
        public Session Open() => Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = Profile.Id, Secret = "x" }).SessionId);

        public async ValueTask DisposeAsync()
        {
            await Executions.DisposeAsync();
            await Sessions.DisposeAsync();
            Assert.Empty(Invariants.Violations);
        }
    }

    static Pool NewPool(FakeDbProvider provider, int max)
    {
        var profile = new Profile { Id = "p", Name = "p", Engine = Engine.Mssql, Host = "h" };
        return new Pool("k", profile, provider, null, (db, ct) => provider.OpenAsync(profile, null, db, null, ct), max, NullLogger.Instance);
    }

    // ------------------------------------------------------------------ regressions

    [Fact]
    public async Task A_borrow_cancelled_during_the_idle_validation_probe_does_not_leak_the_connection()
    {
        // Regression: the probe's catch skipped OperationCanceledException, so the connection was neither returned nor
        // discarded and its slot stayed taken forever (with max 1, every later borrower queued for good).
        var inv = FakeDbInvariants.Capture();
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 1);
        inv.LockHeld = () => pool.LockHeldByCurrentThread;
        var c = await pool.BorrowAsync(null, null, CancellationToken.None);
        pool.Return(c);
        c.LastUsedMs -= 31_000; // idle > 30 s: the next borrow validates first (§6 rule 6)
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnValidate = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var cts = new CancellationTokenSource();
        var borrow = pool.BorrowAsync(null, null, cts.Token);
        await entered.Task.WaitAsync(Wait);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow);

        provider.OnValidate = null;
        var again = await pool.BorrowAsync(null, null, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(1, pool.Total);
        pool.Return(again);
        Assert.Empty(inv.Violations);
    }

    [Fact]
    public async Task A_queued_borrower_cancelled_after_it_was_granted_a_freed_slot_passes_the_slot_on()
    {
        // Regression: Release() handed the freed slot to the first waiter; that waiter, cancelled before it resumed,
        // threw at the top of its loop and the second waiter stayed queued although a slot was free (lost wakeup).
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 1);
        var a = await pool.BorrowAsync(null, null, CancellationToken.None);
        using var cts1 = new CancellationTokenSource();
        var w1 = pool.BorrowAsync(null, null, cts1.Token);
        var w2 = pool.BorrowAsync(null, null, CancellationToken.None);
        var woken = 0;
        pool.AfterWaiterWoken = () => { if (Interlocked.Increment(ref woken) == 1) cts1.Cancel(); };

        await pool.DiscardAsync(a);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w1.WaitAsync(Wait));
        var b = await w2.WaitAsync(Wait);
        Assert.Equal(1, pool.Total);
        pool.Return(b);
    }

    [Fact]
    public async Task A_queued_borrower_cancelled_after_it_was_handed_a_connection_passes_the_connection_on()
    {
        // The handed connection used to be returned to the cancelled borrower as if it had not been cancelled.
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 1);
        var a = await pool.BorrowAsync(null, null, CancellationToken.None);
        using var cts1 = new CancellationTokenSource();
        var w1 = pool.BorrowAsync(null, null, cts1.Token);
        var w2 = pool.BorrowAsync(null, null, CancellationToken.None);
        var woken = 0;
        pool.AfterWaiterWoken = () => { if (Interlocked.Increment(ref woken) == 1) cts1.Cancel(); };

        pool.Return(a);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w1.WaitAsync(Wait));
        Assert.Same(a, await w2.WaitAsync(Wait));
        pool.Return(a);
        Assert.Equal(1, pool.Total);
    }

    [Fact]
    public async Task A_queue_wait_limit_bounds_only_the_wait_for_a_full_pool_never_a_slow_open()
    {
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 1);
        provider.OnOpen = ct => Task.Delay(300, ct);
        var a = await pool.BorrowAsync(null, null, CancellationToken.None, maxQueueWait: TimeSpan.FromMilliseconds(50)); // open 300 ms > limit 50 ms
        provider.OnOpen = null;
        var busy = await Assert.ThrowsAsync<QuerithmException>(() => pool.BorrowAsync(null, null, CancellationToken.None, maxQueueWait: TimeSpan.FromMilliseconds(100)).WaitAsync(Wait));
        Assert.Equal("poolBusy", busy.Code);
        Assert.Equal(0, pool.WaiterCount);
        pool.Return(a);
        Assert.Same(a, await pool.BorrowAsync(null, null, CancellationToken.None, maxQueueWait: TimeSpan.FromMilliseconds(100)).WaitAsync(Wait));
        Assert.Equal(1, pool.Total);
        pool.Return(a);
    }

    [Fact]
    public async Task A_queued_borrower_whose_queue_limit_fires_after_it_was_handed_a_connection_passes_it_on()
    {
        // Flaky on CI until the limit was a gate: a real 200 ms timer raced a 400 ms sleep in the wake hook, and on a busy
        // Linux runner the timer callback (it needs a free thread-pool worker) could come after the sleep ended.
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 1);
        using var limit = new CancellationTokenSource();
        pool.QueueLimit = _ => limit;
        var a = await pool.BorrowAsync(null, null, CancellationToken.None);
        var w1 = pool.BorrowAsync(null, null, CancellationToken.None, maxQueueWait: TimeSpan.FromMilliseconds(200));
        var w2 = pool.BorrowAsync(null, null, CancellationToken.None);
        var woken = 0;
        pool.AfterWaiterWoken = () => { if (Interlocked.Increment(ref woken) == 1) limit.Cancel(); }; // limit fires before w1 resumes

        pool.Return(a);

        var busy = await Assert.ThrowsAsync<QuerithmException>(() => w1.WaitAsync(Wait));
        Assert.Equal("poolBusy", busy.Code);
        Assert.Same(a, await w2.WaitAsync(Wait));
        pool.Return(a);
        Assert.Equal(1, pool.Total);
    }

    [Fact]
    public async Task Closing_a_session_while_its_batch_opens_a_transaction_does_not_pin_the_connection_to_the_closed_session()
    {
        // Regression: the UI closes a busy tab with query.close + session.close (force: false). The session's tx state was
        // still none, so the close went through; the cancelled batch had already begun a transaction, and the execution's
        // cleanup then pinned the connection to the closed session: connection, slot and server transaction (with its
        // locks) leaked until the app exited.
        await using var rig = new Rig();
        var s = rig.Open();
        var inWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.Contains("WAITFOR"))
            {
                cmd.Fake.TxDepth = 1; // BEGIN TRAN ran before the WAITFOR
                inWait.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return FakeResult.None;
        };
        var started = rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "BEGIN TRAN; UPDATE t SET v = 1; WAITFOR DELAY '00:10:00'" });
        await inWait.Task.WaitAsync(Wait);

        await rig.Sessions.CloseAsync(new SessionCloseParams { SessionId = s.Id, Force = false });
        rig.Executions.CloseForSession(s.Id);
        await rig.Notifier.WaitDone(started.ExecutionId!);

        Assert.Equal(0, rig.Pool.Total);
        Assert.Equal(1, Assert.Single(rig.Provider.Opened).DisposeCount);
        Assert.Null(s.Pinned);
    }

    [Fact]
    public async Task Force_closing_a_session_inside_a_transaction_cancels_its_query_before_the_pinned_connection_closes()
    {
        // The close used to dispose the pinned connection under the running command and cancel the run only afterwards.
        await using var rig = new Rig();
        var s = rig.Open();
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
        await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None);
        var started = rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "WAITFOR DELAY '00:10:00'" });
        await entered.Task.WaitAsync(Wait);

        await rig.Sessions.CloseAsync(new SessionCloseParams { SessionId = s.Id, Force = true }).WaitAsync(Wait);

        Assert.Equal(ExecState.Cancelled, (await rig.Notifier.WaitDone(started.ExecutionId!, (int)Wait.TotalMilliseconds)).State);
        Assert.Equal(0, disposedWhileRunning);
        Assert.Equal(1, Assert.Single(rig.Provider.Opened).DisposeCount);
        Assert.Equal(0, rig.Pool.Total);
    }

    [Fact]
    public async Task A_failed_BEGIN_TRANSACTION_returns_the_borrowed_connection()
    {
        // Regression: tx.begin borrowed a connection and only pinned it after BEGIN succeeded; a failing BEGIN leaked it.
        await using var rig = new Rig(max: 1);
        var s = rig.Open();
        rig.Provider.Handler = (cmd, _) => cmd.CommandText.StartsWith("BEGIN", StringComparison.Ordinal)
            ? throw new InvalidOperationException("the server refused BEGIN")
            : Task.FromResult(FakeResult.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None));
        Assert.Null(s.Pinned);
        Assert.Equal(TxState.None, s.TxState);
        Assert.Equal(1, rig.Pool.IdleCount);
    }

    // ------------------------------------------------------------------ invariants

    [Fact]
    public async Task One_command_at_a_time_per_connection_and_no_io_under_the_pool_lock_with_many_concurrent_sessions()
    {
        await using var rig = new Rig(max: 3);
        rig.Provider.Handler = async (_, ct) => { await Task.Yield(); return FakeResult.None; };
        var sessions = Enumerable.Range(0, 12).Select(_ => rig.Open()).ToList();
        var done = new List<Task<QueryDoneEvent>>();
        for (var round = 0; round < 5; round++)
            foreach (var s in sessions)
            {
                var r = rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "SELECT 1", Options = new ExecuteOptions { NoHistory = true } });
                done.Add(rig.Notifier.WaitDone(r.ExecutionId!));
            }
        // Background borrowers (metadata, compare) share the same pool.
        var background = Enumerable.Range(0, 20).Select(async _ =>
        {
            var c = await rig.Pool.BorrowAsync(null, null, CancellationToken.None);
            await using (var cmd = c.Db.CreateCommand()) { cmd.CommandText = "SELECT 2"; await cmd.ExecuteScalarAsync(); }
            rig.Pool.Return(c);
        });
        await Task.WhenAll(done).WaitAsync(TimeSpan.FromSeconds(30));
        await Task.WhenAll(background).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(done, t => Assert.Equal(ExecState.Done, t.Result.State));
        Assert.True(rig.Pool.Total <= 3);
        Assert.Equal(rig.Pool.Total, rig.Pool.IdleCount);
        Assert.Empty(rig.Invariants.Violations);
    }

    [Fact]
    public async Task A_connection_being_validated_is_never_handed_to_another_borrower()
    {
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 2);
        var c = await pool.BorrowAsync(null, null, CancellationToken.None);
        pool.Return(c);
        c.LastUsedMs -= 31_000;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnValidate = async (_, ct) => { entered.TrySetResult(); await gate.Task.WaitAsync(ct); };
        var first = pool.BorrowAsync(null, null, CancellationToken.None);
        await entered.Task.WaitAsync(Wait);
        provider.OnValidate = null;

        var second = await pool.BorrowAsync(null, null, CancellationToken.None).WaitAsync(Wait);
        Assert.NotSame(c, second);
        gate.SetResult();
        Assert.Same(c, await first.WaitAsync(Wait));
        pool.Return(second);
        pool.Return(c);
        Assert.Equal(2, pool.IdleCount);
    }

    [Fact]
    public async Task Returning_a_connection_twice_never_lets_two_borrowers_share_it()
    {
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 2);
        var c = await pool.BorrowAsync(null, null, CancellationToken.None);
        pool.Return(c);
        pool.Return(c); // a second return of the same lease is ignored
        Assert.Equal(1, pool.IdleCount);
        var a = await pool.BorrowAsync(null, null, CancellationToken.None);
        var b = await pool.BorrowAsync(null, null, CancellationToken.None);
        Assert.NotSame(a, b);
        Assert.Equal(2, pool.Total);
    }

    [Fact]
    public async Task Discarding_twice_disposes_once_and_frees_the_slot_once()
    {
        var inv = FakeDbInvariants.Capture();
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 2);
        var a = await pool.BorrowAsync(null, null, CancellationToken.None);
        var b = await pool.BorrowAsync(null, null, CancellationToken.None);
        await Task.WhenAll(pool.DiscardAsync(a), pool.DiscardAsync(a));
        Assert.Equal(1, ((FakeDbConnection)a.Db).DisposeCount);
        Assert.Equal(1, pool.Total);
        pool.Return(a); // returning a discarded connection does nothing
        Assert.Equal(0, pool.IdleCount);
        pool.Return(b);
        Assert.Equal(1, pool.Total);
        Assert.Empty(inv.Violations);
    }

    [Fact]
    public async Task Closing_a_pool_fails_its_queued_borrowers_and_discards_leased_connections_on_return()
    {
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 1);
        var a = await pool.BorrowAsync(null, null, CancellationToken.None);
        var queued = pool.BorrowAsync(null, null, CancellationToken.None);
        await pool.CloseAllAsync();
        var ex = await Assert.ThrowsAsync<Querithm.Core.QuerithmException>(() => queued.WaitAsync(Wait));
        Assert.Equal("poolClosed", ex.Code);
        pool.Return(a);
        Assert.Equal(0, pool.Total);
        Assert.Equal(1, ((FakeDbConnection)a.Db).DisposeCount);
    }

    [Fact]
    public async Task A_broken_connection_is_discarded_on_return_and_its_slot_reaches_a_queued_borrower()
    {
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 1);
        var a = await pool.BorrowAsync(null, null, CancellationToken.None);
        var queued = pool.BorrowAsync(null, null, CancellationToken.None);
        ((FakeDbConnection)a.Db).Break();
        pool.Return(a);
        var b = await queued.WaitAsync(Wait);
        Assert.NotSame(a, b);
        Assert.Equal(1, pool.Total);
        pool.Return(b);
    }

    [Fact]
    public async Task A_failed_open_frees_its_slot_for_the_next_borrower()
    {
        var provider = new FakeDbProvider();
        var pool = NewPool(provider, max: 1);
        provider.OnOpen = _ => throw new InvalidOperationException("login failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.BorrowAsync(null, null, CancellationToken.None));
        Assert.Equal(0, pool.Total);
        provider.OnOpen = null;
        var c = await pool.BorrowAsync(null, null, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(1, pool.Total);
        pool.Return(c);
    }

    [Fact]
    public async Task A_driver_reconnect_during_a_run_is_identified_again_right_after_the_run()
    {
        // Regression (O2): the identity was read again only when the connection was next borrowed, so until then the
        // tab's new server session looked foreign to the monitor, and the kill guard would have let it be killed.
        await using var rig = new Rig();
        rig.Provider.Activity = new FakeActivity();
        var s = rig.Open();
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "SELECT 1" }).ExecutionId!)).State);
        var conn = rig.Provider.Opened.Single();
        var oldKey = rig.Sessions.Registry.KeyOf(conn)!;
        var sawReconnect = false;
        rig.Provider.Handler = (cmd, _) =>
        {
            if (cmd.CommandText == "SELECT 2") { conn.Reconnect(); sawReconnect = rig.Sessions.Registry.HasReconnected(); }
            return Task.FromResult(FakeResult.None);
        };
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "SELECT 2" }).ExecutionId!)).State);
        Assert.True(sawReconnect); // during the run the registry knows its record is stale
        var newKey = rig.Sessions.Registry.KeyOf(conn)!;
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(conn.Spid, newKey.Id);
        Assert.Null(rig.Sessions.Registry.Find(oldKey));
        Assert.Equal(Querithm.Core.Monitoring.ConnectionRegistry.Kinds.Pool, rig.Sessions.Registry.Find(newKey)!.Kind);
        Assert.False(rig.Sessions.Registry.HasReconnected());
        // The tab's reported SPID is the new session too: it was read before the command ran, i.e. before the reconnect
        // (the O2 integration test then looked for the dead session and timed out).
        Assert.Equal(conn.Spid, rig.Sessions.Get(s.Id).Spid);
    }

    [Fact]
    public async Task A_failed_identity_read_after_a_run_still_pins_the_transaction_the_run_opened()
    {
        // Regression (audit): the identity read after a run shared one try with the transaction-state refresh. When the
        // driver had reconnected and that read then failed, the refresh never ran: the batch's BEGIN TRAN was not
        // pinned, and Release returned a connection holding an open transaction to the pool for the next borrower.
        await using var rig = new Rig();
        var activity = new FakeActivity();
        rig.Provider.Activity = activity;
        var s = rig.Open();
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "SELECT 1" }).ExecutionId!)).State);
        var conn = rig.Provider.Opened.Single();
        rig.Provider.Handler = (cmd, _) =>
        {
            if (cmd.CommandText == "BEGIN TRAN; UPDATE t SET v = 1")
            {
                conn.Reconnect(); // connection resiliency: a new server session under the same connection object
                activity.OnIdentity = (_, _) => throw new InvalidOperationException("identity read failed");
            }
            return Task.FromResult(FakeResult.None);
        };
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "BEGIN TRAN; UPDATE t SET v = 1" }).ExecutionId!)).State);
        var session = rig.Sessions.Get(s.Id);
        Assert.Equal(1, conn.TxDepth);
        Assert.Equal(TxState.Open, session.TxState);
        Assert.Same(conn, session.Pinned?.Db);
        Assert.True(rig.Sessions.Registry.HasReconnected()); // unidentified: kills stay refused until it is read again
    }

    [Fact]
    public async Task The_identity_read_after_a_run_never_overlaps_the_next_borrower_of_the_connection_the_run_released()
    {
        // Review finding on the fix above: refreshing the transaction state first can unpin the connection (COMMIT) and hand
        // it to a queued tab at once; the identity read that followed then ran a command on a connection another borrower
        // owned (one command at a time per connection, ARCHITECTURE §6). The identity read comes first.
        await using var rig = new Rig(max: 1);
        var activity = new FakeActivity();
        rig.Provider.Activity = activity;
        var a = rig.Open();
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(rig.Executions.Execute(new QueryExecuteParams { SessionId = a.Id, Sql = "BEGIN TRAN" }).ExecutionId!)).State);
        var conn = rig.Provider.Opened.Single();
        Assert.Same(conn, rig.Sessions.Get(a.Id).Pinned?.Db);
        var reconnected = false;
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText == "COMMIT") { conn.Reconnect(); reconnected = true; }
            if (reconnected && cmd.CommandText == "FAKE identity") await Task.Delay(300, ct); // a slow identity read
            if (cmd.CommandText == "SELECT 2") await Task.Delay(300, ct);
            return FakeResult.None;
        };
        var b = rig.Open();
        var queued = rig.Executions.Execute(new QueryExecuteParams { SessionId = b.Id, Sql = "SELECT 2" }).ExecutionId!; // waits: pool of one, pinned by a
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(rig.Executions.Execute(new QueryExecuteParams { SessionId = a.Id, Sql = "COMMIT" }).ExecutionId!)).State);
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(queued)).State);
        Assert.Empty(rig.Invariants.Violations);
        Assert.Equal(TxState.None, rig.Sessions.Get(a.Id).TxState);
        Assert.False(rig.Sessions.Registry.HasReconnected()); // identified again before the connection moved on
    }

    [Fact]
    public async Task A_driver_reconnect_during_a_tool_run_is_identified_again_right_after_it()
    {
        // The same window as a query run, for grid edits / imports / syncs (RunToolAsync).
        await using var rig = new Rig();
        rig.Provider.Activity = new FakeActivity();
        var s = rig.Open();
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "SELECT 1" }).ExecutionId!)).State);
        var conn = rig.Provider.Opened.Single();
        var oldKey = rig.Sessions.Registry.KeyOf(conn)!;
        await rig.Executions.RunToolAsync(s, "-- tool", ctx => { ((FakeDbConnection)ctx.Connection.Db).Reconnect(); return Task.FromResult(0); }, _ => (null, null));
        Assert.NotEqual(oldKey, rig.Sessions.Registry.KeyOf(conn));
        Assert.Null(rig.Sessions.Registry.Find(oldKey));
        Assert.False(rig.Sessions.Registry.HasReconnected());
    }

    [Fact]
    public async Task A_cancelled_statement_inside_a_transaction_keeps_the_session_pinned_and_usable()
    {
        // "Cancellation preserves a usable session unless the connection actually fails": the tab keeps its open
        // transaction and its connection; Querithm sends no ROLLBACK of its own.
        await using var rig = new Rig();
        var s = rig.Open();
        await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None);
        var pinned = s.Pinned!;
        var inWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.Contains("WAITFOR")) { inWait.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return FakeResult.None;
        };
        var r = rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "WAITFOR DELAY '00:10:00'" });
        await inWait.Task.WaitAsync(Wait);
        rig.Executions.Cancel(r.ExecutionId!);
        var done = await rig.Notifier.WaitDone(r.ExecutionId!);

        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.Same(pinned, s.Pinned);
        Assert.Equal(TxState.Open, s.TxState);
        Assert.DoesNotContain(((FakeDbConnection)pinned.Db).Log, sql => sql.Contains("ROLLBACK", StringComparison.OrdinalIgnoreCase));
        var next = rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "SELECT 1" });
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(next.ExecutionId!)).State);
        await rig.Sessions.TxAsync(s.Id, "rollback", CancellationToken.None);
        Assert.Null(s.Pinned);
    }

    [Fact]
    public async Task A_connection_lost_during_a_pinned_transaction_is_discarded_once_and_the_user_is_told()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None);
        var pinned = (FakeDbConnection)s.Pinned!.Db;
        rig.Provider.Handler = (cmd, _) =>
        {
            if (cmd.CommandText.Contains("boom")) { cmd.Fake.Break(); throw new InvalidOperationException("connection reset"); }
            return Task.FromResult(FakeResult.None);
        };
        var r = rig.Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = "SELECT 'boom'" });
        await rig.Notifier.WaitDone(r.ExecutionId!);
        Assert.Null(s.Pinned);
        Assert.Equal(TxState.None, s.TxState);
        Assert.Equal(1, pinned.DisposeCount);
        Assert.Equal(0, rig.Pool.Total);
        Assert.Contains(rig.Notifier.Of<ServerMessage>(), m => m.Message.Contains("rolled back by the server"));
    }
}
