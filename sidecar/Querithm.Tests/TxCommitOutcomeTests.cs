using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;
using Querithm.Tests.Security;

namespace Querithm.Tests;

/// <summary>
/// tx.commit / tx.rollback cut short (8a review of M5): no RPC deadline; a COMMIT that is stopped or loses its connection
/// says the outcome is unknown (never "try again"), and a later COMMIT that finds no transaction says so instead of
/// reporting success — PostgreSQL warns and MySQL says nothing when COMMIT runs outside a transaction. Each engine's
/// COMMIT-without-a-transaction behaviour is modelled over fake connections.
/// </summary>
public class TxCommitOutcomeTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Transaction control over fake connections; whether COMMIT outside a transaction succeeds is the engine's.</summary>
    sealed class EngineLikeTx(bool commitWithoutTransactionSucceeds) : ITransactionControl
    {
        public TransactionTraits Traits { get; } = new FakeTransactions().Traits;
        public volatile bool StateFails;

        static async Task Run(DbConnection c, string sql, CancellationToken ct)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public Task BeginAsync(DbConnection c, CancellationToken ct) => Run(c, "BEGIN TRAN", ct);
        public Task<bool> BeginReadOnlyAsync(DbConnection c, CancellationToken ct) => Task.FromResult(false);

        public async Task CommitAsync(DbConnection c, CancellationToken ct)
        {
            if (((FakeDbConnection)c).TxDepth == 0 && !commitWithoutTransactionSucceeds) throw new NoTransactionOpenException("No transaction is open.");
            await Run(c, "COMMIT", ct);
        }

        public Task RollbackAsync(DbConnection c, CancellationToken ct) => Run(c, "ROLLBACK", ct);
        public Task SavepointAsync(DbConnection c, string name, CancellationToken ct) => Run(c, $"SAVE TRANSACTION {name}", ct);
        public Task RollbackToSavepointAsync(DbConnection c, string name, CancellationToken ct) => Run(c, $"ROLLBACK TRANSACTION {name}", ct);
        public Task ReleaseSavepointAsync(DbConnection c, string name, CancellationToken ct) => Task.CompletedTask;

        public async Task<TxState> StateAsync(DbConnection c, CancellationToken ct)
        {
            await Run(c, "SELECT tx_state", ct);
            if (StateFails) throw new InvalidOperationException("the state cannot be read");
            return ((FakeDbConnection)c).TxDepth > 0 ? TxState.Open : TxState.None;
        }
    }

    /// <summary>Engine, whether its COMMIT outside a transaction succeeds, whether the session owns its connection.</summary>
    public static TheoryData<Engine, bool, bool> Engines => new()
    {
        { Engine.Mssql, false, false }, // 3902: no corresponding BEGIN TRANSACTION
        { Engine.Pg, true, false },     // WARNING: there is no transaction in progress
        { Engine.MySql, true, false },  // silently
        { Engine.Sqlite, false, true }, // the provider refuses: no transaction is open
        { Engine.DuckDb, false, true },
    };

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public EngineLikeTx Tx { get; }
        readonly Profile _profile;

        public Rig(bool lenientCommit, bool sessionOwned)
        {
            Tx = new EngineLikeTx(lenientCommit);
            Provider.Transactions = Tx;
            if (sessionOwned) Provider.Lifetime = ConnectionLifetime.SessionOwned;
            var profiles = new ProfileStore(LocalStore.InMemory());
            _profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Log.Null) { TxEndLimit = TimeSpan.FromMilliseconds(300) };
            Sessions.RememberSecret(_profile.Id, "x");
        }

        public Session Open() => Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = _profile.Id, Secret = "x" }).SessionId);

        public async Task<Session> OpenInTransaction()
        {
            var s = Open();
            await Sessions.TxAsync(s.Id, "begin", default);
            Assert.Equal(TxState.Open, s.TxState);
            return s;
        }

        public Task<SessionState> Commit(Session s) => Sessions.TxAsync(s.Id, "commit", default).WaitAsync(Wait);
        public Task<SessionState> Rollback(Session s) => Sessions.TxAsync(s.Id, "rollback", default).WaitAsync(Wait);

        /// <summary>COMMIT waits until it is cancelled; then the server has done <paramref name="onCancel"/>.</summary>
        public void CommitHangs(Action<FakeDbCommand> onCancel) => Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText is "COMMIT" or "ROLLBACK")
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { onCancel(cmd); throw; }
            }
            return FakeResult.None;
        };

        public int Commits => Provider.Opened.Sum(c => c.Log.Count(l => l == "COMMIT"));

        public async ValueTask DisposeAsync() => await Sessions.DisposeAsync();
    }

    static void AssertOutcomeUnknown(QuerithmException ex)
    {
        Assert.Equal("txOutcomeUnknown", ex.Code);
        Assert.Contains("whether the transaction committed is unknown", ex.Message);
        Assert.Contains("may have rolled it back", ex.Message);
        Assert.DoesNotContain("Try again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_commit_stopped_after_the_transaction_ended_reports_an_unknown_outcome_and_the_next_commit_says_nothing_was_open(Engine engine, bool lenient, bool owned)
    {
        _ = engine;
        await using var rig = new Rig(lenient, owned);
        var s = await rig.OpenInTransaction();
        rig.CommitHangs(cmd => cmd.Fake.TxDepth = 0); // the server ended the transaction as the cancel arrived

        AssertOutcomeUnknown(await Assert.ThrowsAsync<QuerithmException>(() => rig.Commit(s)));
        Assert.Equal(TxState.None, s.TxState);
        Assert.True(s.CommitOutcomeUnknown);

        rig.Provider.Handler = (_, _) => Task.FromResult(FakeResult.None);
        var again = await Assert.ThrowsAsync<QuerithmException>(() => rig.Commit(s));
        Assert.Equal("txNone", again.Code);
        Assert.Contains("stopped earlier", again.Message);
        Assert.Equal(1, rig.Commits); // the second COMMIT was never sent: on PostgreSQL / MySQL it would have "succeeded"
        Assert.False(s.CommitOutcomeUnknown);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_commit_stopped_with_the_state_unreadable_shows_unknown_and_a_retry_that_finds_no_transaction_is_not_success(Engine engine, bool lenient, bool owned)
    {
        _ = engine;
        await using var rig = new Rig(lenient, owned);
        var s = await rig.OpenInTransaction();
        rig.CommitHangs(cmd => { cmd.Fake.TxDepth = 0; rig.Tx.StateFails = true; });

        AssertOutcomeUnknown(await Assert.ThrowsAsync<QuerithmException>(() => rig.Commit(s)));
        Assert.Equal(TxState.Unknown, s.TxState); // TX ?: the tab offers both buttons
        Assert.NotNull(s.Pinned);

        rig.Tx.StateFails = false;
        rig.Provider.Handler = (_, _) => Task.FromResult(FakeResult.None);
        var again = await Assert.ThrowsAsync<QuerithmException>(() => rig.Commit(s));
        Assert.Equal("txNone", again.Code);
        Assert.Equal(TxState.None, s.TxState);
        Assert.Equal(1, rig.Commits);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_commit_stopped_before_it_ran_keeps_the_transaction_and_a_retry_commits(Engine engine, bool lenient, bool owned)
    {
        _ = engine;
        await using var rig = new Rig(lenient, owned);
        var s = await rig.OpenInTransaction();
        rig.CommitHangs(_ => { }); // the transaction is untouched

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => rig.Commit(s));
        Assert.Equal("cancelled", ex.Code);
        Assert.Contains("still open", ex.Message);
        Assert.Equal(TxState.Open, s.TxState);
        Assert.False(s.CommitOutcomeUnknown);

        rig.Provider.Handler = (_, _) => Task.FromResult(FakeResult.None);
        Assert.Equal(TxState.None, (await rig.Commit(s)).TxState);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_commit_whose_connection_drops_reports_an_unknown_outcome_and_the_next_commit_says_nothing_was_open(Engine engine, bool lenient, bool owned)
    {
        _ = engine;
        await using var rig = new Rig(lenient, owned);
        var s = await rig.OpenInTransaction();
        rig.Provider.Handler = (cmd, _) =>
        {
            if (cmd.CommandText != "COMMIT") return Task.FromResult(FakeResult.None);
            cmd.Fake.Break();
            throw new InvalidOperationException("Exception while reading from stream");
        };

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => rig.Commit(s));
        AssertOutcomeUnknown(ex);
        Assert.Contains("connection was lost during COMMIT", ex.Message);
        Assert.Equal((TxState.None, (PhysicalConnection?)null), (s.TxState, s.Pinned));
        Assert.Contains(rig.Notifier.Of<SessionState>(), st => st.SessionId == s.Id && st.TxState == TxState.None);

        var again = await Assert.ThrowsAsync<QuerithmException>(() => rig.Commit(s));
        Assert.Equal("txNone", again.Code);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_rollback_stopped_after_the_server_rolled_back_reports_success(Engine engine, bool lenient, bool owned)
    {
        _ = engine;
        await using var rig = new Rig(lenient, owned);
        var s = await rig.OpenInTransaction();
        rig.CommitHangs(cmd => cmd.Fake.TxDepth = 0);

        Assert.Equal(TxState.None, (await rig.Rollback(s)).TxState);
        Assert.False(s.CommitOutcomeUnknown);
    }

    [Fact]
    public async Task A_commit_while_the_state_is_unknown_that_finds_no_transaction_is_not_reported_as_committed()
    {
        await using var rig = new Rig(lenientCommit: true, sessionOwned: false); // PostgreSQL: COMMIT outside a transaction only warns
        var s = await rig.OpenInTransaction();
        s.TxState = TxState.Unknown; // pinned, state unknown
        ((FakeDbConnection)s.Pinned!.Db).TxDepth = 0; // a script's COMMIT ended it meanwhile

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => rig.Commit(s));
        Assert.Equal("txNone", ex.Code);
        Assert.Contains("already ended", ex.Message);
        Assert.Equal(0, rig.Commits);
    }

    [Fact]
    public async Task Forcing_the_tab_closed_ends_a_commit_waiting_on_the_server()
    {
        await using var rig = new Rig(lenientCommit: false, sessionOwned: false);
        rig.Sessions.TxEndLimit = TimeSpan.FromMinutes(30);
        var s = await rig.OpenInTransaction();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText == "COMMIT") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return FakeResult.None;
        };

        var commit = rig.Sessions.TxAsync(s.Id, "commit", default);
        await entered.Task.WaitAsync(Wait);
        await rig.Sessions.CloseAsync(new SessionCloseParams { SessionId = s.Id, Force = true }).WaitAsync(Wait);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => commit.WaitAsync(Wait));
        Assert.IsNotType<TimeoutException>(ex);
        Assert.Null(s.Pinned);
    }

    [Fact]
    public async Task Commit_has_no_rpc_deadline()
    {
        await using var h = new TestHostWithFake(TimeSpan.FromMilliseconds(200));
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "srv", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" })).SessionId;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        // A COMMIT that waits for a synchronous standby (or a large ROLLBACK) outlasts the interactive deadline and still ends as asked.
        h.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.StartsWith("COMMIT", StringComparison.Ordinal)) await Task.Delay(700, ct);
            return FakeResult.None;
        };
        Assert.Equal(TxState.None, (await h.Rpc.TxCommit(new SessionRef { SessionId = sid }).WaitAsync(Wait)).TxState);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        h.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.Contains("ROLLBACK", StringComparison.Ordinal)) await Task.Delay(700, ct);
            return FakeResult.None;
        };
        Assert.Equal(TxState.None, (await h.Rpc.TxRollback(new SessionRef { SessionId = sid }).WaitAsync(Wait)).TxState);
    }
}
