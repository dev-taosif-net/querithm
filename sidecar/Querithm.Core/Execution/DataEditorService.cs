using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;

namespace Querithm.Core.Execution;

/// <summary>
/// Grid editing: decides editability when a result set starts, reads row-version tokens for edited rows,
/// previews and applies staged changes. Apply runs through <see cref="ExecutionService.RunToolAsync{T}"/> so queueing,
/// the pinned transaction, guards, notifications and history are the ones query.execute uses.
/// </summary>
public sealed class DataEditorService(SessionManager sessions, ExecutionService executions, MetadataStore store, SchemaCache cache, ILogger log)
{
    const string Savepoint = "querithm_grid";
    const int MaxParamsPerStatement = 1000;
    const int MaxConflictReads = 50;

    /// <summary>The editability hook ExecutionService calls for every data result set.</summary>
    public ResultEditInfo? Decide(Session session, ResultColumn[] columns, ColumnOrigin?[]? origins, SchemaSnapshot? snapshot) =>
        session.Provider.FeatureRefusal(Feature.DataEdit) is { } refused
            ? new ResultEditInfo { Editable = false, Reason = refused }
            : EditabilityAnalyzer.Analyze(session.Provider.DataEdit, columns, origins, snapshot, id => Indexes(snapshot!, id));

    IndexInfo[] Indexes(SchemaSnapshot snapshot, long objectId)
    {
        try
        {
            // Phase D0d: an object of the session's own view (in-memory database, temporary table) keeps its details there.
            if (snapshot.SessionId is { } sid)
            {
                var own = SchemaCache.SessionDatabase(snapshot.Database, sid);
                if (cache.Ephemeral.GetMarkers(snapshot.ProfileId, own).ContainsKey(objectId)) return cache.Ephemeral.Details(snapshot.ProfileId, own, objectId).Indexes;
            }
            return store.Details(snapshot.ProfileId, snapshot.Database, objectId).Indexes;
        }
        catch (Exception ex)
        {
            log.LogDebug("No index metadata for {Id}: {Message}", objectId, ex.Message);
            return [];
        }
    }

    sealed record Target(Session Session, ResultEditInfo Edit);

    /// <summary>
    /// The recorded result, re-checked against the current metadata: a DDL change or a database switch since the
    /// query ran makes the old edit decision unsafe.
    /// </summary>
    Target Resolve(string sessionId, string executionId, int resultIndex)
    {
        var session = sessions.Get(sessionId);
        FeatureGuard.Require(session.Provider, Feature.DataEdit);
        var rec = executions.Recorded(sessionId, executionId, resultIndex)
                  ?? throw new QuerithmException("resultClosed", "This result is no longer available for editing. Re-run the query.");
        if (rec.Edit is not { Editable: true } recorded) throw QuerithmException.Invalid(rec.Edit?.Reason ?? "This result set is read-only.");
        if (!string.Equals(session.Database, rec.Database, StringComparison.Ordinal))
            throw new QuerithmException("databaseChanged", $"The tab switched to database {session.Database} after this result was read from {rec.Database}. Re-run the query.");
        var current = EditabilityAnalyzer.Analyze(session.Provider.DataEdit, rec.Columns, rec.Origins, cache.Get(rec.ProfileId, rec.Database, session.Id),
            id => Indexes(cache.Get(rec.ProfileId, rec.Database, session.Id)!, id));
        if (!current.Editable) throw QuerithmException.Invalid(current.Reason ?? "This result set is read-only.");
        if (current.ObjectId != recorded.ObjectId || !current.KeyColumns.SequenceEqual(recorded.KeyColumns, StringComparer.Ordinal))
            throw new QuerithmException("schemaChanged", $"The table {recorded.Schema}.{recorded.Table} changed since the query ran. Re-run the query.");
        return new Target(session, current);
    }

    static string TableName(IDataEditDialect d, ResultEditInfo e) => $"{d.Quote(e.Schema!)}.{d.Quote(e.Table!)}";

    // ---------------------------------------------------------------------------------------------- tokens

    public async Task<RowTokensResult> RowTokensAsync(RowTokensParams p, CancellationToken ct)
    {
        var (session, edit) = Resolve(p.SessionId, p.ExecutionId, p.ResultIndex);
        var d = session.Provider.DataEdit;
        var byName = edit.Columns.Where(c => c.Column != null).GroupBy(c => c.Column!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var keyCols = edit.KeyColumns.Select(k => byName[k]).ToArray();
        var tokens = new string?[p.Keys.Length];
        // Rows are grouped by which displayed values they must still match, so each group is one statement shape.
        var groups = Enumerable.Range(0, p.Keys.Length).GroupBy(i => ExpectColumns(p.Expect, i, byName));

        await session.ExecLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (conn, borrowed) = await sessions.AcquireAsync(session, null, ct).ConfigureAwait(false);
            try
            {
                // On the pinned connection the reads run inside a savepoint where a failed or cancelled one would abort the
                // user's transaction (PostgreSQL), and a stopped read re-reads the state.
                await sessions.PinnedReadAsync(session, conn, async () =>
                {
                    foreach (var group in groups)
                    {
                        var expect = group.Key.Length == 0 ? [] : group.Key.Split('\u0001');
                        var perRow = keyCols.Length + expect.Length;
                        // Bounded by the parameter budget and by the rows one derived table may hold (SQLite: 500 compound terms).
                        foreach (var chunk in group.Chunk(Math.Max(1, Math.Min(d.MaxRelationRows, MaxParamsPerStatement / Math.Max(1, perRow)))))
                            await ReadTokensAsync(d, conn.Db, session, edit, keyCols, expect.Select(n => byName[n]).ToArray(), p, chunk, tokens, ct).ConfigureAwait(false);
                    }
                    return true;
                }, ct).ConfigureAwait(false);
            }
            finally { await sessions.ReleaseAsync(session, conn, borrowed).ConfigureAwait(false); }
        }
        finally { session.ExecLock.Release(); }
        return new RowTokensResult { Tokens = tokens };
    }

    static string ExpectColumns(Dictionary<string, GridValue>[]? expect, int i, Dictionary<string, EditColumn> byName) =>
        expect == null || i >= expect.Length || expect[i] == null
            ? ""
            : string.Join('\u0001', expect[i].Keys.Where(k => byName.TryGetValue(k, out var c) && c.Comparable).OrderBy(k => k, StringComparer.Ordinal));

    async Task ReadTokensAsync(IDataEditDialect d, DbConnection db, Session session, ResultEditInfo edit, EditColumn[] keyCols, EditColumn[] expectCols,
        RowTokensParams p, int[] rows, string?[] tokens, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandTimeout = session.Provider.Traits.FixedCommandTimeoutS ?? session.Profile.CommandTimeoutS ?? 0;
        var param = 0;
        var tuples = new List<IReadOnlyList<string>>();
        foreach (var i in rows)
        {
            var key = p.Keys[i];
            if (key.Length != keyCols.Length) throw QuerithmException.Invalid($"A row key needs {keyCols.Length} value(s), got {key.Length}.");
            var parts = new List<string> { i.ToString(CultureInfo.InvariantCulture) };
            for (var k = 0; k < keyCols.Length; k++)
            {
                d.AddValue(cmd, param, keyCols[k].Type, key[k]);
                parts.Add(d.TypedPlaceholder(param++, keyCols[k].Type));
            }
            foreach (var e in expectCols)
            {
                d.AddValue(cmd, param, e.Type, p.Expect![i][e.Column!]);
                parts.Add(d.TypedPlaceholder(param++, e.Type));
            }
            tuples.Add(parts);
        }
        var names = new List<string> { "qi" };
        names.AddRange(keyCols.Select((_, k) => $"k{k}"));
        names.AddRange(expectCols.Select((_, k) => $"e{k}"));
        var on = keyCols.Select((c, k) => $"t.{d.Quote(c.Column!)} = v.k{k}")
            .Concat(expectCols.Select((c, k) => d.NullSafeEquals($"t.{d.Quote(c.Column!)}", $"v.e{k}")));
        var token = edit.TokenKind != null ? d.TokenText("t", edit.TokenColumn!) : "''";
        cmd.CommandText = $"SELECT v.qi, {token} FROM {TableName(d, edit)} t JOIN {d.ValuesRelation(tuples, names, "v")} ON {string.Join(" AND ", on)}";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var i = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
            if (i >= 0 && i < tokens.Length) tokens[i] = r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture) ?? "";
        }
    }

    // ---------------------------------------------------------------------------------------------- preview / apply

    public GridPreviewResult Preview(GridPatchParams p)
    {
        var (session, edit) = Resolve(p.SessionId, p.ExecutionId, p.ResultIndex);
        var d = session.Provider.DataEdit;
        var statements = PatchBuilder.Build(d, edit, p, p.ForceOverwriteConflicts);
        var notes = Notes(edit, p);
        var header = new StringBuilder($"-- Changes to {CommentText.Safe($"{edit.Schema}.{edit.Table} ({session.Profile.Name}{(session.Database != null ? " · " + session.Database : "")})")}.\n");
        header.Append("-- Preview only: the values are sent as parameters, never inlined, and everything runs in one transaction.");
        foreach (var n in notes) header.Append("\n-- ").Append(CommentText.Safe(n));
        return new GridPreviewResult { Sql = PatchBuilder.Preview(d, statements, header.ToString()), StatementCount = statements.Count, Notes = notes };
    }

    static string[] Notes(ResultEditInfo edit, GridPatchParams p)
    {
        var notes = new List<string>();
        if (p.ForceOverwriteConflicts) notes.Add("Overwrite: rows are matched on their key only; changes made by others since the query ran are replaced.");
        else if (edit.ConcurrencyNote != null) notes.Add(edit.ConcurrencyNote);
        if (edit.TokenKind == null && !p.ForceOverwriteConflicts)
        {
            var unchecked_ = p.Updates.SelectMany(u => u.Changes.Keys).Distinct()
                .Where(n => edit.Columns.FirstOrDefault(c => c.Column == n) is { Comparable: false }).ToArray();
            if (unchecked_.Length > 0) notes.Add($"Not checked for concurrent changes: {string.Join(", ", unchecked_)}.");
        }
        return notes.ToArray();
    }

    public async Task<GridApplyResult> ApplyAsync(GridPatchParams p)
    {
        var (session, edit) = Resolve(p.SessionId, p.ExecutionId, p.ResultIndex);
        var d = session.Provider.DataEdit;
        var statements = PatchBuilder.Build(d, edit, p, p.ForceOverwriteConflicts);
        if (statements.Count == 0) return new GridApplyResult { Ok = true };

        if (session.TxState != TxState.None && !session.Provider.Transactions.Traits.Savepoints) throw NoSavepoints(session);

        var table = $"{edit.Schema}.{edit.Table}";
        var summary = $"Grid changes to {table}: {p.Updates.Length} updated, {p.Inserts.Length} inserted, {p.Deletes.Length} deleted row(s).";
        var findings = new[] { new DestructiveFinding { Rule = "gridEdit", Statement = summary, Description = "Writes the staged grid changes" } };
        var destructive = p.Deletes.Length > 0
            ? new[] { new DestructiveFinding { Rule = "delete", Statement = $"DELETE {p.Deletes.Length} row(s) FROM {table}", Description = $"Deletes {p.Deletes.Length} row(s) from {table}" } }
            : [];
        if (executions.ToolGuard(session, p.ConfirmedProd, p.ConfirmedDestructive, findings, destructive) is { } block)
            return new GridApplyResult { Blocked = block };

        var history = PatchBuilder.Preview(d, statements, $"-- Grid changes to {CommentText.Safe(table)}");
        var (result, executionId) = await executions.RunToolAsync(session, history,
            ctx => RunPatchAsync(ctx, d, edit, p, statements),
            r => (r.Ok ? r.AppliedUpdates + r.AppliedInserts + r.AppliedDeletes : 0, r.Ok ? null : r.Error ?? "Changes were rolled back: some rows changed on the server.")).ConfigureAwait(false);
        return result with { ExecutionId = executionId };
    }

    /// <summary>
    /// Writes the patch in one transaction of its own, or under a savepoint inside the user's open one. It is all or
    /// nothing whatever ends it (H5): a conflict, an error, a cancel, a lost connection — everything it wrote is undone
    /// before the exception or the result goes back, and when the undo cannot run the connection is closed (the server
    /// rolls back). Only a COMMIT cut short leaves the outcome unknown, and then the webview is told not to retry blindly.
    /// </summary>
    internal async Task<GridApplyResult> RunPatchAsync(ToolContext ctx, IDataEditDialect d, ResultEditInfo edit, GridPatchParams p, List<PatchStatement> statements)
    {
        var session = ctx.Session;
        var db = ctx.Connection.Db;
        var ct = ctx.Ct;
        var tx = session.Provider.Transactions;
        var inTx = await ctx.InTransactionAsync("write the grid changes").ConfigureAwait(false);
        // The transaction opened while the run waited in the queue: the same refusal as at the request (L15).
        if (inTx && !tx.Traits.Savepoints) throw NoSavepoints(session);
        var errors = new List<string>();
        using var sub = session.Provider.SubscribeMessages(db, m => { if (m.Severity == Severity.Error) lock (errors) errors.Add(m.Message); });

        var conflicts = new List<GridConflictRow>();
        var keys = new Dictionary<string, GridValue[]>();
        int updates = 0, inserts = 0, deletes = 0;
        string? error = null;
        // While true, rows of this patch may sit in its transaction or under its savepoint: whatever ends the patch now undoes them.
        var open = false;
        try
        {
            if (inTx)
            {
                await tx.SavepointAsync(db, Savepoint, ct).ConfigureAwait(false);
                open = true;
            }
            else
            {
                open = true; // a BEGIN cut short may still have opened one; the rollback is a no-op when it did not
                await tx.BeginAsync(db, ct).ConfigureAwait(false);
            }
            foreach (var s in statements)
            {
                await using var cmd = db.CreateCommand();
                cmd.CommandText = s.Sql;
                cmd.CommandTimeout = session.Provider.Traits.FixedCommandTimeoutS ?? session.Profile.CommandTimeoutS ?? 0;
                try
                {
                    for (var i = 0; i < s.Values.Count; i++) d.AddValue(cmd, i, s.Values[i].Type, s.Values[i].Value);
                }
                catch (FormatException fe)
                {
                    error = fe.Message;
                    conflicts.Add(new GridConflictRow { RowIndex = s.Row, Insert = s.Kind == PatchStatementKind.Insert, Reason = "error", Message = fe.Message });
                    break;
                }
                lock (errors) errors.Clear();
                // Cancel interrupts the statement as it does a query's (SQLite and DuckDB stop in process).
                ctx.Execution.CurrentCommand = cmd;
                try
                {
                    if (s.Kind == PatchStatementKind.Insert)
                    {
                        var key = await ReadRowAsync(cmd, ct).ConfigureAwait(false);
                        if (key != null) keys[s.Row.ToString(CultureInfo.InvariantCulture)] = key;
                        inserts++;
                    }
                    else
                    {
                        var n = await d.ExecuteCountedAsync(cmd, ct).ConfigureAwait(false);
                        if (FirstError(errors) is null)
                        {
                            if (n == 0) conflicts.Add(new GridConflictRow { RowIndex = s.Row, Reason = "changedOnServer" });
                            else if (n > 1)
                            {
                                error = $"The row key matched {n} rows; nothing was changed.";
                                conflicts.Add(new GridConflictRow { RowIndex = s.Row, Reason = "error", Message = error });
                                break;
                            }
                            else if (s.Kind == PatchStatementKind.Update) updates++;
                            else deletes++;
                        }
                    }
                }
                // A cancel, a lost connection or anything else unexpected ends the patch through the catch below.
                catch (Exception ex) when (ex is DbException or InvalidOperationException && !ct.IsCancellationRequested && ctx.Connection.IsOpen)
                {
                    lock (errors) errors.Add(session.Provider.TranslateException(ex, s.Sql).FirstOrDefault()?.Message ?? ex.Message);
                }
                finally { ctx.Execution.CurrentCommand = null; }
                if (FirstError(errors) is { } msg)
                {
                    error = msg;
                    conflicts.Add(new GridConflictRow { RowIndex = s.Row, Insert = s.Kind == PatchStatementKind.Insert, Reason = "error", Message = msg });
                    break;
                }
            }

            if (conflicts.Count == 0)
            {
                open = false;
                await FinishAsync(ctx, inTx).ConfigureAwait(false);
                return new GridApplyResult { Ok = true, AppliedUpdates = updates, AppliedInserts = inserts, AppliedDeletes = deletes, InsertedKeys = keys.Count > 0 ? keys : null };
            }

            open = false;
            await UndoAsync(ctx, inTx).ConfigureAwait(false);
        }
        catch (Exception ex) when (open)
        {
            log.LogWarning("Grid changes stopped before they were written ({Type}); undoing them", ex.GetType().Name);
            await UndoAsync(ctx, inTx).ConfigureAwait(false);
            if (ex is OperationCanceledException || ct.IsCancellationRequested)
                throw new QuerithmException("cancelled", "The grid changes were cancelled and rolled back: nothing was written.");
            throw;
        }
        // Rows that matched nothing: changed or deleted by someone else. Show the server's current row.
        var resolved = new List<GridConflictRow>();
        var reads = 0;
        foreach (var c in conflicts)
        {
            if (c.Reason != "changedOnServer" || reads++ >= MaxConflictReads) { resolved.Add(c); continue; }
            var key = KeyOf(p, c.RowIndex);
            var current = key == null ? null : await CurrentRowAsync(d, db, session, edit, key, ct).ConfigureAwait(false);
            resolved.Add(current == null ? c with { Reason = "deletedOnServer" } : c with { Current = current });
        }
        return new GridApplyResult
        {
            Ok = false, Conflicts = resolved.ToArray(),
            Error = error ?? $"{resolved.Count} row(s) changed on the server since they were read. Nothing was written.",
        };
    }

    internal static QuerithmException NoSavepoints(Session s) => new("unsupported",
        $"{s.Provider.Traits.Name} has no savepoints, so Querithm cannot apply grid changes inside your open transaction (undoing a failed one would roll back your own changes too). Commit or roll back first.");

    /// <summary>
    /// Commits the patch's own transaction, or releases its savepoint. Bounded by the cleanup limit, not the run's token:
    /// once every statement ran, a cancel is too late to change anything. A COMMIT that fails anyway (a lost connection,
    /// the escalation closing it) may or may not have reached the server, so the result says the outcome is unknown.
    /// </summary>
    async Task FinishAsync(ToolContext ctx, bool inTx)
    {
        var db = ctx.Connection.Db;
        var tx = ctx.Session.Provider.Transactions;
        using var limit = Cleanup.Start();
        try
        {
            if (inTx) await tx.ReleaseSavepointAsync(db, Savepoint, limit.Token).ConfigureAwait(false);
            else await tx.CommitAsync(db, limit.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            log.LogWarning("Finishing the grid changes failed: {Error}", Logging.LogScrubber.Describe(ex));
            // The server answered with an error on a working connection: it refused the COMMIT, so nothing was committed.
            // So did one that found its transaction gone and sent nothing (SQLite / DuckDB, NoTransactionOpenException).
            var refused = ex is DbException or NoTransactionOpenException && ctx.Connection.IsOpen;
            await UndoAsync(ctx, inTx).ConfigureAwait(false);
            if (inTx || refused)
                throw new QuerithmException("applyFailed",
                    $"The grid changes could not be {(inTx ? "finished" : "committed")}, so nothing was written. ({ctx.Session.Provider.TranslateException(ex).FirstOrDefault()?.Message ?? ex.Message})");
        }
        throw new QuerithmException("outcomeUnknown",
            "The connection failed while the grid changes were being committed, so whether they were written is unknown. Re-run the query to see the table's rows before you apply anything again.");
    }

    static string? FirstError(List<string> errors)
    {
        lock (errors) return errors.Count > 0 ? errors[0] : null;
    }

    static GridValue[]? KeyOf(GridPatchParams p, int row) =>
        p.Updates.FirstOrDefault(u => u.RowIndex == row)?.Key ?? p.Deletes.FirstOrDefault(x => x.RowIndex == row)?.Key;

    /// <summary>
    /// Undoes the patch within the cleanup limit: back to its savepoint, or out of its own transaction. A doomed SQL
    /// Server transaction (or SQLite's, after an interrupted write) cannot go back to a savepoint: then the whole
    /// transaction is rolled back and the tab is told. When nothing can be rolled back the connection is closed, so the
    /// patch's rows never stay in the user's transaction or in a pooled connection.
    /// </summary>
    async Task UndoAsync(ToolContext ctx, bool inTx)
    {
        var db = ctx.Connection.Db;
        var tx = ctx.Session.Provider.Transactions;
        using var cleanup = Cleanup.Start();
        try
        {
            if (inTx)
            {
                await tx.RollbackToSavepointAsync(db, Savepoint, cleanup.Token).ConfigureAwait(false);
                await tx.ReleaseSavepointAsync(db, Savepoint, cleanup.Token).ConfigureAwait(false);
            }
            else await tx.RollbackAsync(db, cleanup.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) { log.LogWarning("Rollback of grid changes failed: {Message}", ex.Message); }
        if (inTx && ctx.Connection.IsOpen && !cleanup.IsCancellationRequested)
            try
            {
                await tx.RollbackAsync(db, cleanup.Token).ConfigureAwait(false);
                ctx.Message(Severity.Warning, "The server rolled back the whole open transaction while undoing the grid changes.");
                return;
            }
            catch (Exception ex) { log.LogWarning("Rolling back the open transaction failed: {Message}", ex.Message); }
        ctx.AbandonConnection(inTx
            ? "Querithm could not roll back the grid changes, so it closed the connection: the server rolls them back, and your open transaction with them."
            : "Querithm could not roll back the grid changes, so it closed the connection: the server rolls them back.");
    }

    /// <summary>
    /// The first row of the last result set that has one. Every result set is read, so a trigger's own result set
    /// (before the statement's key SELECT) is skipped and an error raised after it still surfaces.
    /// </summary>
    static async Task<GridValue[]?> ReadRowAsync(DbCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        GridValue[]? last = null;
        do
        {
            if (r.FieldCount == 0) continue;
            GridValue[]? row = null;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (row != null) continue;
                row = new GridValue[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++)
                    row[i] = r.IsDBNull(i) ? new GridValue { IsNull = true } : new GridValue { Text = Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) };
            }
            if (row != null) last = row;
        } while (await r.NextResultAsync(ct).ConfigureAwait(false));
        return last;
    }

    async Task<Dictionary<string, GridValue>?> CurrentRowAsync(IDataEditDialect d, DbConnection db, Session session, ResultEditInfo edit, GridValue[] key, CancellationToken ct)
    {
        var cols = edit.Columns.Where(c => c.Column != null).GroupBy(c => c.Column!).Select(g => g.First()).ToArray();
        var keyCols = edit.KeyColumns.Select(k => cols.First(c => c.Column == k)).ToArray();
        try
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandTimeout = session.Provider.Traits.FixedCommandTimeoutS ?? session.Profile.CommandTimeoutS ?? 0;
            var where = new List<string>();
            for (var i = 0; i < keyCols.Length; i++)
            {
                d.AddValue(cmd, i, keyCols[i].Type, key[i]);
                where.Add($"{d.Quote(keyCols[i].Column!)} = {d.TypedPlaceholder(i, keyCols[i].Type)}");
            }
            cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(c => d.ValueText(d.Quote(c.Column!), c.Type)))} FROM {TableName(d, edit)} WHERE {string.Join(" AND ", where)}";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
            var row = new Dictionary<string, GridValue>(StringComparer.Ordinal);
            for (var i = 0; i < cols.Length; i++)
                row[cols[i].Column!] = r.IsDBNull(i) ? new GridValue { IsNull = true } : new GridValue { Text = Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) };
            return row;
        }
        catch (Exception ex) when (ex is DbException or FormatException)
        {
            // The type only (M21): a FormatException's message quotes the key value it could not read, a server error may too.
            log.LogDebug("Could not read the current row: {Type}", ex.GetType().Name);
            return null;
        }
    }
}
