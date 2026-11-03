using System.Data;
using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// H5: a grid Apply is all or nothing whatever ends it. A cancel (or any non-database exception) used to skip the rollback,
/// so the half-applied patch was then pinned as the tab's open transaction and a retry duplicated its inserts. Each engine's
/// transaction control and DML dialect run over fake connections; the second statement is where the patch is cut short.
/// </summary>
public class GridApplyRecoveryTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Engines => new() { "mssql", "pg", "mysql" };

    /// <summary>A server-side error on a working connection (what a driver raises for a refused statement).</summary>
    sealed class ServerError(string message) : DbException(message);

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public ExecutionService Executions { get; }
        public DataEditorService Editor { get; }
        public string Engine { get; }
        public IDataEditDialect Dialect { get; }
        readonly Profile _profile;
        /// <summary>MySQL's transaction state as the fake server keeps it (its performance_schema answers from this).</summary>
        int _mysqlDepth;

        /// <summary>The running Apply's execution id (known before its first statement).</summary>
        public volatile string? Id;
        /// <summary>Patch statements (not BEGIN, savepoints or state reads) run so far.</summary>
        public int Writes;
        /// <summary>What a patch statement does: null = succeeds; the argument is its 1-based position.</summary>
        public Func<int, FakeDbCommand, CancellationToken, Task<FakeResult>>? OnWrite;
        /// <summary>Transaction statements (rollbacks, commits) the test wants to fail.</summary>
        public Func<string, Exception?> FailTx { get; set; } = _ => null;
        /// <summary>Sees every other statement with the token it runs under (a driver honours it).</summary>
        public Action<string, CancellationToken>? BeforeTx { get; set; }

        public Rig(string engine)
        {
            Engine = engine;
            (Dialect, Provider.Transactions) = engine switch
            {
                "pg" => ((IDataEditDialect)new PostgresDataEdit(), (ITransactionControl)new FakePgTransactions()),
                "mysql" => (new MySqlDataEdit(), new MySqlTransactions()),
                _ => (new SqlServerDataEdit(), new FakeTransactions()),
            };
            Provider.DataEditImpl = Dialect;
            Provider.Handler = Handle;
            var store = LocalStore.InMemory();
            var profiles = new ProfileStore(store);
            _profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Querithm.Contract.Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Log.Null);
            Sessions.RememberSecret(_profile.Id, "x");
            Executions = new ExecutionService(Sessions, new CapturingFrames(), Notifier, new HistoryStore(store), Log.Null)
            {
                CancelGrace = TimeSpan.FromSeconds(5),
            };
            // The patch path reads neither the metadata store nor the schema cache.
            Editor = new DataEditorService(Sessions, Executions, null!, null!, Log.Null);
        }

        static bool IsWrite(string sql) => sql.Contains("INSERT INTO", StringComparison.Ordinal) || sql.StartsWith("UPDATE ", StringComparison.Ordinal);

        Task<FakeResult> Handle(FakeDbCommand cmd, CancellationToken ct)
        {
            var sql = cmd.CommandText.Trim();
            if (IsWrite(sql))
            {
                var n = Interlocked.Increment(ref Writes);
                return OnWrite?.Invoke(n, cmd, ct) ?? Task.FromResult(FakeResult.None);
            }
            BeforeTx?.Invoke(sql, ct);
            if (FailTx(sql) is { } fail) throw fail;
            if (Engine != "mysql") return Task.FromResult(FakeResult.None);
            // MySQL: performance_schema records this connection's transactions; the event says whether one is open.
            if (sql == MySqlTransactions.InstrumentedSql.Trim()) return Task.FromResult(FakeResult.Value(1));
            if (sql == MySqlTransactions.EventSql) return Task.FromResult(FakeResult.Value(_mysqlDepth > 0 ? 1 : 0));
            if (sql == "START TRANSACTION") _mysqlDepth = 1;
            else if (sql is "COMMIT" or "ROLLBACK") _mysqlDepth = 0;
            return Task.FromResult(FakeResult.None);
        }

        public Session Open() => Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = _profile.Id, Secret = "x" }).SessionId);

        string T(string mssql, string pg, string mysql) => Engine switch { "pg" => pg, "mysql" => mysql, _ => mssql };

        /// <summary>A table with an identity key and a text column; the patch inserts <paramref name="rows"/> rows.</summary>
        public (ResultEditInfo Edit, GridPatchParams Patch) Inserts(Session s, int rows)
        {
            var edit = new ResultEditInfo
            {
                Editable = true, ObjectId = 1, Schema = T("dbo", "public", "app"), Table = "t", KeyColumns = ["id"], KeyOrdinals = [0],
                Columns =
                [
                    new EditColumn { Ordinal = 0, Column = "id", Type = T("int", "integer", "int"), Key = true, Generated = true, HasDefault = true, Comparable = true },
                    new EditColumn { Ordinal = 1, Column = "name", Type = T("nvarchar(20)", "text", "varchar(20)"), Nullable = true, Comparable = true },
                ],
            };
            var patch = new GridPatchParams
            {
                SessionId = s.Id, ExecutionId = "x",
                Inserts = Enumerable.Range(0, rows).Select(i => new GridRowInsert { TempIndex = i, Values = new() { ["name"] = new GridValue { Text = $"n{i}" } } }).ToArray(),
            };
            return (edit, patch);
        }

        /// <summary>Runs the patch as grid Apply does (one tool execution); the id is known before the body starts.</summary>
        public async Task<(Task<(GridApplyResult Result, string Id)> Run, string Id)> ApplyAsync(Session s, ResultEditInfo edit, GridPatchParams patch)
        {
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var statements = PatchBuilder.Build(Dialect, edit, patch, false);
            var run = Executions.RunToolAsync(s, "-- grid", ctx => Editor.RunPatchAsync(ctx, Dialect, edit, patch, statements),
                r => (r.AppliedInserts, r.Ok ? null : r.Error), id => { Id = id; started.SetResult(id); });
            return (run, await started.Task.WaitAsync(Wait));
        }

        /// <summary>The second patch statement is cut short by Cancel, as the driver reports it.</summary>
        public void CancelAtSecondWrite() =>
            OnWrite = (n, _, ct) =>
            {
                if (n < 2) return Task.FromResult(FakeResult.None);
                Executions.Cancel(Id!);
                throw new OperationCanceledException(ct);
            };

        public List<string> Statements() => Provider.Opened.SelectMany(c => c.Log).ToList();
        public IEnumerable<ServerMessage> Messages(string id) => Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == id);

        public async ValueTask DisposeAsync()
        {
            await Executions.DisposeAsync();
            await Sessions.DisposeAsync();
        }
    }

    string RollbackSql(string engine) => engine == "mssql" ? "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION" : "ROLLBACK";
    string SavepointRollbackSql(string engine) => engine == "mssql" ? "ROLLBACK TRANSACTION querithm_grid" : "ROLLBACK TO SAVEPOINT querithm_grid";
    string CommitSql(string engine) => engine == "mssql" ? "COMMIT TRANSACTION" : "COMMIT";

    [Theory, MemberData(nameof(Engines))]
    public async Task A_cancel_during_apply_rolls_the_patch_back_and_leaves_no_transaction_behind(string engine)
    {
        await using var rig = new Rig(engine);
        var s = rig.Open();
        var (edit, patch) = rig.Inserts(s, 3);
        rig.CancelAtSecondWrite();
        var (run, id) = await rig.ApplyAsync(s, edit, patch);

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => run.WaitAsync(Wait));
        Assert.Equal("cancelled", ex.Code);
        var done = await rig.Notifier.WaitDone(id);
        Assert.Equal(ExecState.Cancelled, done.State);
        // Nothing written: the patch's own transaction was rolled back, never committed, and the tab holds none.
        var log = rig.Statements();
        Assert.Contains(RollbackSql(engine), log);
        Assert.DoesNotContain(CommitSql(engine), log);
        Assert.Equal(2, rig.Writes);
        Assert.Equal((TxState.None, (PhysicalConnection?)null), (s.TxState, s.Pinned));
        Assert.Equal(1, s.ExecLock.CurrentCount);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_cancel_inside_the_users_transaction_goes_back_to_the_savepoint_and_keeps_that_transaction(string engine)
    {
        await using var rig = new Rig(engine);
        var s = rig.Open();
        await rig.Sessions.TxAsync(s.Id, "begin", default);
        Assert.Equal(TxState.Open, s.TxState);
        var (edit, patch) = rig.Inserts(s, 3);
        rig.CancelAtSecondWrite();
        var (run, id) = await rig.ApplyAsync(s, edit, patch);

        Assert.Equal("cancelled", (await Assert.ThrowsAsync<QuerithmException>(() => run.WaitAsync(Wait))).Code);
        await rig.Notifier.WaitDone(id);
        var log = rig.Statements();
        Assert.Contains(SavepointRollbackSql(engine), log);
        Assert.DoesNotContain(RollbackSql(engine), log); // the user's transaction is theirs to end
        Assert.Equal(TxState.Open, s.TxState);
        Assert.NotNull(s.Pinned);
        Assert.True(s.Pinned!.IsOpen);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_rollback_that_cannot_run_closes_the_connection_so_the_server_rolls_back(string engine)
    {
        await using var rig = new Rig(engine);
        var s = rig.Open();
        var (edit, patch) = rig.Inserts(s, 2);
        rig.CancelAtSecondWrite();
        rig.FailTx = sql => sql == RollbackSql(engine) ? new InvalidOperationException("the server stopped answering") : null;
        var (run, id) = await rig.ApplyAsync(s, edit, patch);

        await Assert.ThrowsAsync<QuerithmException>(() => run.WaitAsync(Wait));
        await rig.Notifier.WaitDone(id);
        var conn = Assert.Single(rig.Provider.Opened);
        Assert.NotEqual(ConnectionState.Open, conn.State); // closed: the uncommitted rows go with it
        Assert.Contains(rig.Messages(id), m => m.Message.Contains("could not roll back the grid changes", StringComparison.Ordinal));
        Assert.Equal((TxState.None, (PhysicalConnection?)null), (s.TxState, s.Pinned));
    }

    [Fact]
    public async Task Inside_the_users_transaction_an_undo_that_cannot_run_closes_the_connection_and_says_the_transaction_is_gone()
    {
        await using var rig = new Rig("pg");
        var s = rig.Open();
        await rig.Sessions.TxAsync(s.Id, "begin", default);
        var (edit, patch) = rig.Inserts(s, 2);
        rig.CancelAtSecondWrite();
        rig.FailTx = sql => sql.StartsWith("ROLLBACK", StringComparison.Ordinal) ? new InvalidOperationException("the server stopped answering") : null;
        var (run, id) = await rig.ApplyAsync(s, edit, patch);

        await Assert.ThrowsAsync<QuerithmException>(() => run.WaitAsync(Wait));
        await rig.Notifier.WaitDone(id);
        Assert.Contains(rig.Messages(id), m => m.Message.Contains("your open transaction with them", StringComparison.Ordinal));
        // The lost-connection path: the pin is gone and the tab is told its transaction ended.
        Assert.Equal((TxState.None, (PhysicalConnection?)null), (s.TxState, s.Pinned));
        Assert.True(rig.Messages(id).Count(m => m.Severity == Severity.Warning) >= 2);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_commit_cut_short_says_the_outcome_is_unknown_so_the_webview_does_not_retry_blindly(string engine)
    {
        await using var rig = new Rig(engine);
        var s = rig.Open();
        var (edit, patch) = rig.Inserts(s, 2);
        rig.FailTx = sql =>
        {
            if (sql != CommitSql(engine)) return null;
            rig.Provider.Opened.Single().Break();
            return new InvalidOperationException("The connection is broken.");
        };
        var (run, id) = await rig.ApplyAsync(s, edit, patch);

        Assert.Equal("outcomeUnknown", (await Assert.ThrowsAsync<QuerithmException>(() => run.WaitAsync(Wait))).Code);
        Assert.Equal(ExecState.Failed, (await rig.Notifier.WaitDone(id)).State);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_commit_the_server_refuses_says_nothing_was_written(string engine)
    {
        await using var rig = new Rig(engine);
        var s = rig.Open();
        var (edit, patch) = rig.Inserts(s, 2);
        rig.FailTx = sql => sql == CommitSql(engine) ? new ServerError("deferred constraint violated") : null;
        var (run, id) = await rig.ApplyAsync(s, edit, patch);

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => run.WaitAsync(Wait));
        Assert.Equal("applyFailed", ex.Code);
        Assert.Contains("nothing was written", ex.Message, StringComparison.Ordinal);
        await rig.Notifier.WaitDone(id);
        Assert.Contains(RollbackSql(engine), rig.Statements());
        Assert.Equal(TxState.None, s.TxState);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_cancel_after_every_statement_ran_does_not_stop_the_commit(string engine)
    {
        await using var rig = new Rig(engine);
        var s = rig.Open();
        var (edit, patch) = rig.Inserts(s, 2);
        // The cancel lands as the COMMIT starts: every statement ran, so the patch is written once and reported as such.
        rig.BeforeTx = (sql, ct) =>
        {
            if (sql != CommitSql(engine)) return;
            rig.Executions.Cancel(rig.Id!);
            ct.ThrowIfCancellationRequested();
        };
        var (run, id) = await rig.ApplyAsync(s, edit, patch);

        var (result, _) = await run.WaitAsync(Wait);
        Assert.True(result.Ok);
        Assert.Equal(2, result.AppliedInserts);
        Assert.DoesNotContain(RollbackSql(engine), rig.Statements());
        Assert.Equal(ExecState.Done, (await rig.Notifier.WaitDone(id)).State);
        Assert.Equal(TxState.None, s.TxState);
    }

    [Fact]
    public async Task An_unknown_transaction_state_is_read_on_the_connection_and_nothing_is_written_when_it_stays_unknown()
    {
        await using var rig = new Rig("mssql");
        var s = rig.Open();
        await rig.Sessions.TxAsync(s.Id, "begin", default);
        s.TxState = TxState.Unknown; // e.g. a COMMIT whose deadline fired before its answer
        rig.FailTx = sql => sql == "SELECT @@TRANCOUNT" ? new InvalidOperationException("cannot read") : null;
        var (edit, patch) = rig.Inserts(s, 1);
        var (run, id) = await rig.ApplyAsync(s, edit, patch);

        Assert.Equal("txUnknown", (await Assert.ThrowsAsync<QuerithmException>(() => run.WaitAsync(Wait))).Code);
        await rig.Notifier.WaitDone(id);
        Assert.Equal(0, rig.Writes);
        Assert.Single(rig.Statements(), x => x == "BEGIN TRANSACTION"); // only the user's own BEGIN
        Assert.DoesNotContain("SAVE TRANSACTION querithm_grid", rig.Statements());
    }

    [Fact]
    public async Task An_unknown_state_that_reads_as_open_writes_under_a_savepoint_not_a_new_transaction()
    {
        await using var rig = new Rig("mssql");
        var s = rig.Open();
        await rig.Sessions.TxAsync(s.Id, "begin", default);
        s.TxState = TxState.Unknown;
        var (edit, patch) = rig.Inserts(s, 1);
        var (run, _) = await rig.ApplyAsync(s, edit, patch);

        Assert.True((await run.WaitAsync(Wait)).Result.Ok);
        var log = rig.Statements();
        Assert.Contains("SAVE TRANSACTION querithm_grid", log);
        Assert.Single(log, x => x == "BEGIN TRANSACTION");
        Assert.DoesNotContain("COMMIT TRANSACTION", log);
        Assert.Equal(TxState.Open, s.TxState);
    }
}
