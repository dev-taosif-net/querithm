using Querithm.Contract;
using Querithm.Core;

namespace Querithm.Tests.Integration;

/// <summary>Session lifecycle, transactions, database switching, pools and profile tests on both engines. Enable with QUERITHM_IT=1.</summary>
public class SessionLifecycleIntegrationTests
{
    static SessionRef R(string sid) => new() { SessionId = sid };

    public abstract class Base(string engine)
    {
        protected abstract Task<(Profile Profile, string SessionId)> Open(TestHost h);
        protected abstract string OtherDatabase { get; }
        protected abstract string CurrentDbSql { get; }
        protected string Engine => engine;

        [IntegrationFact]
        public async Task Switching_database_updates_the_session_and_is_refused_while_a_transaction_is_open()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var r = await h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = OtherDatabase });
            Assert.Equal(OtherDatabase, r.Database, ignoreCase: true);
            Assert.Equal(OtherDatabase, (await h.Rpc.SessionStateGet(R(sid))).Database, ignoreCase: true);
            var (done, batches, _) = await h.RunAsync(sid, CurrentDbSql);
            Assert.Equal(ExecState.Done, done.State);
            Assert.Equal(OtherDatabase, ((Apache.Arrow.StringArray)batches[0].Column(0)).GetString(0), ignoreCase: true);

            await h.Rpc.TxBegin(R(sid));
            if (Engine == "pg")
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = "postgres" }));
                Assert.Contains("transaction", ex.Message);
            }
            else
            {
                // SQL Server switches the pinned connection itself (USE).
                var back = await h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = "master" });
                Assert.Equal("master", back.Database, ignoreCase: true);
            }
            await h.Rpc.TxRollback(R(sid));
        }

        [IntegrationFact]
        public async Task Switching_to_a_database_that_does_not_exist_keeps_the_old_one()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var before = (await h.Rpc.SessionStateGet(R(sid))).Database;
            await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = "cov_no_such_database" }));
            Assert.Equal(before, (await h.Rpc.SessionStateGet(R(sid))).Database);
            var (done, _, _) = await h.RunAsync(sid, "SELECT 1");
            Assert.Equal(ExecState.Done, done.State);
        }

        [IntegrationFact]
        public async Task Closing_with_an_open_transaction_needs_force_and_discards_the_connection()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var st = await h.Rpc.TxBegin(R(sid));
            Assert.Equal(TxState.Open, st.TxState);
            Assert.NotNull(st.TxStartedAt);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid }));
            Assert.Contains("open transaction", ex.Message);
            await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid, Force = true });
            await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.SessionStateGet(R(sid)));
            await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });   // closing twice is harmless
        }

        [IntegrationFact]
        public async Task Transactions_cannot_start_or_switch_database_while_a_query_runs()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var wait = Engine == "pg" ? "SELECT pg_sleep(30)" : "WAITFOR DELAY '00:00:30'";
            var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = wait });
            await Task.Delay(500);
            Assert.True((await h.Rpc.SessionStateGet(R(sid))).Busy);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = OtherDatabase }));
            Assert.Contains("executing", ex.Message);
            var tx = await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.TxBegin(R(sid)));
            Assert.Contains("executing", tx.Message);
            await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
            Assert.Equal(ExecState.Cancelled, (await h.Notifier.WaitDone(r.ExecutionId!, 10_000)).State);
        }

        [IntegrationFact]
        public async Task A_second_query_on_a_busy_session_is_queued_then_runs()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var slow = Engine == "pg" ? "SELECT pg_sleep(2)" : "WAITFOR DELAY '00:00:02'";
            var first = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = slow });
            // The second goes in once the first holds the session (query.started), not after a fixed delay: on a cold CI
            // runner the first run's parse could outlast the delay and let the second take the session first.
            var started = DateTime.UtcNow;
            while (!h.Notifier.Of<QueryStartedEvent>().Any(e => e.ExecutionId == first.ExecutionId))
            {
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the first query never started");
                await Task.Delay(20);
            }
            var second = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 42 AS n" });
            var firstDone = await h.Notifier.WaitDone(first.ExecutionId!);
            Assert.Equal(ExecState.Done, firstDone.State);
            Assert.Equal(ExecState.Done, (await h.Notifier.WaitDone(second.ExecutionId!)).State);
            Assert.True(h.Notifier.Of<QueryQueuedEvent>().Any(q => q.ExecutionId == second.ExecutionId),
                $"second not queued; first ran {firstDone.ElapsedMs:0} ms; queued: [{string.Join(", ", h.Notifier.Of<QueryQueuedEvent>().Select(q => q.ExecutionId == first.ExecutionId ? "first" : q.ExecutionId))}]");
        }

        [IntegrationFact]
        public async Task Deleting_a_profile_drops_its_pooled_connections_and_the_profile()
        {
            await using var h = new TestHost();
            var (profile, sid) = await Open(h);
            var (done, _, _) = await h.RunAsync(sid, "SELECT 1");
            Assert.Equal(ExecState.Done, done.State);
            await h.Rpc.ProfileDelete(new ProfileRef { ProfileId = profile.Id });
            Assert.DoesNotContain(await h.Rpc.ProfileList(), p => p.Id == profile.Id);
        }

        [IntegrationFact]
        public async Task Profile_test_reports_success_and_scrubbed_failures()
        {
            await using var h = new TestHost();
            var (profile, _) = await Open(h);
            var ok = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = profile, Secret = Engine == "pg" ? TestHost.PgPassword : TestHost.MssqlPassword });
            Assert.True(ok.Ok, ok.Error);
            Assert.False(string.IsNullOrEmpty(ok.ServerVersion));
            var bad = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = profile, Secret = "wrong-pw-xyz-123" });
            Assert.False(bad.Ok);
            Assert.False(string.IsNullOrEmpty(bad.Error));
            Assert.DoesNotContain("wrong-pw-xyz-123", bad.Error);
            var saved = await h.Rpc.ProfileTest(new ProfileTestParams { ProfileId = profile.Id, Secret = Engine == "pg" ? TestHost.PgPassword : TestHost.MssqlPassword });
            Assert.True(saved.Ok, saved.Error);
            await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.ProfileTest(new ProfileTestParams()));
        }

        [IntegrationFact]
        public async Task Cells_of_a_finished_execution_can_be_read_and_closed_executions_report_unavailable()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var sql = Engine == "pg"
                ? "SELECT repeat('x', 1200000) AS s, decode(repeat('41', 200000), 'hex') AS b, 'short'::text AS small"
                : "SELECT REPLICATE(CAST(N'x' AS nvarchar(max)), 1200000) AS s, CAST(REPLICATE(CAST(0x41 AS varbinary(max)), 200000) AS varbinary(max)) AS b, N'short' AS small";
            var (done, _, id) = await h.RunAsync(sid, sql);
            Assert.Equal(ExecState.Done, done.State);
            var text = await h.Rpc.QueryCell(new CellParams { ExecutionId = id, ResultIndex = 0, Row = 0, Col = 0 });
            Assert.False(text.Unavailable);
            Assert.False(text.IsBinary);
            Assert.Equal(1_200_000, text.Value!.Length);
            var bin = await h.Rpc.QueryCell(new CellParams { ExecutionId = id, ResultIndex = 0, Row = 0, Col = 1 });
            Assert.True(bin.IsBinary);
            Assert.Equal(200_000, Convert.FromBase64String(bin.Value!).Length);
            // Values that were not truncated are in the Arrow batch already, not in the cell store.
            Assert.True((await h.Rpc.QueryCell(new CellParams { ExecutionId = id, ResultIndex = 0, Row = 0, Col = 2 })).Unavailable);
            await h.Rpc.QueryClose(new ExecutionRef { ExecutionId = id });
            Assert.True((await h.Rpc.QueryCell(new CellParams { ExecutionId = id, ResultIndex = 0, Row = 0, Col = 0 })).Unavailable);
            await h.Rpc.QueryClose(new ExecutionRef { ExecutionId = id });   // idempotent
        }

        [IntegrationFact]
        public async Task A_syntax_error_fails_the_execution_with_a_message_and_leaves_the_session_usable()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var (done, _, id) = await h.RunAsync(sid, "SELEC 1 FROMM");
            Assert.Equal(ExecState.Failed, done.State);
            Assert.NotNull(done.Error);
            Assert.NotEmpty(h.Messages(id));
            var (again, _, _) = await h.RunAsync(sid, "SELECT 1");
            Assert.Equal(ExecState.Done, again.State);
        }
    }

    [Collection("mssql")]
    public class SqlServer() : Base("mssql")
    {
        protected override Task<(Profile Profile, string SessionId)> Open(TestHost h) => h.OpenMssqlAsync("master");
        protected override string OtherDatabase => "tempdb";
        protected override string CurrentDbSql => "SELECT DB_NAME() AS d";
    }

    [Collection("pg")]
    public class Postgres() : Base("pg")
    {
        protected override Task<(Profile Profile, string SessionId)> Open(TestHost h) => h.OpenPgAsync("postgres");
        protected override string OtherDatabase => "template1";
        protected override string CurrentDbSql => "SELECT current_database() AS d";
    }

    [Fact]
    public async Task Synthetic_results_stream_without_a_database()
    {
        await using var h = new TestHost();
        var r = await h.Rpc.BenchSynthetic(new SyntheticParams { Rows = 2500, Columns = 4 });
        var done = await h.Notifier.WaitDone(r.ExecutionId!, 20_000);
        Assert.Equal(ExecState.Done, done.State);
        Assert.Equal(2500, done.AffectedRows);
        Assert.Contains(h.Frames.For(r.ExecutionId!), f => f.Kind == FrameKind.Batch);
        Assert.Contains(h.Frames.For(r.ExecutionId!), f => f.Kind == FrameKind.EndOfExecution);
    }
}
