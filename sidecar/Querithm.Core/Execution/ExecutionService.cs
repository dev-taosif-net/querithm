using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Core.Sessions;
using Querithm.Core.Store;

namespace Querithm.Core.Execution;

public sealed class Execution(Ulid id, Session? session, string sql, ExecuteOptions options)
{
    public Ulid Id { get; } = id;
    public string IdText { get; } = id.ToString();
    public Session? Session { get; } = session;
    public string Sql { get; } = sql;
    public ExecuteOptions Options { get; } = options;
    public CancellationTokenSource Cts { get; } = new();
    public volatile DbCommand? CurrentCommand;
    public ExecState State { get; set; } = ExecState.Queued;
    public TruncatedCellStore Cells { get; } = new();
    public long LastTouchedMs { get; set; } = Time.NowMs();
    public long LastProgressMs { get; set; } = Time.NowMs();
    public bool Finished { get; set; }
    /// <summary>What each data result set read: kept until the tab closes or re-runs, so grid edits resolve the table here.</summary>
    public ConcurrentDictionary<int, RecordedResult> Results { get; } = new();
    /// <summary>M13: the run's messages, coalesced (created on the first one; see <see cref="ExecutionService"/>.Message).</summary>
    internal MessageBatcher? Batcher;
    /// <summary>L13: the run told the user it passed <see cref="ExecutionService.MaxResultSets"/>.</summary>
    internal bool ResultSetLimitNoted;
    /// <summary>
    /// The index the next data result set gets. Kept on the run, not in a batch's locals, so a set that a failing batch
    /// started still uses up its index: the next batch (SQL Server runs on after an error) never reuses it, so two
    /// result sets never share <see cref="Results"/> or the grid's edits.
    /// </summary>
    internal int NextResultIndex;

    /// <summary>
    /// The connection the run holds, from the moment it has one until it hands it back (pool or pin); guarded by
    /// <see cref="Gate"/> so the cancel escalation never closes a connection another borrower already owns.
    /// </summary>
    internal PhysicalConnection? Connection;
    internal readonly object Gate = new();
    internal int CancelRequested;
    /// <summary>The escalation claimed the connection to close it: the run discards it, never pools it.</summary>
    bool _aborting;

    internal void Hold(PhysicalConnection conn) { lock (Gate) Connection = conn; }

    /// <summary>The run hands its connection on; true when the escalation claimed it (it must be discarded, not reused).</summary>
    internal bool LetGo() { lock (Gate) { Connection = null; return _aborting; } }

    internal bool Aborting { get { lock (Gate) return _aborting; } }
    Task? _abortClose;

    /// <summary>
    /// Claims the connection the run still holds and starts <paramref name="close"/> on it (false once the run let it go).
    /// The close runs on its own task, never under the gate: a driver's Close may block, and <see cref="LetGo"/> must not.
    /// </summary>
    internal bool TryStartAbort(Func<PhysicalConnection, Task> close)
    {
        lock (Gate)
        {
            if (Connection is null) return false;
            _aborting = true;
            _abortClose = close(Connection);
            return true;
        }
    }

    /// <summary>Waits (bounded) for the escalation's close, so the run never disposes the connection while it is closing.</summary>
    internal async Task WaitAbortCloseAsync()
    {
        Task? close;
        lock (Gate) close = _abortClose;
        if (close is null) return;
        try { await close.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { /* logged by the closer; the discard goes ahead */ }
    }
}

/// <summary>A result set's columns, their table origins and the edit decision made when it started streaming.</summary>
public sealed record RecordedResult(ResultColumn[] Columns, Contract.Providers.ColumnOrigin?[]? Origins, string? ProfileId, string? Database, ResultEditInfo? Edit);

/// <summary>A Querithm-generated write running inside an execution (grid patch, import, sync): its connection and message sink.</summary>
public sealed class ToolContext(Execution exec, Session session, PhysicalConnection conn, Action<Severity, string> message, ILogger? log = null)
{
    public Execution Execution { get; } = exec;
    public Session Session { get; } = session;
    public PhysicalConnection Connection { get; } = conn;
    public CancellationToken Ct => Execution.Cts.Token;
    public void Message(Severity severity, string text) => message(severity, text);

    /// <summary>
    /// Whether a write runs inside the user's open transaction (under a savepoint) or in one of its own. A tab whose state
    /// is unknown is read on the connection: a BEGIN inside an open transaction would end or nest into the user's work,
    /// and a savepoint outside one protects nothing. When the server cannot tell either, nothing is written.
    /// </summary>
    public async Task<bool> InTransactionAsync(string what)
    {
        if (Session.Pinned != Connection || Session.TxState == TxState.None) return false;
        if (Session.TxState == TxState.Open) return true;
        TxState state;
        try { state = await Session.Provider.Transactions.StateAsync(Connection.Db, Ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException && !Ct.IsCancellationRequested) { state = TxState.Unknown; }
        return state switch
        {
            TxState.Open => true,
            TxState.None => false,
            _ => throw new QuerithmException("txUnknown",
                $"Querithm cannot tell whether a transaction is open on this tab's connection, so it cannot {what} safely: a transaction of its own would end or nest into yours. Commit or roll back first."),
        };
    }

    /// <summary>
    /// Closes the connection when Querithm could not undo its own uncommitted changes on it (the rollback failed or ran out
    /// of time): the server then rolls them back, so they never stay in a pooled connection or in the user's transaction.
    /// The run treats the connection as lost (an open transaction of the user's is gone with it, and the tab is told).
    /// </summary>
    public void AbandonConnection(string text)
    {
        if (!Connection.IsOpen) return; // gone already: the server rolled back, and the run reports the lost connection
        Message(Severity.Warning, text);
        Execution.TryStartAbort(c => Task.Run(() =>
        {
            try { c.Db.Close(); }
            catch (Exception ex) { log?.LogWarning("Execution {Id}: closing its connection failed: {Message}", Execution.IdText, ex.Message); }
        }));
    }
}

/// <summary>Runs executions as state machines on the thread pool (§7). The UI sends execute/cancel and reads frames.</summary>
public sealed class ExecutionService : IAsyncDisposable
{
    public const int DefaultMaxRows = 50_000;
    /// <summary>L13: result sets one run streams; a frame's result index is a u16 (§5), and 0xFFFF stays unused.</summary>
    public const int MaxResultSets = ushort.MaxValue;
    static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ForgetLimit = TimeSpan.FromHours(12);

    readonly SessionManager _sessions;
    readonly IFrameSink _frames;
    readonly INotifier _notifier;
    readonly HistoryStore _history;
    readonly ILogger _log;
    readonly Contract.Providers.ISchemaSource? _schema;
    readonly ConcurrentDictionary<string, Execution> _executions = new();
    readonly Timer _watchdog;

    /// <summary>
    /// Bound on a best-effort frame write (the end of a run): the run must still end, release its session and send
    /// query.done when nobody drains the data plane. Settable for tests.
    /// </summary>
    internal TimeSpan FrameDrainLimit { get; set; } = DefaultFrameDrainLimit;

    /// <summary>
    /// 35 s: with the data plane's FrameGrace (3 s) past the shell's STALE_CONSUMER (30 s) plus a 5 s margin (§5): a run's
    /// end frame that waits behind the shell's reader, paused on another run's full queue, outlasts that pause instead of
    /// dropping the client every tab shares (it was 10 s). A shell with no reader at all still fails at once (5 s client wait).
    /// </summary>
    internal static readonly TimeSpan DefaultFrameDrainLimit = TimeSpan.FromSeconds(35);

    /// <summary>
    /// How long a cancelled run may take to stop before its connection is closed (a server that never acknowledges the
    /// driver's cancel holds the run forever at the default command timeout of 0, §16), and again before the run is
    /// reported as stuck. Settable for tests.
    /// </summary>
    internal TimeSpan CancelGrace { get; set; } = DefaultCancelGrace;

    internal static readonly TimeSpan DefaultCancelGrace = TimeSpan.FromSeconds(5);

    /// <summary>Raised after an execution containing DDL completes (§8 step 5).</summary>
    public event Action<Session>? DdlExecuted;

    /// <summary>Decides whether the grid may edit a result set (set by the composition root; null = never editable).</summary>
    public Func<Session, ResultColumn[], Contract.Providers.ColumnOrigin?[]?, SchemaSnapshot?, ResultEditInfo?>? Editability { get; set; }

    public ExecutionService(SessionManager sessions, IFrameSink frames, INotifier notifier, HistoryStore history, ILogger log, Contract.Providers.ISchemaSource? schema = null)
    {
        _schema = schema;
        _sessions = sessions;
        _frames = frames;
        _notifier = notifier;
        _history = history;
        _log = log;
        Guard = new QueryGuard(sessions);
        _watchdog = new Timer(_ => Watchdog(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        // A closing tab's runs are cancelled before its connection closes: the close waits for them to let go of it.
        _sessions.SessionClosing += OnSessionClosing;
    }

    void OnSessionClosing(Session s) => CloseForSession(s.Id);

    public QueryExecuteResult Execute(QueryExecuteParams p)
    {
        var options = p.Options ?? new ExecuteOptions();
        // RPC input is untrusted: a negative row limit used to mean "unlimited" by accident (§7 Row fetch limits).
        if (options.MaxRows is < 0) throw QuerithmException.Invalid("The row limit must be 0 (no limit) or a positive number of rows.");
        var session = _sessions.Get(p.SessionId);
        var classification = session.Provider.Language.Classify(p.Sql);
        // §13 rule 7: the sidecar enforces the guards, not only the UI (the same check as plan.get and debug.attach).
        if (Guard.Check(session, classification, options.ConfirmedProd, options.ConfirmedDestructive) is { } block)
            return new QueryExecuteResult { Blocked = block };

        var exec = new Execution(Ulid.New(), session, p.Sql, options);
        _executions[exec.IdText] = exec;
        _ = Task.Run(() => RunAsync(exec, classification));
        return new QueryExecuteResult { ExecutionId = exec.IdText };
    }

    /// <summary>The guards on caller SQL and on Querithm-generated writes (§13 rule 7).</summary>
    public QueryGuard Guard { get; }

    /// <summary>§13 rule 7 for Querithm-generated writes (see <see cref="QueryGuard.Tool"/>).</summary>
    public GuardBlock? ToolGuard(Session session, bool confirmedProd, bool confirmedDestructive, DestructiveFinding[] findings, DestructiveFinding[] destructive) =>
        Guard.Tool(session, confirmedProd, confirmedDestructive, findings, destructive);

    /// <summary>The savepoint an actual plan sets inside the user's open transaction (rolled back to and released after it).</summary>
    internal const string PlanSavepoint = "querithm_plan";

    /// <summary>
    /// plan.get (§11): the text is guarded like query.execute (refusals, Prod first run, destructive confirm), then planned
    /// as one execution on the session — queued behind a running query, ended by query.cancel and the §7 escalation, closed
    /// with query.done; the plan arrives as query.plan. An actual plan runs the statement inside a transaction that is
    /// always rolled back, or a savepoint inside the user's open transaction; an engine without savepoints refuses it there.
    /// </summary>
    public QueryExecuteResult Plan(PlanParams p)
    {
        var session = _sessions.Get(p.SessionId);
        var actual = p.Mode == PlanMode.Actual;
        if (actual && !p.ConfirmedRun) throw new QuerithmException("confirmRun", "An actual plan executes the query. Confirm to continue.");
        var plans = session.Provider.Plans;
        if (plans.Refusal(p.Sql, p.Mode) is { } refusal) throw new QuerithmException("unsupported", refusal);
        var classification = session.Provider.Language.Classify(p.Sql);
        if (actual)
        {
            if (classification.BeginsTransaction || classification.EndsTransaction)
                throw new QuerithmException("unsupported",
                    "An actual plan runs inside a transaction that Querithm rolls back, and this text begins or ends a transaction itself, so it would step outside it. Plan the statements without BEGIN, COMMIT or ROLLBACK.");
            if (session.TxState != TxState.None && !session.Provider.Transactions.Traits.Savepoints)
                throw new QuerithmException("unsupported", NoSavepoints(session));
        }
        if (Guard.Check(session, classification, p.ConfirmedProd, p.ConfirmedDestructive) is { } block) return new QueryExecuteResult { Blocked = block };

        var history = actual ? $"-- actual plan (the statements ran, then were rolled back)\n{p.Sql}" : p.Sql;
        var exec = new Execution(Ulid.New(), session, history, new ExecuteOptions { NoHistory = !actual });
        _executions[exec.IdText] = exec;
        _ = Task.Run(async () =>
        {
            try { await RunToolCoreAsync(exec, session, ctx => PlanBodyAsync(ctx, plans, p), e => (null, e), endFrame: true).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug("Plan {Id} ended: {Message}", exec.IdText, ex.Message); }
        });
        return new QueryExecuteResult { ExecutionId = exec.IdText };
    }

    static string NoSavepoints(Session s) =>
        $"{s.Provider.Traits.Name} has no savepoints, so an actual plan inside your open transaction could only be undone by rolling back your own changes too. Commit or roll back first, or use the estimated plan.";

    /// <summary>The plan on the run's connection; the error text (also sent to Messages) or null.</summary>
    async Task<string?> PlanBodyAsync(ToolContext ctx, Contract.Providers.IPlanProvider plans, PlanParams p)
    {
        var db = ctx.Connection.Db;
        var tx = ctx.Session.Provider.Transactions;
        var actual = p.Mode == PlanMode.Actual;
        var savepoint = actual && ctx.Session.TxState != TxState.None;
        if (actual && ctx.Session.TxState == TxState.Unknown)
        {
            // Read it on the connection: a BEGIN inside an open transaction would end the user's, a savepoint outside one fails.
            try { savepoint = await tx.StateAsync(db, ctx.Ct).ConfigureAwait(false) != TxState.None; }
            catch (Exception ex) when (ex is not OperationCanceledException) { savepoint = true; }
        }
        string Fail(string text)
        {
            ctx.Message(Severity.Error, text);
            return text;
        }
        string Reason(Exception ex) => ctx.Session.Provider.TranslateException(ex).FirstOrDefault()?.Message ?? ex.Message;
        PlanResult result;
        // While true the plan's transaction or savepoint may be open: whatever ends the plan from here undoes it — a cancel
        // during BEGIN or SAVEPOINT included (8a review: it skipped the undo and left the run's connection in a transaction).
        var undo = false;
        try
        {
            if (actual)
            {
                // The transaction opened while the run waited in the queue: the same refusal as at the request.
                if (savepoint && !tx.Traits.Savepoints) return Fail(NoSavepoints(ctx.Session));
                try
                {
                    if (savepoint)
                    {
                        // Set only once it exists: a rollback to a savepoint that was never made fails (on PostgreSQL it
                        // would abort the user's transaction); one cut short and left behind holds nothing.
                        await tx.SavepointAsync(db, PlanSavepoint, ctx.Ct).ConfigureAwait(false);
                        undo = true;
                    }
                    else
                    {
                        undo = true; // a BEGIN cut short may still have opened one; the rollback is a no-op when it did not
                        await tx.BeginAsync(db, ctx.Ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!ctx.Ct.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    return Fail(savepoint && tx.Traits.FailedStatementAbortsTransaction
                        ? $"Your open transaction has failed (an earlier statement raised an error) and accepts no savepoint, so nothing ran. Roll it back, then ask for the actual plan again. ({Reason(ex)})"
                        : $"Querithm could not start the {(savepoint ? "savepoint" : "transaction")} the actual plan runs in, so nothing ran. ({Reason(ex)})");
                }
            }
            try
            {
                result = await plans.GetPlanAsync(db, p.Sql, p.Mode, ctx.Ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ctx.Ct.IsCancellationRequested && ex is not OperationCanceledException)
            {
                IReadOnlyList<Contract.Providers.ProviderMessage> messages = ex is NotSupportedException
                    ? [new Contract.Providers.ProviderMessage(Severity.Error, ex.Message)]
                    : ctx.Session.Provider.TranslateException(ex);
                foreach (var m in messages.Skip(1)) ctx.Message(Severity.Error, m.Message);
                return Fail(messages.FirstOrDefault()?.Message ?? ex.Message);
            }
        }
        finally
        {
            if (undo) await UndoPlanAsync(ctx, savepoint).ConfigureAwait(false);
        }
        FlushMessages(ctx.Execution);
        _notifier.Notify("query.plan", new QueryPlanEvent { ExecutionId = ctx.Execution.IdText, BatchIndex = 0, Plan = result.Plan, Raw = result.Raw });
        return null;
    }

    /// <summary>The query timeout by name when the driver's own text does not give a reason (D4 S07).</summary>
    internal static string TimeoutText(Session session, Exception ex)
    {
        var timeout = session.Provider.Traits.FixedCommandTimeoutS ?? session.Profile.CommandTimeoutS ?? 0;
        return ex is OperationCanceledException && timeout > 0
            ? $"The statement ran longer than the query timeout ({timeout} s) and was stopped. Raise the timeout in the connection's settings, or set it to 0 for none."
            : ex.Message;
    }

    /// <summary>
    /// Rolls an actual plan back (to its savepoint inside the user's transaction) within <see cref="Contract.Providers.Cleanup.Limit"/>.
    /// When that fails the connection is closed instead — the server then rolls back — so the plan's changes never stay
    /// in a pooled connection or in the user's transaction; the tab is told through the lost-connection path.
    /// </summary>
    async Task UndoPlanAsync(ToolContext ctx, bool savepoint)
    {
        var db = ctx.Connection.Db;
        var tx = ctx.Session.Provider.Transactions;
        using var limit = Contract.Providers.Cleanup.Start();
        try
        {
            if (savepoint)
            {
                await tx.RollbackToSavepointAsync(db, PlanSavepoint, limit.Token).ConfigureAwait(false);
                await tx.ReleaseSavepointAsync(db, PlanSavepoint, limit.Token).ConfigureAwait(false);
            }
            else await tx.RollbackAsync(db, limit.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) { _log.LogWarning("Execution {Id}: rolling back the actual plan failed: {Message}", ctx.Execution.IdText, ex.Message); }
        ctx.AbandonConnection("Querithm could not roll back the actual plan, so it closed the connection: the server rolls its changes back.");
    }

    async Task RunAsync(Execution exec, Classification classification)
    {
        var session = exec.Session!;
        var ct = exec.Cts.Token;
        var sw = Stopwatch.StartNew();
        var messages = new ConcurrentQueue<StatementStats>();
        long? affected = null;
        var truncatedAny = false;
        var hadError = false;
        ErrorInfo? firstError = null;
        var batchIndex = 0;
        var batches = classification.Batches;
        var lineOffset = (exec.Options.LineOffset ?? 1) - 1;
        var maxRows = exec.Options.MaxRows ?? DefaultMaxRows;
        PhysicalConnection? conn = null;
        var borrowed = false;
        var lockTaken = false;
        var txWasOpen = false;
        // M10: the run is Cancelled only when the cancel stopped work still to do — requested while a statement was still
        // running (even one the driver then returns normally) or before a later one. Once the last statement completed (or
        // the run stopped at an error), a cancel that lands in the remaining steps changes nothing the server did.
        var workDone = false;
        var cancelled = false;

        try
        {
            if (!session.ExecLock.Wait(0))
            {
                _notifier.Notify("query.queued", new QueryQueuedEvent { ExecutionId = exec.IdText, SessionId = session.Id });
                await session.ExecLock.WaitAsync(ct).ConfigureAwait(false);
            }
            lockTaken = true;
            session.ActiveExecutionId = exec.IdText;
            exec.State = ExecState.Running;
            _notifier.Notify("query.started", new QueryStartedEvent { ExecutionId = exec.IdText, SessionId = session.Id, BatchCount = batches.Length, StartedAt = Time.NowMs() });
            _notifier.Notify("session.stateChanged", session.State());
            _log.LogInformation("Execution {Id} started on session {Session} ({Batches} batch(es))", exec.IdText, session.Id, batches.Length);

            (conn, borrowed) = await _sessions.AcquireAsync(session, () =>
                _notifier.Notify("query.queued", new QueryQueuedEvent { ExecutionId = exec.IdText, SessionId = session.Id }), ct).ConfigureAwait(false);
            exec.Hold(conn);
            txWasOpen = session.TxState == TxState.Open;
            session.Spid = await session.Provider.GetSpidAsync(conn.Db, ct).ConfigureAwait(false);

            using var sub = session.Provider.SubscribeMessages(conn.Db, m =>
            {
                var b = batches.Length > 0 ? batches[Math.Min(batchIndex, batches.Length - 1)] : null;
                int? editorLine = m.Line is > 0 && b != null ? b.StartLine + m.Line.Value - 1 + lineOffset : null;
                var editorColumn = editorLine != null ? EditorColumn(exec.Sql, b, m.Line, m.Column) : null;
                if (m.Severity == Severity.Error)
                {
                    hadError = true;
                    firstError ??= new ErrorInfo { Message = m.Message, Number = m.Number, Line = m.Line, EditorLine = editorLine, EditorColumn = editorColumn, Procedure = m.Procedure, BatchIndex = batchIndex };
                }
                if (StatisticsParser.Parse(m.Message, batchIndex) is { } stat) messages.Enqueue(stat);
                Message(exec, new ServerMessage
                {
                    ExecutionId = exec.IdText, Severity = m.Severity, Message = m.Message, Line = m.Line, EditorLine = editorLine, EditorColumn = editorColumn,
                    Procedure = m.Procedure, Number = m.Number, State = m.State, Class = m.Class, BatchIndex = batchIndex, Ts = Time.NowMs(),
                });
            });

            var (before, after) = session.Provider.SessionOptionsSql(exec.Options);
            if (before != null) await ExecuteNonQueryAsync(conn.Db, before, ct).ConfigureAwait(false);
            // Phase D4: server output follows the session (the run option sets it), whichever connection this run borrowed.
            if (exec.Options.ServerOutput is { } so) session.ServerOutput = so;
            var toggles = classification.ServerOutput ?? [];
            await session.Provider.SetServerOutputAsync(conn.Db, session.ServerOutput, ct).ConfigureAwait(false);
            async Task ToggleAsync(int beforeBatch)
            {
                foreach (var t in toggles)
                    if (t.BeforeBatch == beforeBatch && t.On != session.ServerOutput)
                    {
                        session.ServerOutput = t.On;
                        await session.Provider.SetServerOutputAsync(conn.Db, t.On, ct).ConfigureAwait(false);
                    }
            }
            var stopped = false;
            try
            {
                for (batchIndex = 0; batchIndex < batches.Length; batchIndex++)
                {
                    await ToggleAsync(batchIndex).ConfigureAwait(false);
                    var batch = batches[batchIndex];
                    var text = exec.Sql.Substring(batch.StartOffset, batch.Length);
                    var reps = Math.Max(1, batch.RepeatCount);
                    for (var rep = 0; rep < reps; rep++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var (rows, trunc) = await RunBatchAsync(exec, session, conn.Db, text, batchIndex, maxRows, ct).ConfigureAwait(false);
                            truncatedAny |= trunc;
                            if (rows >= 0) affected = (affected ?? 0) + rows;
                            // A cancel requested before the driver came back stopped the statement even when the driver
                            // returns normally (MySQL's KILL QUERY makes SELECT SLEEP(n) return 1): Cancelled, not Done.
                            ct.ThrowIfCancellationRequested();
                            if (batchIndex == batches.Length - 1 && rep == reps - 1) workDone = true;
                            foreach (var m in await session.Provider.AfterBatchMessagesAsync(conn.Db, text, ct).ConfigureAwait(false))
                            {
                                int? editorLine = m.Line is > 0 ? batch.StartLine + m.Line.Value - 1 + lineOffset : null;
                                var editorColumn = editorLine != null ? EditorColumn(exec.Sql, batch, m.Line, m.Column) : null;
                                if (m.Severity == Severity.Error && m.FailsRun) // the batch's own failure only (D4 S13: not a stale SHOW WARNINGS row)
                                {
                                    hadError = true;
                                    firstError ??= new ErrorInfo { Message = m.Message, Number = m.Number, Line = m.Line, EditorLine = editorLine, EditorColumn = editorColumn, Procedure = m.Procedure, BatchIndex = batchIndex };
                                }
                                Message(exec, new ServerMessage
                                {
                                    ExecutionId = exec.IdText, Severity = m.Severity, Message = m.Message, Line = m.Line, EditorLine = editorLine, EditorColumn = editorColumn, Procedure = m.Procedure,
                                    Number = m.Number, BatchIndex = batchIndex, Ts = Time.NowMs(),
                                });
                            }
                        }
                        // No data plane reader, or a frame it broke off: every later batch would fail the same way (or run
                        // after results the user never saw complete), so the run ends here.
                        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException and not DataPlaneUnavailableException and not DataPlaneBrokenException)
                        {
                            hadError = true;
                            foreach (var m in session.Provider.TranslateException(ex, text))
                            {
                                int? editorLine = m.Line is > 0 ? batch.StartLine + m.Line.Value - 1 + lineOffset : null;
                                var editorColumn = editorLine != null ? EditorColumn(exec.Sql, batch, m.Line, m.Column) : null;
                                firstError ??= new ErrorInfo { Message = m.Message, Number = m.Number, Line = m.Line, EditorLine = editorLine, EditorColumn = editorColumn, Procedure = m.Procedure, BatchIndex = batchIndex };
                                Message(exec, new ServerMessage
                                {
                                    ExecutionId = exec.IdText, Severity = Severity.Error, Message = m.Message, Line = m.Line, EditorLine = editorLine, EditorColumn = editorColumn,
                                    Procedure = m.Procedure, Number = m.Number, State = m.State, Class = m.Class, BatchIndex = batchIndex, Ts = Time.NowMs(),
                                });
                            }
                            if (!conn.IsOpen) throw; // connection-level failure: stop
                            // Phase D4: what the failed batch printed before it raised (Oracle DBMS_OUTPUT), shown with its error.
                            try
                            {
                                foreach (var m in await session.Provider.FailedBatchMessagesAsync(conn.Db, ct).ConfigureAwait(false))
                                    Message(exec, new ServerMessage
                                    {
                                        ExecutionId = exec.IdText, Severity = m.Severity == Severity.Error ? Severity.Warning : m.Severity, Message = m.Message,
                                        Number = m.Number, BatchIndex = batchIndex, Ts = Time.NowMs(),
                                    });
                            }
                            catch (Exception readEx) when (readEx is not OperationCanceledException)
                            {
                                _log.LogWarning("Execution {Id}: reading the failed batch's messages failed: {Message}", exec.IdText, readEx.Message);
                            }
                            stopped = session.Provider.StopOnBatchError;
                            break; // like SSMS: an error ends this batch, the next batch still runs
                        }
                    }
                    if (stopped)
                    {
                        if (batchIndex < batches.Length - 1)
                            Message(exec, new ServerMessage
                            {
                                ExecutionId = exec.IdText, Severity = Severity.Info, BatchIndex = batchIndex, Ts = Time.NowMs(),
                                Message = $"Execution stopped at the first error; {batches.Length - batchIndex - 1} later statement(s) were not run.",
                            }, server: false);
                        break;
                    }
                }
                if (!stopped) await ToggleAsync(batches.Length).ConfigureAwait(false); // a trailing SET SERVEROUTPUT: for the next run
                workDone = true; // every batch ran, or the run stopped at an error
            }
            finally
            {
                if (after != null && conn.IsOpen)
                    try
                    {
                        using var cleanup = Contract.Providers.Cleanup.Start();
                        await ExecuteNonQueryAsync(conn.Db, after, cleanup.Token).ConfigureAwait(false);
                    }
                    catch { /* best effort */ }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || ct.IsCancellationRequested)
        {
            if (workDone) { /* the cancel came after the last statement completed: the run ended as it ran (M10) */ }
            else if (ct.IsCancellationRequested) cancelled = true;
            else
            {
                // Stopped by a timeout that is not the user's cancel: work was left undone, so the run failed. A driver that
                // ends its command timeout with a bare OperationCanceledException (ODP.NET: "A task was canceled.") says
                // nothing about why, so the query timeout is named.
                hadError = true;
                var text = TimeoutText(session, ex);
                firstError ??= new ErrorInfo { Message = text, BatchIndex = batchIndex };
                Message(exec, new ServerMessage { ExecutionId = exec.IdText, Severity = Severity.Error, Message = text, BatchIndex = batchIndex, Ts = Time.NowMs() }, server: false);
            }
        }
        catch (Exception ex)
        {
            hadError = true;
            IReadOnlyList<Contract.Providers.ProviderMessage> msgs = ex is DataPlaneUnavailableException or DataPlaneBrokenException ? [] : session.Provider.TranslateException(ex);
            var first = msgs.FirstOrDefault() ?? new Contract.Providers.ProviderMessage(Severity.Error, ex.Message);
            firstError ??= new ErrorInfo { Message = first.Message, Number = first.Number, Line = first.Line, Procedure = first.Procedure, BatchIndex = batchIndex };
            Message(exec, new ServerMessage { ExecutionId = exec.IdText, Severity = Severity.Error, Message = first.Message, Number = first.Number, BatchIndex = batchIndex, Ts = Time.NowMs() }, server: false);
        }
        finally
        {
            sw.Stop();
            var stateRead = false;
            exec.State = cancelled ? ExecState.Cancelled : hadError ? ExecState.Failed : ExecState.Done;
            if (conn != null)
            {
                // A connection the cancel escalation is closing gets no more commands: it is treated as lost below.
                if (!exec.Aborting)
                try
                {
                    // The run's token may have fired: these reads have the cleanup bound instead (and the escalation still
                    // holds the connection until LetGo below).
                    using var cleanup = Contract.Providers.Cleanup.Start();
                    session.Database = session.Provider.CurrentDatabase(conn.Db) ?? session.Database;
                    // The driver may have reconnected during the run (connection resiliency): record the new identity now,
                    // so the monitor never shows this tab's connection as a foreign session (O2) — and the tab's SPID, read
                    // before the command ran, i.e. before the reconnect. If this fails, kills stay refused (HasReconnected).
                    // It runs before the transaction-state refresh, which may hand the connection back to the pool.
                    try
                    {
                        if (conn.IsOpen) await _sessions.ReidentifyAsync(conn, cleanup.Token).ConfigureAwait(false);
                        if (conn.IsOpen) session.Spid = await session.Provider.GetSpidAsync(conn.Db, cleanup.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) { _log.LogWarning("Identifying the connection again after the run failed: {Message}", ex.Message); }
                    // Always, even when the identity read failed: a transaction the run opened must be pinned, never
                    // returned to the pool for the next borrower.
                    if (conn.IsOpen) await _sessions.RefreshTxStateAsync(session, conn, cleanup.Token).ConfigureAwait(false);
                    stateRead = conn.IsOpen;
                    // SQL Server with XACT_ABORT ON rolls the transaction back on a cancel or an error (B0 row 3). Querithm
                    // never does, but the user is told that the transaction they had open is gone.
                    if (txWasOpen && (cancelled || hadError) && conn.IsOpen && session.TxState == TxState.None)
                        Message(exec, new ServerMessage { ExecutionId = exec.IdText, Severity = Severity.Warning, Ts = Time.NowMs(),
                            Message = "The transaction that was open before this run has ended on the server: unless this run committed it, its changes were rolled back (a cancel or an error with XACT_ABORT ON does that)." }, server: false);
                }
                catch (Exception ex) { _log.LogWarning("Post-execution state check failed: {Message}", ex.Message); }
                // From here on the connection is the pool's or the session's pin — unless the escalation closed it.
                var aborted = exec.LetGo();
                if (aborted) await exec.WaitAbortCloseAsync().ConfigureAwait(false);
                if ((aborted || !conn.IsOpen) && session.Pinned == conn)
                {
                    // Lost the pinned connection: its transaction is gone (and an in-memory database with it); tell the user.
                    var lost = await _sessions.ConnectionLostAsync(session, conn).ConfigureAwait(false);
                    Message(exec, new ServerMessage { ExecutionId = exec.IdText, Severity = Severity.Warning, Message = lost, Ts = Time.NowMs() }, server: false);
                }
                else if (aborted) await conn.Pool.DiscardAsync(conn).ConfigureAwait(false);
                // Not pinned after a successful state read = no transaction: straight back to the pool; otherwise the pool
                // reads the state itself before reusing the connection (Phase D0b).
                else if (session.Pinned != conn) await _sessions.ReleaseAsync(session, conn, borrowed, stateRead ? TxState.None : null).ConfigureAwait(false);
            }
            await WriteFrameSafe(FrameKind.EndOfExecution, exec, 0, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);

            var stats = messages.ToArray();
            var timeStats = stats.Where(s => s.Kind == "time").ToArray();
            exec.Finished = true;
            exec.LastTouchedMs = Time.NowMs();
            if (lockTaken) session.ActiveExecutionId = null; // a queued run that never ran must not clear the running one's id
            exec.Batcher?.Complete();
            _notifier.Notify("query.done", new QueryDoneEvent
            {
                ExecutionId = exec.IdText, SessionId = session.Id, State = exec.State, AffectedRows = affected, ElapsedMs = sw.Elapsed.TotalMilliseconds,
                ServerElapsedMs = timeStats.Length > 0 ? timeStats.Sum(s => s.ElapsedMs ?? 0) : null,
                ServerCpuMs = timeStats.Length > 0 ? timeStats.Sum(s => s.CpuMs ?? 0) : null,
                Error = cancelled ? null : firstError, Truncated = truncatedAny, ResultSetCount = exec.NextResultIndex,
                Statistics = stats.Length > 0 ? stats : null, Database = session.Database, HadDdl = classification.HasDdl,
            });
            _notifier.Notify("session.stateChanged", session.State());
            if (lockTaken) session.ExecLock.Release();
            _log.LogInformation("Execution {Id} ended {State} after {Ms:0} ms", exec.IdText, exec.State, sw.Elapsed.TotalMilliseconds);

            if (!exec.Options.NoHistory)
                try
                {
                    AddHistory(session, exec.Sql, sw.Elapsed.TotalMilliseconds, affected, EnumText.Of(exec.State), firstError?.Message);
                }
                catch (Exception ex) { _log.LogWarning("History write failed: {Message}", ex.Message); }
            if (classification.HasDdl && !cancelled) DdlExecuted?.Invoke(session);
        }
    }

    async Task<(long Rows, bool Truncated)> RunBatchAsync(Execution exec, Session session, DbConnection db, string text, int batchIndex,
        int maxRows, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = text;
        cmd.CommandTimeout = session.Provider.Traits.FixedCommandTimeoutS ?? session.Profile.CommandTimeoutS ?? 0;
        if (exec.Options.Parameters is { Length: > 0 } ps)
            foreach (var prm in ps) session.Provider.AddParameter(cmd, prm);
        session.Provider.PrepareCommand(cmd);
        exec.CurrentCommand = cmd;
        var truncatedAny = false;
        var dataResult = 0;
        SchemaSnapshot? snapshot = null;
        try
        {
            await using var reg = ct.Register(() => { try { session.Provider.Cancel(cmd); } catch { /* best effort */ } });
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            exec.State = ExecState.Streaming;
            do
            {
                if (reader.FieldCount == 0) continue;
                if (session.Provider.IsPlanResultSet(reader))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var xml = reader.GetString(0);
                        try
                        {
                            var plan = session.Provider.Plans.Parse(xml);
                            FlushMessages(exec);
                            _notifier.Notify("query.plan", new QueryPlanEvent { ExecutionId = exec.IdText, BatchIndex = batchIndex, Plan = plan, Raw = xml });
                        }
                        catch (Exception ex) { _log.LogWarning("Plan parse failed: {Message}", ex.Message); }
                    }
                    continue;
                }
                // L13: the data plane numbers result sets with a u16; past the limit the batch still runs (and Cancel still
                // ends it), but its further result sets are skipped, said once.
                var resultIndex = exec.NextResultIndex;
                if (resultIndex >= MaxResultSets)
                {
                    if (!exec.ResultSetLimitNoted)
                    {
                        exec.ResultSetLimitNoted = true;
                        Message(exec, new ServerMessage
                        {
                            ExecutionId = exec.IdText, Severity = Severity.Warning, BatchIndex = batchIndex, Ts = Time.NowMs(),
                            Message = $"This run returned more than {MaxResultSets:N0} result sets; the later ones were not shown (their statements still ran).",
                        }, server: false);
                    }
                    continue;
                }
                // Key markers come from cached metadata only; a missing snapshot just means no markers.
                snapshot ??= SafeSnapshot(session);
                var inBatch = dataResult++;
                var snap = snapshot;
                // A statement shape whose origins cannot name the row shown (self-join, UNION): no origins, and the reason.
                string? refusal = null;
                try { refusal = session.Provider.ResultEditRefusal(text, inBatch, snap); }
                catch (Exception ex) { _log.LogDebug("Result shape check skipped: {Message}", ex.Message); refusal = "the query's shape could not be checked"; }
                Func<IReadOnlyList<DbColumn>, Contract.Providers.ColumnOrigin?[]?> origins = cols =>
                {
                    if (refusal != null) return null;
                    try { return session.Provider.ResultOrigins(db, reader, cols, text, inBatch, snap); }
                    catch (Exception ex) { _log.LogDebug("Result origins skipped: {Message}", ex.Message); return null; }
                };
                bool truncated;
                // The index is spent once the set started, even when it fails part-way: the UI may already hold its schema.
                try { truncated = await StreamResultSetAsync(exec, reader, session.Provider, resultIndex, batchIndex, maxRows, ct, origins, snap, refusal).ConfigureAwait(false); }
                finally { exec.NextResultIndex = resultIndex + 1; }
                truncatedAny |= truncated;
            } while (await reader.NextResultAsync(ct).ConfigureAwait(false));
            return (reader.RecordsAffected, truncatedAny);
        }
        finally
        {
            exec.CurrentCommand = null;
        }
    }

    SchemaSnapshot? SafeSnapshot(Session session)
    {
        // Phase D0d: the session's own view (an in-memory database, temporary tables) where it has one.
        try { return _schema is Metadata.SchemaCache scoped ? scoped.Get(session.Profile.Id, session.Database ?? session.Profile.DatabaseDefault, session.Id) : _schema?.Get(session.Profile.Id, session.Database ?? session.Profile.DatabaseDefault); }
        catch (Exception ex) { _log.LogDebug("No snapshot for key markers: {Message}", ex.Message); return null; }
    }

    async Task<bool> StreamResultSetAsync(Execution exec, DbDataReader reader, Contract.Providers.IDbProvider provider, int resultIndex, int batchIndex,
        int maxRows, CancellationToken ct, Func<IReadOnlyList<DbColumn>, Contract.Providers.ColumnOrigin?[]?>? origins = null, SchemaSnapshot? snapshot = null, string? editRefusal = null)
    {
        var enc = new ResultSetEncoder(reader, provider, exec.Cells, resultIndex, origins, snapshot, ct);
        var ri = checked((ushort)resultIndex); // below MaxResultSets (L13), never wrapped
        ResultEditInfo? edit = null;
        if (exec.Session is { } session && Editability is { } decide)
        {
            try { edit = decide(session, enc.Columns, enc.Origins, snapshot); }
            catch (Exception ex) { _log.LogDebug("Editability check skipped: {Message}", ex.Message); }
            // The statement shape outranks the analyzer (which sees no origins), unless the engine refuses grid edits outright.
            if (editRefusal != null && provider.FeatureRefusal(Contract.Providers.Feature.DataEdit) == null)
                edit = new ResultEditInfo { Editable = false, Reason = "Read-only: " + editRefusal };
        }
        exec.Results[resultIndex] = new RecordedResult(enc.Columns, enc.Origins, snapshot?.ProfileId, snapshot?.Database, edit);
        await WriteFrame(FrameKind.Schema, exec, ri, ResultSetEncoder.ToIpc(enc.Schema, null), ct).ConfigureAwait(false);
        FlushMessages(exec);
        _notifier.Notify("query.resultSet", new QueryResultSetEvent { ExecutionId = exec.IdText, ResultIndex = resultIndex, Schema = enc.Columns, BatchIndex = batchIndex, Edit = edit });
        long row = 0;
        var truncated = false;
        try
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (maxRows > 0 && row >= maxRows) { truncated = true; break; }
                enc.AppendRow(reader, row++);
                if (enc.ShouldFlush)
                {
                    using var batch = enc.Flush();
                    await WriteFrame(FrameKind.Batch, exec, ri, ResultSetEncoder.ToIpc(enc.Schema, batch), ct).ConfigureAwait(false);
                }
            }
            if (enc.PendingRows > 0)
            {
                using var batch = enc.Flush();
                await WriteFrame(FrameKind.Batch, exec, ri, ResultSetEncoder.ToIpc(enc.Schema, batch), ct).ConfigureAwait(false);
            }
            await WriteFrame(FrameKind.EndOfResultSet, exec, ri, new[] { truncated ? (byte)1 : (byte)0 }, ct).ConfigureAwait(false);
            if (truncated)
            {
                // Row limit hit (§7): stop reading. Draining the rest is left to the driver on NextResult;
                // if that takes long the user can cancel, which keeps the rows already shown.
                Message(exec, new ServerMessage
                {
                    ExecutionId = exec.IdText, Severity = Severity.Info, BatchIndex = batchIndex, Ts = Time.NowMs(),
                    Message = $"Result set {resultIndex + 1}: row limit of {maxRows:N0} reached; remaining rows were not fetched.",
                });
            }
            return truncated;
        }
        catch
        {
            exec.Results.TryRemove(resultIndex, out _);
            throw;
        }
    }

    async ValueTask WriteFrame(byte kind, Execution exec, ushort resultIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _frames.WriteFrameAsync(kind, exec.Id, resultIndex, payload, ct).ConfigureAwait(false);
        exec.LastProgressMs = Time.NowMs();
    }

    async ValueTask WriteFrameSafe(byte kind, Execution exec, ushort resultIndex, ReadOnlyMemory<byte> payload)
    {
        // A cancelled run's end frame waits the cancel grace, not the drain limit: query.done stays within 10 s of Cancel
        // whatever the data plane does (§14 #15b).
        using var limit = new CancellationTokenSource(exec.Cts.IsCancellationRequested && CancelGrace < FrameDrainLimit ? CancelGrace : FrameDrainLimit);
        try { await _frames.WriteFrameAsync(kind, exec.Id, resultIndex, payload, limit.Token).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning("Frame write failed: {Message}", ex.Message); }
    }

    /// <summary>
    /// M13: every message of a run goes through its batcher. <paramref name="server"/>: a line that scales with the work
    /// (the server's messages, row-limit notes, a tool's lines) counts toward the run's cap; Querithm's own few never do.
    /// </summary>
    void Message(Execution exec, ServerMessage message, bool server = true) => BatcherOf(exec).Add(message, server);

    MessageBatcher BatcherOf(Execution exec) =>
        LazyInitializer.EnsureInitialized(ref exec.Batcher, () => new MessageBatcher(_notifier, exec.IdText));

    /// <summary>The run's messages so far go out first, so the webview sees them before the result set, plan or end.</summary>
    static void FlushMessages(Execution exec) => exec.Batcher?.Flush();

    /// <summary>
    /// Phase D4: a message's column in the editor. The server counts columns in the batch's text, so only the batch's first
    /// line shifts — by where the batch starts on its line in the run's text (a statement after another on the same line).
    /// A selection run that starts mid-line is not shifted by the selection's own column (the run's text starts there).
    /// </summary>
    internal static int? EditorColumn(string sql, BatchInfo? batch, int? line, int? column)
    {
        if (column is not > 0 || line is not > 0) return null;
        if (line != 1 || batch == null) return column;
        var start = Math.Clamp(batch.StartOffset, 0, sql.Length);
        var lineStart = start;
        while (lineStart > 0 && sql[lineStart - 1] != '\n') lineStart--;
        return start - lineStart + column;
    }

    static async Task ExecuteNonQueryAsync(DbConnection db, string sql, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public CancelResult Cancel(string executionId)
    {
        if (!_executions.TryGetValue(executionId, out var exec) || exec.Finished) return new CancelResult { Cancelled = false };
        RequestCancel(exec);
        return new CancelResult { Cancelled = true };
    }

    /// <summary>The one cancel path (Cancel, closing the tab, the idle watchdog): token, driver cancel, then the escalation.</summary>
    void RequestCancel(Execution exec)
    {
        exec.Cts.Cancel();
        if (exec.CurrentCommand is { } cmd && exec.Session is { } s)
            try { s.Provider.Cancel(cmd); } catch { /* the token registration also cancels */ }
        if (Interlocked.Exchange(ref exec.CancelRequested, 1) == 1) return;
        _log.LogInformation("Execution {Id}: cancel requested ({State})", exec.IdText, exec.State);
        _ = EscalateCancelAsync(exec);
    }

    /// <summary>
    /// How long after the second grace a run still going is reported as stuck for good (the cleanup a cancelled run may still
    /// do — a rollback, restoring session options, the state read — has <see cref="Contract.Providers.Cleanup.Limit"/>, and
    /// PostgreSQL / MySQL drivers break a connection whose cancel is not answered within that too). Settable for tests.
    /// </summary>
    internal TimeSpan StuckLimit { get; set; } = Contract.Providers.Cleanup.Limit;

    /// <summary>
    /// A cancelled run that has not stopped after <see cref="CancelGrace"/> has its connection closed: the driver call
    /// parked on it fails, and the run ends through the lost-connection path, which tells the user an open transaction
    /// is gone. A run still going after a second grace is told as it is (8a review: "Restart the engine" there was wrong for
    /// a run that ends by itself): one finishing its cleanup frees the tab within <see cref="Contract.Providers.Cleanup.Limit"/>,
    /// one still in its driver call may end when the driver gives up on the cancel; only a run still going after
    /// <see cref="StuckLimit"/> more is reported as stuck, with the way out (restart the engine).
    /// </summary>
    async Task EscalateCancelAsync(Execution exec)
    {
        try
        {
            await Task.Delay(CancelGrace).ConfigureAwait(false);
            if (exec.Finished) return;
            if (CloseConnectionOf(exec))
            {
                _log.LogWarning("Execution {Id} did not stop {Seconds:0} s after cancel ({State}); its connection was closed", exec.IdText, CancelGrace.TotalSeconds, exec.State);
                Message(exec, new ServerMessage
                {
                    ExecutionId = exec.IdText, Severity = Severity.Warning, Ts = Time.NowMs(),
                    Message = $"The query did not stop within {CancelGrace.TotalSeconds:0} s of Cancel, so Querithm closed this tab's connection to end it.",
                }, server: false);
            }
            await Task.Delay(CancelGrace).ConfigureAwait(false);
            if (exec.Finished) return;
            var since = 2 * CancelGrace.TotalSeconds;
            // The final state is set before the run's cleanup starts: a run past that is finishing up, not stuck.
            var cleaning = exec.State is ExecState.Cancelled or ExecState.Failed or ExecState.Done;
            _log.LogWarning("Execution {Id} is still running {Seconds:0} s after cancel ({State})", exec.IdText, since, exec.State);
            Message(exec, new ServerMessage
            {
                ExecutionId = exec.IdText, Severity = cleaning ? Severity.Info : Severity.Warning, Ts = Time.NowMs(),
                Message = cleaning
                    ? $"Cancel stopped the query; Querithm is still finishing up on this tab's connection (a rollback or the transaction-state read, at most {Contract.Providers.Cleanup.Limit.TotalSeconds:0} s). The tab frees itself when that ends."
                    : $"The query has not stopped {since:0} s after Cancel. It may still end by itself within {StuckLimit.TotalSeconds:0} s (the driver gives up on a cancel the server does not answer); if this tab is still busy then, restart the engine to free it.",
            }, server: false);
            await Task.Delay(StuckLimit).ConfigureAwait(false);
            if (exec.Finished) return;
            _log.LogError("Execution {Id} is still running {Seconds:0} s after cancel ({State})", exec.IdText, since + StuckLimit.TotalSeconds, exec.State);
            Message(exec, new ServerMessage
            {
                ExecutionId = exec.IdText, Severity = Severity.Error, Ts = Time.NowMs(),
                Message = $"This run has still not stopped {since + StuckLimit.TotalSeconds:0} s after Cancel. Restart the engine to free the tab (every tab reconnects; open transactions are rolled back).",
            }, server: false);
        }
        catch (Exception ex) { _log.LogWarning("Execution {Id}: cancel escalation failed: {Message}", exec.IdText, ex.Message); }
    }

    /// <summary>
    /// Step one of the escalation: closes the connection the run still holds, so the driver call parked on it fails.
    /// Never on an engine where that is unsafe (<see cref="Contract.Providers.IDbProvider.CloseAbortsCommand"/>).
    /// </summary>
    internal bool CloseConnectionOf(Execution exec)
    {
        if (exec.Session?.Provider.CloseAbortsCommand != true) return false;
        // Claimed: the run discards this connection instead of passing it to another borrower, so closing it here can
        // only end this run.
        return exec.TryStartAbort(c => Task.Run(() =>
        {
            try { c.Db.Close(); }
            catch (Exception ex) { _log.LogWarning("Execution {Id}: closing its connection failed: {Message}", exec.IdText, ex.Message); }
        }));
    }

    /// <summary>Test seam for the real drivers (a real server always acknowledges a cancel, so the escalation never fires there).</summary>
    internal bool CloseConnectionOf(string executionId) => _executions.TryGetValue(executionId, out var exec) && CloseConnectionOf(exec);

    /// <summary>The recorded result set of an execution on <paramref name="sessionId"/>; null when closed or not that session's.</summary>
    public RecordedResult? Recorded(string sessionId, string executionId, int resultIndex)
    {
        if (!_executions.TryGetValue(executionId, out var exec) || exec.Session?.Id != sessionId) return null;
        exec.LastTouchedMs = Time.NowMs();
        return exec.Results.GetValueOrDefault(resultIndex);
    }

    public CellResult Cell(CellParams p)
    {
        if (!_executions.TryGetValue(p.ExecutionId, out var exec)) return new CellResult { Unavailable = true };
        exec.LastTouchedMs = Time.NowMs();
        return exec.Cells.Get(p.ResultIndex, p.Row, p.Col) switch
        {
            string s => new CellResult { Value = s },
            byte[] b => new CellResult { Value = Convert.ToBase64String(b), IsBinary = true },
            _ => new CellResult { Unavailable = true },
        };
    }

    public void Close(string executionId)
    {
        if (_executions.TryRemove(executionId, out var exec))
        {
            if (!exec.Finished) RequestCancel(exec);
            exec.Cells.Clear();
        }
    }

    public void CloseForSession(string sessionId)
    {
        foreach (var e in _executions.Values.Where(e => e.Session?.Id == sessionId).ToList()) Close(e.IdText);
    }

    /// <summary>§13 rule 9: executions idle for 10 minutes are closed and the tab is told.</summary>
    void Watchdog()
    {
        var now = Time.NowMs();
        foreach (var exec in _executions.Values)
        {
            // Idle results give back their cell buffer; the small record stays so the grid can still be edited.
            if (exec.Finished && now - exec.LastTouchedMs > ForgetLimit.TotalMilliseconds) Close(exec.IdText);
            else if (exec.Finished && now - exec.LastTouchedMs > IdleLimit.TotalMilliseconds) exec.Cells.Clear();
            else if (!exec.Finished && exec.State == ExecState.Streaming && now - exec.LastProgressMs > IdleLimit.TotalMilliseconds)
            {
                Message(exec, new ServerMessage
                {
                    ExecutionId = exec.IdText, Severity = Severity.Warning, Ts = now,
                    Message = "No rows were consumed for 10 minutes; the execution was cancelled to free resources.",
                }, server: false);
                RequestCancel(exec);
            }
        }
    }

    /// <summary>
    /// Runs a Querithm-generated write (grid patch, import, sync) as one execution on the session: the same queueing,
    /// pinned-connection reuse, transaction-state refresh, notifications and history as query.execute.
    /// The caller checks <see cref="ToolGuard"/> first. <paramref name="outcome"/> turns the body's result into the
    /// execution's affected rows and error.
    /// </summary>
    public async Task<(T Result, string ExecutionId)> RunToolAsync<T>(Session session, string historySql, Func<ToolContext, Task<T>> body,
        Func<T, (long? Affected, string? Error)> outcome, Action<string>? started = null)
    {
        var exec = new Execution(Ulid.New(), session, historySql, new ExecuteOptions());
        _executions[exec.IdText] = exec;
        started?.Invoke(exec.IdText);
        return (await RunToolCoreAsync(exec, session, body, outcome).ConfigureAwait(false), exec.IdText);
    }

    /// <summary>
    /// The tool execution's lifecycle for an <paramref name="exec"/> already registered. <paramref name="endFrame"/>: the
    /// webview reads the run like a query's (a plan), so the data plane gets its end-of-execution frame.
    /// </summary>
    async Task<T> RunToolCoreAsync<T>(Execution exec, Session session, Func<ToolContext, Task<T>> body,
        Func<T, (long? Affected, string? Error)> outcome, bool endFrame = false)
    {
        var historySql = exec.Sql;
        var ct = exec.Cts.Token;
        var sw = Stopwatch.StartNew();
        PhysicalConnection? conn = null;
        var borrowed = false;
        var lockTaken = false;
        string? error = null;
        long? affected = null;
        try
        {
            if (!session.ExecLock.Wait(0))
            {
                _notifier.Notify("query.queued", new QueryQueuedEvent { ExecutionId = exec.IdText, SessionId = session.Id });
                await session.ExecLock.WaitAsync(ct).ConfigureAwait(false);
            }
            lockTaken = true;
            session.ActiveExecutionId = exec.IdText;
            exec.State = ExecState.Running;
            _notifier.Notify("query.started", new QueryStartedEvent { ExecutionId = exec.IdText, SessionId = session.Id, BatchCount = 1, StartedAt = Time.NowMs() });
            _notifier.Notify("session.stateChanged", session.State());
            (conn, borrowed) = await _sessions.AcquireAsync(session, null, ct).ConfigureAwait(false);
            exec.Hold(conn);
            var ctx = new ToolContext(exec, session, conn, (sev, text) => Message(exec,
                new ServerMessage { ExecutionId = exec.IdText, Severity = sev, Message = text, Ts = Time.NowMs() }), _log);
            var result = await body(ctx).ConfigureAwait(false);
            (affected, error) = outcome(result);
            exec.State = error == null ? ExecState.Done : ExecState.Failed;
            return result;
        }
        catch (Exception ex)
        {
            exec.State = ct.IsCancellationRequested ? ExecState.Cancelled : ExecState.Failed;
            error = session.Provider.TranslateException(ex).FirstOrDefault()?.Message ?? ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            var toolStateRead = false;
            if (conn != null)
            {
                if (!exec.Aborting)
                try
                {
                    // As after a query run: a reconnect inside the tool is identified again at once (O2), SPID included,
                    // and the transaction state is refreshed even when that fails — within the cleanup bound.
                    using var cleanup = Contract.Providers.Cleanup.Start();
                    try
                    {
                        if (conn.IsOpen) await _sessions.ReidentifyAsync(conn, cleanup.Token).ConfigureAwait(false);
                        if (conn.IsOpen) session.Spid = await session.Provider.GetSpidAsync(conn.Db, cleanup.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) { _log.LogWarning("Identifying the connection again after the write failed: {Message}", ex.Message); }
                    if (conn.IsOpen) await _sessions.RefreshTxStateAsync(session, conn, cleanup.Token).ConfigureAwait(false);
                    toolStateRead = conn.IsOpen;
                }
                catch (Exception ex) { _log.LogWarning("Post-write state check failed: {Message}", ex.Message); }
                var aborted = exec.LetGo();
                if (aborted) await exec.WaitAbortCloseAsync().ConfigureAwait(false);
                if ((aborted || !conn.IsOpen) && session.Pinned == conn)
                {
                    var lost = await _sessions.ConnectionLostAsync(session, conn).ConfigureAwait(false);
                    Message(exec, new ServerMessage { ExecutionId = exec.IdText, Severity = Severity.Warning, Message = lost, Ts = Time.NowMs() }, server: false);
                }
                else if (aborted) await conn.Pool.DiscardAsync(conn).ConfigureAwait(false);
                else if (session.Pinned != conn) await _sessions.ReleaseAsync(session, conn, borrowed, toolStateRead ? TxState.None : null).ConfigureAwait(false);
            }
            if (endFrame) await WriteFrameSafe(FrameKind.EndOfExecution, exec, 0, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            exec.Finished = true;
            exec.LastTouchedMs = Time.NowMs();
            if (lockTaken) session.ActiveExecutionId = null;
            exec.Batcher?.Complete();
            _notifier.Notify("query.done", new QueryDoneEvent
            {
                ExecutionId = exec.IdText, SessionId = session.Id, State = exec.State, AffectedRows = affected, ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Error = error == null ? null : new ErrorInfo { Message = error }, Database = session.Database,
            });
            _notifier.Notify("session.stateChanged", session.State());
            if (lockTaken) session.ExecLock.Release();
            if (!exec.Options.NoHistory)
                try
                {
                    AddHistory(session, historySql, sw.Elapsed.TotalMilliseconds, affected, EnumText.Of(exec.State), error);
                }
                catch (Exception ex) { _log.LogWarning("History write failed: {Message}", ex.Message); }
        }
    }

    /// <summary>
    /// L4: history keeps the statement with its secret literals masked on the engine's tokens (a password typed in ALTER
    /// LOGIN, IDENTIFIED BY, CREATE SECRET …), and the error without those literals (a syntax error may quote them).
    /// </summary>
    void AddHistory(Session session, string sql, double elapsedMs, long? rows, string status, string? error)
    {
        var redacted = session.Provider.Language.RedactSecrets(sql);
        _history.Add(session.Profile.Id, session.Database, redacted.Text, elapsedMs, rows, status, redacted.Mask(error));
    }

    /// <summary>
    /// Rows that are not a query result (an import preview) as one text result set on the data plane, so they cross
    /// IPC as Arrow frames like every other row (§15 rule 4). The webview reads them with the execution id returned.
    /// </summary>
    public async Task<string> StaticResultAsync(IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows)
    {
        var exec = new Execution(Ulid.New(), null, "-- preview", new ExecuteOptions()) { Finished = true, State = ExecState.Done };
        _executions[exec.IdText] = exec;
        var fields = columns.Select((c, i) => new Apache.Arrow.Field(c, Apache.Arrow.Types.StringType.Default, true,
            new Dictionary<string, string> { ["q:typeName"] = "text", ["q:ordinal"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture), ["q:nullable"] = "1" })).ToList();
        var schema = new Apache.Arrow.Schema(fields, null);
        var arrays = new List<Apache.Arrow.IArrowArray>();
        for (var c = 0; c < columns.Count; c++)
        {
            var b = new Apache.Arrow.StringArray.Builder();
            foreach (var r in rows) { if (c < r.Length && r[c] is { } v) b.Append(v); else b.AppendNull(); }
            arrays.Add(b.Build());
        }
        using var batch = new Apache.Arrow.RecordBatch(schema, arrays, rows.Count);
        await WriteFrameSafe(FrameKind.Schema, exec, 0, ResultSetEncoder.ToIpc(schema, null)).ConfigureAwait(false);
        await WriteFrameSafe(FrameKind.Batch, exec, 0, ResultSetEncoder.ToIpc(schema, batch)).ConfigureAwait(false);
        await WriteFrameSafe(FrameKind.EndOfResultSet, exec, 0, new byte[] { 0 }).ConfigureAwait(false);
        await WriteFrameSafe(FrameKind.EndOfExecution, exec, 0, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        return exec.IdText;
    }

    /// <summary>A tool's own result stream (data compare diffs): frames under an execution closed by query.close.</summary>
    public Execution OpenStream(string label)
    {
        var exec = new Execution(Ulid.New(), null, label, new ExecuteOptions()) { State = ExecState.Running };
        _executions[exec.IdText] = exec;
        return exec;
    }

    public ValueTask StreamFrameAsync(Execution exec, byte kind, ReadOnlyMemory<byte> payload, CancellationToken ct) => WriteFrame(kind, exec, 0, payload, ct);

    /// <summary>Ends the stream's one result set and the execution; never throws (the webview may be gone).</summary>
    public async ValueTask EndStreamAsync(Execution exec, bool truncated, ExecState state)
    {
        await WriteFrameSafe(FrameKind.EndOfResultSet, exec, 0, new[] { truncated ? (byte)1 : (byte)0 }).ConfigureAwait(false);
        await WriteFrameSafe(FrameKind.EndOfExecution, exec, 0, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        exec.State = state;
        exec.Finished = true;
    }

    /// <summary>Milestone-1 synthetic result: hard-coded rows straight to the data plane (bench metrics 11–13).</summary>
    public QueryExecuteResult Synthetic(SyntheticParams p)
    {
        var exec = new Execution(Ulid.New(), null, "-- synthetic", new ExecuteOptions());
        _executions[exec.IdText] = exec;
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await foreach (var (kind, payload) in SyntheticResult.Generate(p.Rows ?? 1_000_000, p.Columns ?? 40, exec.Cts.Token))
                    await WriteFrame(kind, exec, 0, payload, exec.Cts.Token).ConfigureAwait(false);
                exec.State = ExecState.Done;
            }
            catch (OperationCanceledException) { exec.State = ExecState.Cancelled; }
            catch (Exception ex) { exec.State = ExecState.Failed; _log.LogError(ex, "Synthetic stream failed"); }
            finally
            {
                await WriteFrameSafe(FrameKind.EndOfExecution, exec, 0, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                exec.Finished = true;
                _notifier.Notify("query.done", new QueryDoneEvent
                {
                    ExecutionId = exec.IdText, SessionId = "", State = exec.State, ElapsedMs = sw.Elapsed.TotalMilliseconds, ResultSetCount = 1,
                    AffectedRows = p.Rows ?? 1_000_000,
                });
            }
        });
        return new QueryExecuteResult { ExecutionId = exec.IdText };
    }

    public async ValueTask DisposeAsync()
    {
        _sessions.SessionClosing -= OnSessionClosing;
        await _watchdog.DisposeAsync().ConfigureAwait(false);
        foreach (var e in _executions.Values) e.Cts.Cancel();
    }
}
