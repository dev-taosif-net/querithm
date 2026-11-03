using Querithm.Contract;
using Querithm.Core;

namespace Querithm.Tests.Integration;

/// <summary>
/// Notebook run-all (Phase C3) on both engines. A notebook runs its SQL cells one execution each, in order, on its one
/// session; the webview's queue stops at a cell that failed (<c>cellFailed</c> in src/notebook/runtime.ts: state Failed or
/// Cancelled, an error, or an error message). These tests drive the same sequence through the RPC layer and check that
/// each engine gives that signal for a failing middle cell — PostgreSQL stops the cell at its first error; SQL Server's
/// batch goes on past a statement-level error, and the error message still stops the queue — and that the session runs
/// the next cell afterwards. Enable with QUERITHM_IT=1.
/// </summary>
public class NotebookRunIntegrationTests
{
    /// <summary>The webview's rule, mirrored: a failed, cancelled or erroring cell stops the queue.</summary>
    static bool CellFailed(QueryDoneEvent done, IEnumerable<ServerMessage> messages) =>
        done.State is ExecState.Failed or ExecState.Cancelled || done.Error is not null || messages.Any(m => m.Severity == Severity.Error);

    public abstract class Base
    {
        protected abstract Task<(Profile Profile, string SessionId)> Open(TestHost h);
        /// <summary>A cell of three statements whose second one fails at run time (division by zero).</summary>
        protected const string FailingCell = "SELECT 2 AS b;\nSELECT 1/0 AS boom;\nSELECT 22 AS after_error;";
        /// <summary>The engine's batch semantics: whether the statement after the failing one still ran.</summary>
        protected abstract bool RunsPastTheError { get; }

        [IntegrationFact]
        public async Task Run_all_stops_at_the_failing_middle_cell_keeps_earlier_output_and_the_session_runs_again()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            string[] cells = ["SELECT 1 AS a", FailingCell, "SELECT 3 AS c"];
            var ran = new List<(QueryDoneEvent Done, string ExecutionId, int Batches)>();
            foreach (var sql in cells)
            {
                var (done, batches, id) = await h.RunAsync(sid, sql);
                ran.Add((done, id, batches.Count));
                var messages = h.Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == id);
                if (CellFailed(done, messages)) break;
            }

            Assert.Equal(2, ran.Count); // the third cell never ran
            Assert.Equal(ExecState.Done, ran[0].Done.State);
            Assert.Equal(1, ran[0].Batches);
            var failing = ran[1];
            Assert.Contains(h.Notifier.Of<ServerMessage>(), m => m.ExecutionId == failing.ExecutionId && m.Severity == Severity.Error);
            var columns = h.Frames.For(failing.ExecutionId).Where(f => f.Kind == FrameKind.Schema)
                .SelectMany(f => SchemaOf(f.Payload).FieldsList.Select(x => x.Name)).ToList();
            Assert.Contains("b", columns);
            Assert.Equal(RunsPastTheError, columns.Contains("after_error"));

            // Re-running a cell after the failure works on the same session, outside any transaction.
            var (again, b, _) = await h.RunAsync(sid, "SELECT 3 AS c");
            Assert.Equal(ExecState.Done, again.State);
            Assert.Equal(3, Convert.ToInt32(((Apache.Arrow.Int32Array)b[0].Column(0)).GetValue(0)));
            Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        }

        [IntegrationFact]
        public async Task Cancelling_a_running_cell_ends_it_cancelled_so_the_queue_stops()
        {
            await using var h = new TestHost();
            var (_, sid) = await Open(h);
            var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = SlowCell });
            Assert.NotNull(r.ExecutionId);
            await Task.Delay(500);
            await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
            var done = await h.Notifier.WaitDone(r.ExecutionId!);
            Assert.True(CellFailed(done, h.Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == r.ExecutionId)));
            Assert.Equal(ExecState.Cancelled, done.State);
            var (next, _, _) = await h.RunAsync(sid, "SELECT 1");
            Assert.Equal(ExecState.Done, next.State);
        }

        protected abstract string SlowCell { get; }

        static Apache.Arrow.Schema SchemaOf(byte[] ipc)
        {
            using var reader = new Apache.Arrow.Ipc.ArrowStreamReader(new MemoryStream(ipc));
            return reader.Schema;
        }
    }

    public class SqlServer : Base
    {
        protected override Task<(Profile Profile, string SessionId)> Open(TestHost h) => h.OpenMssqlAsync("master");
        // Divide by zero ends the statement, not the batch (XACT_ABORT off): the statement after it still returns; its
        // error message is what stops the notebook.
        protected override bool RunsPastTheError => true;
        protected override string SlowCell => "WAITFOR DELAY '00:00:30'";
    }

    public class Postgres : Base
    {
        protected override Task<(Profile Profile, string SessionId)> Open(TestHost h) => h.OpenPgAsync("postgres");
        // One statement per batch, stopping at the first error (§16): nothing after the failing statement runs.
        protected override bool RunsPastTheError => false;
        protected override string SlowCell => "SELECT pg_sleep(30)";
    }
}
