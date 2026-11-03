using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Execution;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;

namespace Querithm.Core.Import;

/// <summary>
/// The import wizard's engine: inspect (format detection + full-file profiling + preview frames), the target
/// check (new table DDL or per-column verdicts against the cached schema), and the load, which runs as one execution
/// through <see cref="ExecutionService.RunToolAsync{T}"/> with the engine's native bulk loader and a reject log.
/// </summary>
public sealed class ImportService(SessionManager sessions, ExecutionService executions, MetadataService metadata, INotifier notifier, string dataDir, ILogger log)
{
    const string Savepoint = "querithm_import";
    static readonly TimeSpan Keep = TimeSpan.FromHours(2);

    readonly ConcurrentDictionary<string, (Inspection Inspection, DateTime At)> _inspections = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> _profiling = new();

    /// <summary>The shell injects the dialog-picked path; anything else (no path, a directory, a device) is refused.</summary>
    static string CheckedPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            throw new QuerithmException("fileToken", "The file was not picked with the file dialog (no path came with the token). Pick it again.");
        if (!Path.IsPathFullyQualified(path)) throw new QuerithmException("fileToken", "The picked file path is not absolute.");
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) throw new QuerithmException("importFile", $"{Path.GetFileName(full)} no longer exists.");
        return full;
    }

    Inspection Get(string id)
    {
        foreach (var (k, v) in _inspections) if (DateTime.UtcNow - v.At > Keep) _inspections.TryRemove(k, out _);
        return _inspections.TryGetValue(id, out var e) ? e.Inspection : throw new QuerithmException("inspectionGone", "The file inspection expired. Pick the file again.");
    }

    // ---------------------------------------------------------------------------------------------- inspect

    public async Task<ImportInspectResult> InspectAsync(ImportInspectParams p, CancellationToken ct)
    {
        var path = CheckedPath(p.FilePath);
        var id = Ulid.New().ToString();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _profiling[p.FileToken] = cts;
        var sw = Stopwatch.StartNew();
        Inspection ins;
        try
        {
            ins = await Task.Run(() => FileInspector.Inspect(id, path, p.Options, (rows, bytes, total) => notifier.Notify("import.progress", new ImportProgressEvent
            {
                JobId = p.FileToken, Phase = "profiling", RowsProcessed = rows, BytesProcessed = bytes, TotalBytes = total, ElapsedMs = sw.Elapsed.TotalMilliseconds,
            }), cts.Token), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or System.Xml.XmlException or DecoderFallbackException)
        {
            throw new QuerithmException("importFile", $"{Path.GetFileName(path)} could not be read: {ex.Message}");
        }
        finally { _profiling.TryRemove(p.FileToken, out _); }
        _inspections[id] = (ins, DateTime.UtcNow);
        var preview = await executions.StaticResultAsync(ins.Plan.Columns, ins.Preview).ConfigureAwait(false);
        notifier.Notify("import.progress", new ImportProgressEvent
        {
            JobId = p.FileToken, Phase = "profiling", RowsProcessed = ins.RowCount, BytesProcessed = new FileInfo(path).Length, TotalBytes = new FileInfo(path).Length,
            ElapsedMs = sw.Elapsed.TotalMilliseconds, Done = true,
        });
        return new ImportInspectResult
        {
            InspectionId = id, FileName = Path.GetFileName(path), FileBytes = new FileInfo(path).Length, Format = ins.Plan.Format,
            Delimiter = ins.Plan.Format is ImportFormat.Csv or ImportFormat.Tsv ? (ins.Plan.Delimiter == '\t' ? "\\t" : ins.Plan.Delimiter.ToString()) : null,
            HasHeader = ins.Plan.HasHeader, Encoding = FileInspector.EncodingName(ins.Plan.Encoding), Sheets = ins.Sheets, Sheet = ins.Plan.Sheet,
            RowCount = ins.RowCount, Columns = ins.Columns, PreviewExecutionId = preview, Notes = ins.Notes,
        };
    }

    // ---------------------------------------------------------------------------------------------- target check

    static SnapObject? FindTable(SchemaSnapshot? snap, string schema, string table) =>
        snap?.Objects.Where(o => o.Kind == ObjectKind.Table).FirstOrDefault(o => o.Schema == schema && o.Name == table)
        ?? snap?.Objects.Where(o => o.Kind == ObjectKind.Table).FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(o.Name, table, StringComparison.OrdinalIgnoreCase));

    static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>File columns → table columns: the given mapping, else by name (case-insensitive, then ignoring spaces and underscores).</summary>
    public static ImportMapping[] AutoMap(IReadOnlyList<ImportColumnProfile> file, IReadOnlyList<SnapColumn>? target, ImportMapping[]? given)
    {
        if (given != null) return given;
        return file.Select(f =>
        {
            if (target == null) return new ImportMapping { FileColumn = f.Name, TargetColumn = f.Name };
            var t = target.FirstOrDefault(c => string.Equals(c.Name, f.Name, StringComparison.OrdinalIgnoreCase))
                    ?? target.FirstOrDefault(c => Normalize(c.Name) == Normalize(f.Name));
            return new ImportMapping { FileColumn = f.Name, TargetColumn = t?.Name };
        }).ToArray();
    }

    public ImportTargetResult CheckTarget(ImportTargetParams p)
    {
        var session = sessions.Get(p.SessionId);
        FeatureGuard.Require(session.Provider, Feature.Import);
        var ins = Get(p.InspectionId);
        var dialect = session.Provider.Import;
        var snap = metadata.Cache.Get(session.Profile.Id, session.Database, session.Id);
        var table = FindTable(snap, p.Schema, p.Table);
        var profiles = ins.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        if (p.NewTable)
        {
            if (table != null) throw QuerithmException.Invalid($"{table.Schema}.{table.Name} already exists. Choose “Existing table” to load into it.");
            var mapping = AutoMap(ins.Columns, null, p.Mapping);
            var cols = new List<(string Name, string Type, bool NotNull)>();
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in mapping)
            {
                if (m.TargetColumn == null || !profiles.TryGetValue(m.FileColumn, out var prof)) continue;
                var type = p.TypeOverrides?.GetValueOrDefault(m.FileColumn) is { Length: > 0 } o ? o : dialect.TypeFor(prof);
                types[m.FileColumn] = type;
                cols.Add((m.TargetColumn, type, false));
            }
            var pk = (p.PrimaryKey ?? []).Select(k => mapping.FirstOrDefault(m => m.FileColumn == k)?.TargetColumn).OfType<string>().ToArray();
            var verdicts = pk.Select(k => mapping.First(m => m.TargetColumn == k)).Where(m => profiles[m.FileColumn].NullCount > 0)
                .Select(m => new ImportColumnVerdict
                {
                    FileColumn = m.FileColumn, TargetColumn = m.TargetColumn, Kind = ImportVerdictKind.NullIntoNotNull,
                    Message = $"{profiles[m.FileColumn].NullCount:N0} row(s) have no value for this primary key column (first: row {string.Join(", ", profiles[m.FileColumn].FirstNullRows)})",
                }).ToArray();
            return new ImportTargetResult { TableExists = false, Mapping = mapping, Verdicts = verdicts, CreateTableSql = dialect.CreateTable(p.Schema, p.Table, cols, pk), ProposedTypes = types };
        }
        if (table?.Columns == null)
            throw new QuerithmException("tableNotFound", $"{p.Schema}.{p.Table} is not in the cached metadata. Refresh the explorer, or create the table first.");
        var map = AutoMap(ins.Columns, table.Columns, p.Mapping);
        var list = new List<ImportColumnVerdict>();
        foreach (var m in map)
        {
            if (!profiles.TryGetValue(m.FileColumn, out var prof)) continue;
            var target = m.TargetColumn == null ? null : table.Columns.FirstOrDefault(c => string.Equals(c.Name, m.TargetColumn, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                list.Add(new ImportColumnVerdict
                {
                    FileColumn = m.FileColumn, TargetColumn = m.TargetColumn, Kind = ImportVerdictKind.MissingInTarget,
                    Message = m.TargetColumn == null ? $"No column of {table.Schema}.{table.Name} matches; it is skipped unless added" : $"{table.Schema}.{table.Name} has no column {m.TargetColumn}",
                    SuggestedSql = dialect.AddColumn(table.Schema, table.Name, m.TargetColumn ?? m.FileColumn, dialect.TypeFor(prof)),
                });
                continue;
            }
            if (target.Identity || target.Computed)
            {
                list.Add(new ImportColumnVerdict
                {
                    FileColumn = m.FileColumn, TargetColumn = target.Name, Kind = target.Computed ? ImportVerdictKind.Mismatch : ImportVerdictKind.Fits,
                    Message = target.Computed ? $"{target.Name} is computed; values cannot be loaded into it" : $"{target.Name} is an identity column: values are kept only with “Keep identity values”",
                });
                continue;
            }
            var fit = dialect.Check(prof, target);
            list.Add(new ImportColumnVerdict
            {
                FileColumn = m.FileColumn, TargetColumn = target.Name, Kind = fit.Kind, Message = fit.Message,
                SuggestedSql = fit.WidenTo != null ? dialect.AlterColumnType(table.Schema, table.Name, target, fit.WidenTo) : null,
            });
            if (prof.NullCount > 0 && !target.Nullable && !(dialect.NullUsesDefault && target.Default != null))
                list.Add(new ImportColumnVerdict
                {
                    FileColumn = m.FileColumn, TargetColumn = target.Name, Kind = ImportVerdictKind.NullIntoNotNull,
                    Message = $"{prof.NullCount:N0} row(s) have no value, but {target.Name} is NOT NULL (first: row {string.Join(", ", prof.FirstNullRows)}); those rows will fail",
                });
        }
        var mapped = new HashSet<string>(map.Where(m => m.TargetColumn != null).Select(m => m.TargetColumn!), StringComparer.OrdinalIgnoreCase);
        foreach (var c in table.Columns)
            if (!mapped.Contains(c.Name) && !c.Nullable && c.Default == null && !c.Identity && !c.Computed && !IsRowVersion(c))
                list.Add(new ImportColumnVerdict
                {
                    FileColumn = "", TargetColumn = c.Name, Kind = ImportVerdictKind.RequiredUnmapped,
                    Message = $"{c.Name} is NOT NULL without a default, and no file column maps to it",
                });
        return new ImportTargetResult { TableExists = true, Mapping = map, Verdicts = list.ToArray() };
    }

    static bool IsRowVersion(SnapColumn c) => c.Type is "timestamp" or "rowversion";

    // ---------------------------------------------------------------------------------------------- run

    sealed record LoadResult(bool Ok, long Loaded, long Rejected, string? Error, string? RejectFile, long? Committed, bool Cancelled, string? ErrorCode = null);

    public async Task<ImportRunResult> RunAsync(ImportRunParams p)
    {
        var session = sessions.Get(p.SessionId);
        FeatureGuard.Require(session.Provider, Feature.Import);
        var ins = Get(p.InspectionId);
        var path = CheckedPath(p.FilePath);
        if (!string.Equals(path, Path.GetFullPath(ins.FilePath), StringComparison.Ordinal))
            throw new QuerithmException("fileToken", "The picked file does not match the inspected one. Pick the file again.");
        var options = p.Options ?? new ImportRunOptions();
        var single = options.TransactionMode == ImportTransactionMode.Single;
        if (!single && session.TxState != TxState.None) throw BatchesInTransaction(session.TxState);
        if (single && session.TxState != TxState.None && !session.Provider.Transactions.Traits.Savepoints) throw NoSavepoints(session);

        var table = FindTable(metadata.Cache.Get(session.Profile.Id, session.Database, session.Id), p.Schema, p.Table);
        if (table?.Columns == null && session.Database != null)
        {
            // A table created a moment ago (the wizard's CREATE TABLE) may not be in the snapshot yet. A session-owned
            // session reads its own view (an in-memory database, temporary tables): refresh and wait for that one.
            metadata.Refresh(new RefreshParams { ProfileId = session.Profile.Id, Database = session.Database, SessionId = session.Id });
            await Task.Delay(50).ConfigureAwait(false);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await metadata.WaitAsync(session.Profile.Id, session.Database, session.Id).WaitAsync(wait.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            table = FindTable(metadata.Cache.Get(session.Profile.Id, session.Database, session.Id), p.Schema, p.Table);
        }
        if (table?.Columns == null && session.TxState == TxState.Open)
            throw new QuerithmException("tableNotFound", $"{p.Schema}.{p.Table} is not in the metadata yet: a table created inside the open transaction is read when the transaction ends. Commit, then import.");
        if (table?.Columns == null) throw new QuerithmException("tableNotFound", $"{p.Schema}.{p.Table} does not exist (or is not in the cached metadata yet).");

        var mapping = AutoMap(ins.Columns, table.Columns, p.Mapping);
        var fileIndex = ins.Plan.Columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.Ordinal);
        var cols = new List<BulkColumn>();
        var sources = new List<int>();
        foreach (var m in mapping)
        {
            if (m.TargetColumn == null || !fileIndex.TryGetValue(m.FileColumn, out var idx)) continue;
            // Names come from the sidecar's metadata, never from the request (it only says which column).
            var target = table.Columns.FirstOrDefault(c => string.Equals(c.Name, m.TargetColumn, StringComparison.OrdinalIgnoreCase))
                         ?? throw QuerithmException.Invalid($"{table.Schema}.{table.Name} has no column {m.TargetColumn}.");
            if (target.Computed) throw QuerithmException.Invalid($"{target.Name} is a computed column; it cannot be loaded.");
            if (target.Identity && !options.KeepIdentity) continue;
            if (cols.Any(c => c.Name == target.Name)) throw QuerithmException.Invalid($"Two file columns map to {target.Name}.");
            cols.Add(new BulkColumn(target.Name, target.Type, target.Identity));
            sources.Add(idx);
        }
        if (cols.Count == 0) throw QuerithmException.Invalid("No file column maps to a column of the table.");

        var bulk = new BulkTarget(table.Schema, table.Name, cols);
        var qualified = $"{table.Schema}.{table.Name}";
        var findings = new[] { new DestructiveFinding { Rule = "import", Statement = $"Import {ins.RowCount:N0} row(s) from {Path.GetFileName(path)} into {qualified}", Description = $"Loads {ins.RowCount:N0} row(s) into {qualified}" } };
        if (executions.ToolGuard(session, p.ConfirmedProd, false, findings, []) is { } block) return new ImportRunResult { Blocked = block };

        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = $"-- Import {CommentText.Safe($"{Path.GetFileName(path)} ({ins.RowCount:N0} rows) into {qualified} ({string.Join(", ", cols.Select(c => c.Name))})")}";
        string? jobId = null;
        var reported = false; // LoadAsync sent the final import.progress
        _ = Task.Run(async () =>
        {
            try
            {
                await executions.RunToolAsync(session, history,
                    async ctx =>
                    {
                        log.LogInformation("Import {JobId}: loading", ctx.Execution.IdText);
                        var r = await LoadAsync(ctx, ins, path, bulk, sources, options, single).ConfigureAwait(false);
                        reported = true;
                        log.LogInformation("Import {JobId}: finished, {Loaded} loaded, {Rejected} rejected, ok={Ok}", ctx.Execution.IdText, r.Loaded, r.Rejected, r.Ok);
                        return r;
                    },
                    r => (r.Loaded, r.Ok ? null : r.Error),
                    id =>
                    {
                        jobId = id;
                        log.LogInformation("Import {JobId}: started, waiting for the session and a connection", id);
                        started.TrySetResult(id);
                    }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning("Import failed: {Error}", Logging.LogScrubber.Describe(ex));
                started.TrySetException(ex);
                // Ended before the load ran (cancelled while queued, no connection): the wizard still needs its final event.
                if (jobId != null && !reported)
                {
                    var cancelled = ex is OperationCanceledException;
                    notifier.Notify("import.progress", new ImportProgressEvent
                    {
                        JobId = jobId, Phase = "loading", Done = true, Cancelled = cancelled,
                        Error = cancelled ? "The import was cancelled." : session.Provider.TranslateException(ex).FirstOrDefault()?.Message ?? ex.Message,
                    });
                }
            }
            finally { _inspections.TryRemove(p.InspectionId, out _); }
        });
        return new ImportRunResult { JobId = await started.Task.ConfigureAwait(false) };
    }

    async Task<LoadResult> LoadAsync(ToolContext ctx, Inspection ins, string path, BulkTarget target, List<int> sources, ImportRunOptions options, bool single)
    {
        var session = ctx.Session;
        var db = ctx.Connection.Db;
        var d = session.Provider.DataEdit;
        var loader = session.Provider.Import.Loader ?? new BatchInsertLoader(d, session.Provider.Transactions);
        // Read again on the connection: the transaction may have opened (or its state become unknown) while the run queued.
        var inTx = await ctx.InTransactionAsync("import").ConfigureAwait(false);
        if (inTx && !single) throw BatchesInTransaction(TxState.Open);
        if (inTx && !session.Provider.Transactions.Traits.Savepoints) throw NoSavepoints(session);
        var sw = Stopwatch.StartNew();
        var jobId = ctx.Execution.IdText;
        using var src = RowSourceFactory.Open(path, ins.Plan);
        var blankIsNull = target.Columns.Select(c => session.Provider.Import.BlankIsNull(c.Type)).ToArray();
        var rows = new MappedRows(src, sources, blankIsNull, options.MaxRejects, RejectPath(path), ins.Plan.Columns, (loaded, rejected) => notifier.Notify("import.progress", new ImportProgressEvent
        {
            JobId = jobId, Phase = "loading", RowsProcessed = src.Row, RowsLoaded = loaded, BytesProcessed = src.BytesRead, TotalBytes = src.TotalBytes, Rejected = rejected,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        }));
        string? error = null;
        string? errorCode = null;
        var cancelled = false;
        // One transaction of the loader's own; inside the user's open transaction, under a savepoint the service owns.
        var tx = !single ? new BulkTransaction(BulkTxMode.Batches, Math.Max(1, options.CommitEvery ?? 10_000)) : inTx ? new BulkTransaction(BulkTxMode.Caller) : new BulkTransaction(BulkTxMode.Own);
        var control = session.Provider.Transactions;
        var savepointSet = false;
        try
        {
            if (tx.Mode == BulkTxMode.Caller)
            {
                await control.SavepointAsync(db, Savepoint, ctx.Ct).ConfigureAwait(false);
                savepointSet = true;
            }
            await loader.LoadAsync(db, target, rows, options, tx, session.Profile.CommandTimeoutS, ctx.Ct).ConfigureAwait(false);
            if (tx.Mode == BulkTxMode.Caller)
            {
                // Every row is in: a cancel now is too late to change anything, so the release has the cleanup bound instead.
                using var release = Cleanup.Start();
                await control.ReleaseSavepointAsync(db, Savepoint, release.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            cancelled = ctx.Ct.IsCancellationRequested || ex is OperationCanceledException;
            // A COMMIT of the loader's own that the server refused on a working connection wrote nothing (that batch, in
            // batches mode); one that found its transaction already gone (SQLite / DuckDB check before sending: an
            // interrupted statement rolled it back) sent nothing; one cut short — a lost connection, a cancel — may have
            // committed or not, so a retry could load the rows twice: the outcome is unknown, as for a grid Apply.
            var gone = ex is NoTransactionOpenException;
            if (tx.InCommit && !gone && !(ex is DbException && !cancelled && ctx.Connection.IsOpen))
            {
                errorCode = "outcomeUnknown";
                cancelled = false;
                error = single
                    ? $"The connection failed while the import was being committed, so whether its {rows.LoadedRows:N0} row(s) were written to {target.Schema}.{target.Table} is unknown. Check the table before you import this file again."
                    : $"The connection failed while a batch was being committed: {rows.CommittedRows:N0} row(s) are committed, and whether the batch after them was written is unknown. Check {target.Schema}.{target.Table} before you resume.";
                log.LogWarning("Import {JobId}: the COMMIT was cut short ({Type}); the outcome is unknown", jobId, ex.GetType().Name);
            }
            else if (gone && tx.InCommit)
                error = single
                    ? $"The import's transaction had already been rolled back when Querithm committed it (an interrupted statement ends it), so nothing was written to {target.Schema}.{target.Table}."
                    : $"A batch's transaction had already been rolled back when Querithm committed it (an interrupted statement ends it): {rows.CommittedRows:N0} row(s) are committed in {target.Schema}.{target.Table}, the batch after them was not written.";
            else error = cancelled ? "The import was cancelled." : ex is RejectLimitException ? ex.Message : session.Provider.TranslateException(ex).FirstOrDefault()?.Message ?? ex.Message;
            // Inside the user's transaction nothing was written before the savepoint existed: then there is nothing to undo.
            if (tx.Mode != BulkTxMode.Caller || savepointSet) await UndoAsync(ctx, tx.Mode == BulkTxMode.Caller).ConfigureAwait(false);
        }
        finally { rows.CloseRejects(); }
        var loaded = error == null ? rows.LoadedRows : single ? 0 : rows.CommittedRows;
        var result = new LoadResult(error == null, loaded, rows.Rejected, error, rows.RejectFile, single ? null : rows.CommittedRows, cancelled, errorCode);
        notifier.Notify("import.progress", new ImportProgressEvent
        {
            JobId = jobId, Phase = "loading", RowsProcessed = src.Row, RowsLoaded = loaded, BytesProcessed = src.TotalBytes, TotalBytes = src.TotalBytes,
            Rejected = rows.Rejected, ElapsedMs = sw.Elapsed.TotalMilliseconds, Done = true, Cancelled = cancelled, Error = error, RejectFile = rows.RejectFile,
            CommittedRows = single || error == null ? null : rows.CommittedRows, ErrorCode = errorCode,
        });
        return result;
    }

    static QuerithmException BatchesInTransaction(TxState state) => QuerithmException.Invalid(state == TxState.Open
        ? "Batches mode commits on its own, which would commit your open transaction too. Commit or roll it back first, or import in one transaction."
        : "This tab may have an open transaction (its state cannot be read), and batches mode commits on its own, which would commit it too. Commit or roll back first, or import in one transaction.");

    static QuerithmException NoSavepoints(Session s) => new("unsupported",
        $"{s.Provider.Traits.Name} has no savepoints, so Querithm cannot import inside your open transaction (undoing a failed import would roll back your own changes too). Commit or roll back first.");

    /// <summary>
    /// Nothing of a failed or cancelled import stays open (H5): back to the service's savepoint inside the user's
    /// transaction, else whatever the loader's own transaction left (a no-op once the loader rolled back). A savepoint the
    /// server already dropped (a doomed transaction) takes the whole transaction; when nothing can be rolled back the
    /// connection is closed and the server rolls back.
    /// </summary>
    async Task UndoAsync(ToolContext ctx, bool savepoint)
    {
        var db = ctx.Connection.Db;
        var control = ctx.Session.Provider.Transactions;
        using var cleanup = Cleanup.Start();
        try
        {
            if (savepoint)
            {
                await control.RollbackToSavepointAsync(db, Savepoint, cleanup.Token).ConfigureAwait(false);
                await control.ReleaseSavepointAsync(db, Savepoint, cleanup.Token).ConfigureAwait(false);
            }
            else if (ctx.Connection.IsOpen) await control.RollbackAsync(db, cleanup.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) { log.LogWarning("Import rollback failed: {Message}", ex.Message); }
        if (savepoint && ctx.Connection.IsOpen && !cleanup.IsCancellationRequested)
            try
            {
                await control.RollbackAsync(db, cleanup.Token).ConfigureAwait(false);
                ctx.Message(Severity.Warning, "The server rolled back the whole open transaction while undoing the import.");
                return;
            }
            catch (Exception ex) { log.LogWarning("Rolling back the open transaction failed: {Message}", ex.Message); }
        ctx.AbandonConnection(savepoint
            ? "Querithm could not roll back the import, so it closed the connection: the server rolls it back, and your open transaction with it."
            : "Querithm could not roll back the import, so it closed the connection: the server rolls it back.");
    }

    string RejectPath(string source)
    {
        var name = string.Concat(Path.GetFileName(source).Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
        return Path.Combine(dataDir, "imports", $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}.rejected.csv");
    }

    public ImportCancelResult Cancel(ImportJobRef p)
    {
        if (_profiling.TryGetValue(p.JobId, out var cts))
        {
            cts.Cancel();
            return new ImportCancelResult { Cancelled = true };
        }
        return new ImportCancelResult { Cancelled = executions.Cancel(p.JobId).Cancelled };
    }
}

sealed class RejectLimitException(string message) : Exception(message);

/// <summary>
/// File rows mapped to the target columns, with the reject log and progress counters. A whitespace-only cell is NULL
/// for the columns <paramref name="blankIsNull"/> marks (non-character types), as the profile counted it.
/// </summary>
sealed class MappedRows(IRowSource src, List<int> sources, bool[] blankIsNull, int maxRejects, string rejectPath, string[] fileColumns, Action<long, long> progress) : IBulkRows
{
    readonly string?[] _current = new string?[sources.Count];
    string?[]? _raw;
    StreamWriter? _rejects;
    long _lastTick = Environment.TickCount64;

    public IReadOnlyList<string?> Current => _current;
    public long Row => src.Row;
    public long Rejected { get; private set; }
    public long LoadedRows { get; private set; }
    public long CommittedRows { get; private set; }
    public string? RejectFile { get; private set; }

    public bool Read()
    {
        _raw = src.Next();
        if (_raw == null) return false;
        for (var i = 0; i < sources.Count; i++)
        {
            var v = sources[i] < _raw.Length ? _raw[sources[i]] : null;
            _current[i] = v != null && blankIsNull[i] && string.IsNullOrWhiteSpace(v) ? null : v;
        }
        return true;
    }

    public void Reject(string reason)
    {
        Rejected++;
        if (_rejects == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rejectPath)!);
            _rejects = new StreamWriter(rejectPath, false, new UTF8Encoding(false));
            RejectFile = rejectPath;
            _rejects.WriteLine(string.Join(",", new[] { "row", "reason" }.Concat(fileColumns).Select(c => DelimitedReader.Escape(c, ','))));
        }
        _rejects.WriteLine(string.Join(",", new[] { Row.ToString(CultureInfo.InvariantCulture), reason }.Concat((_raw ?? []).Select(v => v ?? "")).Select(c => DelimitedReader.Escape(c, ','))));
        if (Rejected > maxRejects)
            throw new RejectLimitException(maxRejects == 0
                ? $"Row {Row}: {reason}. Nothing was imported (stop at the first error); the row is in the reject log."
                : $"Stopped: more than {maxRejects:N0} rows could not be converted (last: row {Row}, {reason}). See the reject log.");
        Tick();
    }

    public void Loaded(long totalRows, bool committed)
    {
        LoadedRows = totalRows;
        if (committed) CommittedRows = totalRows;
        Tick();
    }

    void Tick()
    {
        if (Environment.TickCount64 - _lastTick < 250) return;
        _lastTick = Environment.TickCount64;
        progress(LoadedRows, Rejected);
    }

    public void CloseRejects() => _rejects?.Dispose();
}
