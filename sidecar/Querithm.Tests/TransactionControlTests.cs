using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Phase D0b: one provider-owned transaction API, and pool returns that read the transaction state — a connection left
/// inside a transaction (or whose state cannot be read) is discarded, never handed to the next borrower.
/// </summary>
public class TransactionControlTests
{
    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public Profile Profile { get; }
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public InvariantSink Invariants { get; } = FakeDbInvariants.Capture();

        public Rig()
        {
            var profiles = new ProfileStore(LocalStore.InMemory());
            Profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Log.Null);
            Sessions.RememberSecret(Profile.Id, "x");
            Invariants.LockHeld = () => Pool.LockHeldByCurrentThread;
        }

        public Pool Pool => Sessions.PoolFor(Profile, Provider, null);
        public Session Open() => Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = Profile.Id, Secret = "x" }).SessionId);

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            Assert.Empty(Invariants.Violations);
        }
    }

    static async Task Exec(PhysicalConnection c, string sql)
    {
        await using var cmd = c.Db.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_clean_connection_goes_back_to_the_pool_after_its_state_is_read()
    {
        await using var rig = new Rig();
        var c = await rig.Pool.BorrowAsync(null, null, default);
        await rig.Pool.ReturnAsync(c);
        Assert.Equal((ConnLifecycle.Idle, 1, 1), (c.State, rig.Pool.IdleCount, rig.Pool.Total));
        Assert.Contains("SELECT @@TRANCOUNT", ((FakeDbConnection)c.Db).Log);
    }

    [Fact]
    public async Task A_helper_that_leaves_a_transaction_open_never_hands_it_to_the_next_borrower()
    {
        await using var rig = new Rig();
        var c = await rig.Pool.BorrowAsync(null, null, default, purpose: "metadata");
        await Exec(c, "BEGIN TRANSACTION"); // e.g. a provider catalog read that began one and failed before ending it
        await rig.Pool.ReturnAsync(c);
        Assert.Equal(ConnLifecycle.Discarded, c.State);
        Assert.Equal((0, 0), (rig.Pool.IdleCount, rig.Pool.Total));
        Assert.Equal(1, ((FakeDbConnection)c.Db).DisposeCount);
        var next = await rig.Pool.BorrowAsync(null, null, default);
        Assert.NotSame(c, next);
        Assert.Equal(0, ((FakeDbConnection)next.Db).TxDepth);
        await rig.Pool.ReturnAsync(next);
    }

    [Fact]
    public async Task A_connection_whose_state_cannot_be_read_is_discarded()
    {
        await using var rig = new Rig();
        var c = await rig.Pool.BorrowAsync(null, null, default);
        rig.Provider.Handler = (cmd, _) => cmd.CommandText == "SELECT @@TRANCOUNT" ? throw new InvalidOperationException("broken pipe") : Task.FromResult(FakeResult.None);
        await rig.Pool.ReturnAsync(c);
        Assert.Equal(ConnLifecycle.Discarded, c.State);
        Assert.Equal(0, rig.Pool.Total);
    }

    [Fact]
    public async Task Returning_twice_or_after_a_discard_is_a_no_op()
    {
        await using var rig = new Rig();
        var c = await rig.Pool.BorrowAsync(null, null, default);
        await rig.Pool.ReturnAsync(c);
        await rig.Pool.ReturnAsync(c);
        Assert.Equal((1, 1), (rig.Pool.IdleCount, rig.Pool.Total));
    }

    [Fact]
    public async Task Tx_begin_commit_and_rollback_go_through_the_provider_control()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        var begun = await rig.Sessions.TxAsync(s.Id, "begin", default);
        Assert.Equal((TxState.Open, true), (begun.TxState, begun.Pinned));
        var conn = (FakeDbConnection)s.Pinned!.Db;
        Assert.Contains("BEGIN TRANSACTION", conn.Log);
        var committed = await rig.Sessions.TxAsync(s.Id, "commit", default);
        Assert.Equal((TxState.None, false), (committed.TxState, committed.Pinned));
        Assert.Contains("COMMIT TRANSACTION", conn.Log);
        await rig.Sessions.TxAsync(s.Id, "begin", default);
        var rolled = await rig.Sessions.TxAsync(s.Id, "rollback", default);
        Assert.Equal(TxState.None, rolled.TxState);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", conn.Log);
    }

    [Fact]
    public async Task A_commit_the_server_refuses_is_reported_and_the_state_is_read_again()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        await rig.Sessions.TxAsync(s.Id, "begin", default);
        var conn = (FakeDbConnection)s.Pinned!.Db;
        // The server already ended the transaction (XACT_ABORT, a killed session's rollback): COMMIT has nothing to commit.
        conn.TxDepth = 0;
        rig.Provider.Handler = (cmd, _) => cmd.CommandText == "COMMIT TRANSACTION"
            ? throw new InvalidOperationException("The COMMIT TRANSACTION request has no corresponding BEGIN TRANSACTION.")
            : Task.FromResult(FakeResult.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sessions.TxAsync(s.Id, "commit", default));
        Assert.Contains("no corresponding BEGIN", ex.Message);
        // Never shown as committed-and-still-open: the tab now shows what the server holds (no transaction) and is unpinned.
        Assert.Equal((TxState.None, (PhysicalConnection?)null), (s.TxState, s.Pinned));
        Assert.Contains(rig.Notifier.Of<SessionState>(), st => st.SessionId == s.Id && st.TxState == TxState.None);
    }

    [Fact]
    public async Task A_failed_commit_that_leaves_the_transaction_open_keeps_it_pinned()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        await rig.Sessions.TxAsync(s.Id, "begin", default);
        var pinned = s.Pinned;
        // A deferred constraint fails at commit time on an engine that keeps the transaction open afterwards.
        rig.Provider.Handler = (cmd, _) => cmd.CommandText == "COMMIT TRANSACTION" ? throw new InvalidOperationException("constraint violated at commit") : Task.FromResult(FakeResult.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sessions.TxAsync(s.Id, "commit", default));
        Assert.Equal((TxState.Open, pinned), (s.TxState, s.Pinned));
        rig.Provider.Handler = (_, _) => Task.FromResult(FakeResult.None);
        Assert.Equal(TxState.None, (await rig.Sessions.TxAsync(s.Id, "rollback", default)).TxState);
    }

    [Fact]
    public async Task A_failed_begin_never_pools_a_connection_left_inside_a_transaction()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        // BEGIN reached the server and then the reply failed: the connection may be inside a transaction.
        rig.Provider.Handler = (cmd, _) =>
        {
            if (cmd.CommandText == "BEGIN TRANSACTION") { cmd.Fake.TxDepth++; throw new InvalidOperationException("reply lost"); }
            return Task.FromResult(FakeResult.None);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sessions.TxAsync(s.Id, "begin", default));
        Assert.Equal((TxState.None, (PhysicalConnection?)null), (s.TxState, s.Pinned));
        Assert.Equal((0, 0), (rig.Pool.IdleCount, rig.Pool.Total));
    }

    [Fact]
    public async Task Session_helpers_return_with_a_state_check_but_executions_skip_the_second_read()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        await rig.Sessions.StateAsync(s.Id, default); // a helper borrow (identity/SPID read)
        var c = (FakeDbConnection)rig.Provider.Opened.Single();
        Assert.Equal(1, c.Log.Count(l => l == "SELECT @@TRANCOUNT"));
        Assert.Equal(1, rig.Pool.IdleCount);
        // A caller that just read the state passes it on: no second read.
        var (conn, borrowed) = await rig.Sessions.AcquireAsync(s, null, default);
        await rig.Sessions.ReleaseAsync(s, conn, borrowed, TxState.None);
        Assert.Equal(1, c.Log.Count(l => l == "SELECT @@TRANCOUNT"));
    }
}
