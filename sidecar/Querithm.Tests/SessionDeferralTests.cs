using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// A metadata refresh deferred until a pooled session's transaction ends (§8 step 5, audit H11) is never lost: the
/// deferral and the transaction's end meet under one lock (G#5), a lost pinned connection ends the transaction too (A#11),
/// and the DDL event names the database the run ended in, read before a queued run can move the session (G#5).
/// </summary>
public class SessionDeferralTests
{
    static readonly TimeSpan Wait = TestTime.Of(TimeSpan.FromSeconds(10));

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public ExecutionService Executions { get; }
        readonly Profile _profile;

        public Rig()
        {
            var store = LocalStore.InMemory();
            var profiles = new ProfileStore(store);
            _profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Log.Null);
            Sessions.RememberSecret(_profile.Id, "x");
            Executions = new ExecutionService(Sessions, new CapturingFrames(), Notifier, new HistoryStore(store), Log.Null);
        }

        public Session Open() => Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = _profile.Id, Secret = "x" }).SessionId);
        public string Run(Session s, string sql) => Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = sql }).ExecutionId!;
        public Task<QueryDoneEvent> Done(string id) => Notifier.WaitDone(id, (int)Wait.TotalMilliseconds);

        public async ValueTask DisposeAsync()
        {
            await Executions.DisposeAsync();
            await Sessions.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_deferral_and_the_transactions_end_never_miss_each_other()
    {
        // One side defers (DDL in the transaction), the other ends the transaction and takes what was deferred, at the
        // same moment. Exactly one of them must read: the deferral sees no transaction, or the end finds the deferral.
        // Before, the check, the flag and the end's look at it were separate unlocked reads and writes.
        await using var rig = new Rig();
        var s = rig.Open();
        const int rounds = 20_000;
        int lost = 0, impossible = 0;
        using var round = new Barrier(2);
        bool deferred = false, taken = false;
        var ddl = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                round.SignalAndWait();
                deferred = s.DeferIfInTransaction(["db"]); // false: no transaction, so the caller reads now
                round.SignalAndWait();
            }
        });
        var commit = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                s.TxState = TxState.Open;
                round.SignalAndWait();
                s.TxState = TxState.None;               // COMMIT
                taken = s.TakeDeferral() != null;       // TransactionEnded
                round.SignalAndWait();
                // Both sides are done with the round: deferred but not taken means no read ever comes (a lost refresh).
                if (deferred && !taken) { lost++; s.TakeDeferral(); }
                if (!deferred && taken) impossible++;
            }
        });
        await Task.WhenAll(ddl, commit).WaitAsync(TestTime.Of(TimeSpan.FromSeconds(60)));
        Assert.Equal(0, lost);
        Assert.Equal(0, impossible);
        Assert.False(s.RefreshDeferred);
    }

    [Fact]
    public async Task Deferral_needs_an_open_transaction_and_is_taken_once()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        Assert.False(s.DeferIfInTransaction(["db"]));
        Assert.Null(s.TakeDeferral());

        s.TxState = TxState.Open;
        Assert.True(s.DeferIfInTransaction(["db", "other"]));
        Assert.True(s.RefreshDeferred);
        Assert.True(s.DefersRefreshOf("other"));
        Assert.False(s.DefersRefreshOf("third"));
        Assert.Equal(["db", "other"], s.TakeDeferral()!.Order());
        Assert.Null(s.TakeDeferral());
        Assert.False(s.DefersRefreshOf("other"));

        s.Closed = true;
        Assert.False(s.DeferIfInTransaction(["db"]));
    }

    [Fact]
    public async Task A_lost_pinned_connection_ends_the_transaction_and_its_deferred_refresh_runs()
    {
        // A#11: a run that lost its pinned connection set the transaction to none without saying so; the refresh deferred
        // until it ended waited for the tab's next run, and every refresh of the database stayed Stale meanwhile.
        await using var rig = new Rig();
        var s = rig.Open();
        var (conn, _) = await rig.Sessions.AcquireAsync(s, null, CancellationToken.None);
        s.Pinned = conn;
        s.TxState = TxState.Open;
        Assert.True(s.DeferIfInTransaction(["db"]));
        string[]? ranFor = null;
        rig.Sessions.TransactionEnded += x => ranFor = x.TakeDeferral();

        await rig.Sessions.ConnectionLostAsync(s, conn);

        Assert.Equal(TxState.None, s.TxState);
        Assert.Equal(["db"], ranFor!);
        Assert.False(s.RefreshDeferred);
    }

    [Fact]
    public async Task The_ddl_event_names_the_database_the_run_ended_in_even_after_a_queued_run_moved_the_session()
    {
        // G#5: the event was raised after the run let go of the tab, and the handler read session.Database — by then a
        // queued `USE c` could have moved it, and the database the DDL ran in (b, reached by a USE inside the run) was lost.
        await using var rig = new Rig();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.Contains("USE b", StringComparison.Ordinal))
            {
                cmd.Fake.ChangeDatabase("b");
                entered.TrySetResult();
                await go.Task.WaitAsync(ct);
            }
            else if (cmd.CommandText.Contains("USE c", StringComparison.Ordinal)) cmd.Fake.ChangeDatabase("c");
            return FakeResult.None;
        };
        var s = rig.Open();
        Assert.Null(s.Database); // no database configured: the first connection learns it
        string? second = null;
        (string? Start, string? End, string? SessionDb) seen = default;
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // First handler: holds the event until the queued run has moved the session, as a slow subscriber or a busy
        // machine would; the second sees what a handler sees then.
        rig.Executions.DdlExecuted += (_, _, _) => rig.Done(second!).Wait(Wait);
        rig.Executions.DdlExecuted += (x, start, end) => { seen = (start, end, x.Database); raised.TrySetResult(); };

        var first = rig.Run(s, "USE b;\nCREATE TABLE dbo.t (a int);");
        await entered.Task.WaitAsync(Wait);
        second = rig.Run(s, "USE c;"); // queued behind the first
        go.SetResult();
        await rig.Done(first);
        await raised.Task.WaitAsync(Wait);

        Assert.Equal("c", seen.SessionDb); // the race: the session had moved on before the handler ran
        // The database the run started in is the one its connection learned (review 2026-11-26): read before the
        // connection, it was null, and the database the run left (USE b) was never read again.
        Assert.Equal("db", seen.Start);
        Assert.Equal("b", seen.End);
    }
}
