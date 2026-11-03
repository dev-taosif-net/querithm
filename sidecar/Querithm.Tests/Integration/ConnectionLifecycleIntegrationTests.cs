using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// Cancellation and transaction state on real engines (plan B0 row 3, ARCHITECTURE §6 "Physical connection lifecycle"):
/// a cancel never rolls back a user's transaction and keeps the session on its pinned connection; when the server itself
/// ends the transaction (SQL Server XACT_ABORT ON) the user is told. Enable with QUERITHM_IT=1.
/// </summary>
public class ConnectionLifecycleIntegrationTests
{
    static SessionRef R(string sid) => new() { SessionId = sid };
    const string Ended = "has ended on the server";

    public abstract class Base(Engine engine)
    {
        protected bool Ms => engine == Engine.Mssql;
        protected Task<(Profile Profile, string SessionId)> Open(TestHost h) => Ms ? h.OpenMssqlAsync("master") : h.OpenPgAsync("postgres");
        string Pid => Ms ? "SELECT CAST(@@SPID AS nvarchar(10)) AS s" : "SELECT pg_backend_pid()::text AS s";
        string LongWait => Ms ? "WAITFOR DELAY '00:00:30'" : "SELECT pg_sleep(30)";

        protected static async Task<string> TextAsync(TestHost h, string sid, string sql)
        {
            var (done, batches, _) = await h.RunAsync(sid, sql);
            Assert.Equal(ExecState.Done, done.State);
            return ((Apache.Arrow.StringArray)batches[0].Column(0)).GetString(0);
        }

        protected static async Task<QueryDoneEvent> CancelAsync(TestHost h, string sid, string sql)
        {
            var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql });
            await Task.Delay(700); // the statement is running on the server
            await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
            var done = await h.Notifier.WaitDone(r.ExecutionId!, 15_000);
            Assert.Equal(ExecState.Cancelled, done.State);
            return done;
        }

        [IntegrationFact]
        public async Task A_cancel_inside_a_transaction_keeps_the_transaction_open_on_the_same_pinned_connection()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            await h.Rpc.TxBegin(R(sid));
            var (setup, _, _) = await h.RunAsync(sid, Ms ? "CREATE TABLE #lc(v int); INSERT #lc VALUES (1);" : "CREATE TEMP TABLE lc(v int); INSERT INTO lc VALUES (1);");
            Assert.Equal(ExecState.Done, setup.State);
            var before = await TextAsync(h, sid, Pid);

            var cancelled = await CancelAsync(h, sid, LongWait);

            Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(R(sid))).TxState); // never rolled back by Querithm
            Assert.DoesNotContain(h.Messages(cancelled.ExecutionId), m => m.Contains(Ended));
            if (Ms)
            {
                // XACT_ABORT OFF: the transaction and its row survive the cancel, on the same connection.
                Assert.Equal(before, await TextAsync(h, sid, Pid));
                Assert.Equal("1", await TextAsync(h, sid, "SELECT CAST(COUNT(*) AS nvarchar(10)) FROM #lc"));
                await h.Rpc.TxRollback(R(sid));
            }
            else
            {
                // PG: the transaction is aborted (25P02 until rollback) but still the user's to end; the session stays pinned.
                var (aborted, _, _) = await h.RunAsync(sid, "SELECT 1");
                Assert.Equal(ExecState.Failed, aborted.State);
                var st = await h.Rpc.SessionStateGet(R(sid));
                Assert.Equal(TxState.Open, st.TxState);
                Assert.Equal(before, st.Spid?.ToString(System.Globalization.CultureInfo.InvariantCulture)); // still the pinned connection
                await h.Rpc.TxRollback(R(sid));
            }
            Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(R(sid))).TxState);
            Assert.Equal(ExecState.Done, (await h.RunAsync(sid, "SELECT 1")).Done.State); // usable after the cancel
        }

        [IntegrationFact]
        public async Task A_cancel_outside_a_transaction_leaves_the_session_usable_and_returns_the_connection()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var before = await TextAsync(h, sid, Pid);
            await CancelAsync(h, sid, LongWait);
            var st = await h.Rpc.SessionStateGet(R(sid));
            Assert.Equal(TxState.None, st.TxState);
            Assert.False(st.Busy);
            Assert.Equal(before, await TextAsync(h, sid, Pid)); // the same pooled connection, not a new one
        }
    }

    [Collection("mssql")]
    public class SqlServer() : Base(Engine.Mssql)
    {
        [IntegrationFact]
        public async Task With_xact_abort_on_a_cancel_rolls_the_transaction_back_on_the_server_and_the_user_is_told()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            await h.Rpc.TxBegin(R(sid));
            var (setup, _, _) = await h.RunAsync(sid, "SET XACT_ABORT ON; CREATE TABLE #lc(v int); INSERT #lc VALUES (1);");
            Assert.Equal(ExecState.Done, setup.State);

            var cancelled = await CancelAsync(h, sid, "WAITFOR DELAY '00:00:30'");

            // The server rolled back (B0 row 3); the post-execution @@TRANCOUNT read sees it, unpins and says so.
            Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(R(sid))).TxState);
            Assert.Contains(h.Messages(cancelled.ExecutionId), m => m.Contains(Ended));
            // CREATE TABLE is transactional: the rollback removed the temp table with its row. The session is usable.
            Assert.Equal("rolled back", await TextAsync(h, sid, "SET XACT_ABORT OFF; SELECT CASE WHEN OBJECT_ID('tempdb..#lc') IS NULL THEN N'rolled back' ELSE N'kept' END"));
        }
    }

    [Collection("pg")]
    public class Postgres() : Base(Engine.Pg);
}
