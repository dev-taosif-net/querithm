using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Execution;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;

namespace Querithm.Core.Compare;

/// <summary>
/// dataCompare.*: resolves both sides against the sidecar's own metadata, reads them ordered by key on two
/// pool connections at once, streams the differences to the data plane, and writes the sync script from what it kept.
/// </summary>
public sealed class DataCompareService(SessionManager sessions, MetadataService metadata, ExecutionService executions, INotifier notifier, ILogger log)
{
    /// <summary>A side once resolved: its columns (names and types the sidecar read itself) and how to read it.</summary>
    sealed record Resolved(Session Session, string Database, IDataCompareDialect Compare, IDataEditDialect Edit, string? Schema, string? Table, string? Query,
        IReadOnlyList<(string Name, string Type, bool Pk, bool Identity, bool Computed)> Columns)
    {
        public string Label => Table != null ? $"{Session.Profile.Name} · {Database}.{Schema}.{Table}" : $"{Session.Profile.Name} · {Database} (query)";
    }

    sealed class State(string id, Resolved source, Resolved target, ColumnPair[] key, ColumnPair[] columns, ComparePlan plan, Execution.Execution exec)
    {
        public string Id { get; } = id;
        public Resolved Source { get; } = source;
        public Resolved Target { get; } = target;
        public ColumnPair[] Key { get; } = key;
        public ColumnPair[] Columns { get; } = columns;
        public ComparePlan Plan { get; } = plan;
        public Execution.Execution Exec { get; } = exec;
        /// <summary>
        /// dataCompare.cancel, or the stream's execution cancelled: query.close when the tab goes (its reader stopped, so a
        /// producer still writing would hold the data plane, H3) and the sidecar shutting down.
        /// </summary>
        public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(exec.Cts.Token);
        public CompareCounts Counts { get; } = new();
        public List<DiffRow> Diffs { get; } = [];
        /// <summary>Estimated managed bytes of <see cref="Diffs"/> (L21).</summary>
        public long KeptBytes;
        public bool Truncated;
        public string? TruncatedNote;
        public volatile bool Done;
        public string? Error;
        public DateTime At { get; } = DateTime.UtcNow;
    }

    readonly ConcurrentDictionary<string, State> _compares = new();
    const int Keep = 4;
    /// <summary>A diff batch flushes at this many rows or at <see cref="ResultSetEncoder.MaxBatchBytes"/>, as a query batch does.</summary>
    const int BatchRows = 1000;

    /// <summary>
    /// L21: the estimated managed bytes the kept differences of one comparison may hold — with <see cref="Keep"/> kept,
    /// 256 MB in all. Past it (or past <see cref="DataCompareOptions.MaxDiffRows"/>) the rest are counted, not kept or streamed.
    /// </summary>
    public const long DefaultKeptBytes = 64L << 20;

    /// <summary>Test seam: <see cref="DefaultKeptBytes"/> unless a test lowers it.</summary>
    internal long KeptBytesLimit { get; set; } = DefaultKeptBytes;

    /// <summary>What a kept difference holds in memory, estimated: the row object, its arrays and their UTF-16 strings.</summary>
    internal static long KeptFootprint(DiffRow d)
    {
        static long Row(string?[]? r)
        {
            if (r == null) return 0;
            var n = 24 + 8L * r.Length;
            foreach (var v in r) if (v != null) n += 24 + 2L * v.Length;
            return n;
        }
        return 64 + d.Mask.Length + Row(d.Source) + Row(d.Target);
    }

    /// <summary>A difference's share of a diff batch, estimated as the query encoder does text (2 bytes per character + 4).</summary>
    static long WireBytes(DiffRow d)
    {
        static long Row(string?[]? r)
        {
            if (r == null) return 0;
            long n = 0;
            foreach (var v in r) n += (v?.Length ?? 0) * 2L + 4;
            return n;
        }
        return 8 + d.Mask.Length + Row(d.Source) + Row(d.Target);
    }

    static StringComparer NamesOf(Session s) => s.Provider.Traits.NameMatch;

    async Task<Resolved> ResolveAsync(DataCompareSide side, string role, CancellationToken ct)
    {
        var s = sessions.Get(side.SessionId);
        FeatureGuard.Require(s.Provider, Feature.DataCompare); // the provider's own reason first
        var compare = s.Provider.DataCompare ?? throw new QuerithmException("compareUnsupported", $"Data compare is not available for {s.Provider.Engine} yet.");
        var db = side.Database ?? s.Database ?? throw QuerithmException.Invalid($"Choose the {role} database.");
        if (side.Table != null == (side.Query != null)) throw QuerithmException.Invalid($"The {role} needs a table or a query (one of them).");
        if (side.Table != null)
        {
            var snap = metadata.Cache.Get(s.Profile.Id, db, s.Id);
            var names = NamesOf(s);
            var obj = snap?.Objects.FirstOrDefault(o => o.Kind is ObjectKind.Table or ObjectKind.View && names.Equals(o.Name, side.Table) &&
                                                        (side.Schema == null || names.Equals(o.Schema, side.Schema)))
                      ?? throw new QuerithmException("compareTable", $"{side.Schema}.{side.Table} is not in the cached metadata of {db}. Refresh the database in the explorer and try again.");
            var cols = (obj.Columns ?? []).Select(c => (c.Name, c.Type, c.Pk, c.Identity, c.Computed)).ToList();
            if (cols.Count == 0) throw new QuerithmException("compareTable", $"No columns are cached for {obj.Schema}.{obj.Name}. Refresh the database and try again.");
            return new Resolved(s, db, compare, s.Provider.DataEdit, obj.Schema, obj.Name, null, cols);
        }
        var sql = side.Query!.Trim();
        if (compare.QueryRefusal(sql) is { } why) throw new QuerithmException("compareQuery", $"The {role} query: {why}");
        var (prefix, body) = compare.DerivedQuery(sql);
        var columns = await ReadAsync(s, db, compare, $"{prefix}SELECT * FROM (\n{body}\n) AS q WHERE 1 = 0", async (r, _) =>
        {
            var schema = await r.GetColumnSchemaAsync(ct).ConfigureAwait(false);
            return schema.Select(c => (c.ColumnName, c.DataTypeName ?? "", false, false, false)).ToList();
        }, ct).ConfigureAwait(false);
        var dup = columns.GroupBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1 || string.IsNullOrEmpty(g.Key));
        if (dup != null) throw new QuerithmException("compareQuery", $"The {role} query's columns need distinct names ({(string.IsNullOrEmpty(dup.Key) ? "a column has no name" : dup.Key)}).");
        return new Resolved(s, db, compare, s.Provider.DataEdit, null, null, sql, columns);
    }

    /// <summary>One read on a pool connection of the side's database; read-only where the engine can enforce it.</summary>
    /// <summary>
    /// A connection for one side's read: a pool connection of that database, or — for a session-owned session — the
    /// session's own connection after its execution (Phase D0d).
    /// </summary>
    async Task<(PhysicalConnection Conn, Func<Task> Release)> BorrowForReadAsync(Session s, string db, CancellationToken ct)
    {
        if (s.SessionOwned)
        {
            var lease = await sessions.LeaseAsync(s, ct).ConfigureAwait(false);
            return (lease.Conn, async () => await lease.DisposeAsync().ConfigureAwait(false));
        }
        var pool = sessions.PoolFor(s.Profile, s.Provider, db);
        var conn = await pool.BorrowAsync(db, null, ct).ConfigureAwait(false);
        return (conn, () => pool.ReturnAsync(conn));
    }

    async Task<T> ReadAsync<T>(Session s, string db, IDataCompareDialect d, string sql, Func<DbDataReader, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var (conn, release) = await BorrowForReadAsync(s, db, ct).ConfigureAwait(false);
        var readOnly = false;
        try
        {
            // Read-only where the engine enforces it (PostgreSQL), through the provider's transaction control (Phase D0b).
            readOnly = await s.Provider.Transactions.BeginReadOnlyAsync(conn.Db, ct).ConfigureAwait(false);
            await using var cmd = conn.Db.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            return await read(r, ct).ConfigureAwait(false);
        }
        finally
        {
            if (readOnly) { try { using var cleanup = Cleanup.Start(); await s.Provider.Transactions.RollbackAsync(conn.Db, cleanup.Token).ConfigureAwait(false); } catch { /* connection gone */ } }
            // The pool reads the state: a read transaction that did not end is never handed to the next borrower.
            await release().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------------------------- start

    public async Task<DataCompareStart> StartAsync(DataCompareParams p, CancellationToken ct)
    {
        var src = await ResolveAsync(p.Source, "source", ct).ConfigureAwait(false);
        var tgt = await ResolveAsync(p.Target, "target", ct).ConfigureAwait(false);
        // Both sides stream at once; a session-owned session has one connection, which cannot read both (Phase D0d).
        if (src.Session == tgt.Session && src.Session.SessionOwned)
            throw new QuerithmException("unsupported", "Both sides would need this tab's single connection at once: open the target in another tab and compare across the two.");
        var options = p.Options ?? new DataCompareOptions();
        if (options.MaxDiffRows is < 1 or > 1_000_000) throw QuerithmException.Invalid("Keep between 1 and 1,000,000 differences.");
        var notes = new List<string>();

        // Column pairs: given (each name must exist on its side), else same names (case-insensitive across engines).
        static int Find(Resolved r, string name) => r.Columns.ToList().FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        ColumnPair[] pairs;
        if (p.Columns is { Length: > 0 } given)
        {
            foreach (var c in given)
            {
                if (Find(src, c.Source) < 0) throw QuerithmException.Invalid($"The source has no column {c.Source}.");
                if (Find(tgt, c.Target) < 0) throw QuerithmException.Invalid($"The target has no column {c.Target}.");
            }
            pairs = given.Select(c => new ColumnPair { Source = src.Columns[Find(src, c.Source)].Name, Target = tgt.Columns[Find(tgt, c.Target)].Name }).ToArray();
        }
        else
        {
            pairs = src.Columns.Where(c => Find(tgt, c.Name) >= 0).Select(c => new ColumnPair { Source = c.Name, Target = tgt.Columns[Find(tgt, c.Name)].Name }).ToArray();
            var onlySource = src.Columns.Where(c => Find(tgt, c.Name) < 0).Select(c => c.Name).ToList();
            var onlyTarget = tgt.Columns.Where(c => Find(src, c.Name) < 0).Select(c => c.Name).ToList();
            if (onlySource.Count > 0) notes.Add($"Only in the source, not compared: {string.Join(", ", onlySource)}.");
            if (onlyTarget.Count > 0) notes.Add($"Only in the target, not compared: {string.Join(", ", onlyTarget)}.");
        }
        if (pairs.Length == 0) throw new QuerithmException("compareColumns", "The two sides have no column in common to compare.");

        // Key: given, else the source's primary key, else the target's.
        string[] keySource;
        if (p.Key is { Length: > 0 } k) keySource = k;
        else if (src.Columns.Where(c => c.Pk).Select(c => c.Name).ToArray() is { Length: > 0 } spk) keySource = spk;
        else if (tgt.Columns.Where(c => c.Pk).Select(c => pairs.FirstOrDefault(x => string.Equals(x.Target, c.Name, StringComparison.OrdinalIgnoreCase))?.Source).ToArray() is { Length: > 0 } tpk && tpk.All(x => x != null))
            keySource = tpk!;
        else throw new QuerithmException("compareKey", "Choose the key columns: neither side has a primary key to match rows by.");
        var keyPairs = keySource.Select(name => pairs.FirstOrDefault(x => string.Equals(x.Source, name, StringComparison.OrdinalIgnoreCase))
                                                ?? throw QuerithmException.Invalid($"Key column {name} is not compared on both sides.")).ToArray();
        var valuePairs = pairs.Where(x => !keyPairs.Contains(x)).ToArray();

        string TypeOf(Resolved r, string name) => r.Columns[Find(r, name)].Type;
        var sk = keyPairs.Select(x => src.Compare.KindOf(TypeOf(src, x.Source))).ToArray();
        var tk = keyPairs.Select(x => tgt.Compare.KindOf(TypeOf(tgt, x.Target))).ToArray();
        for (var i = 0; i < keyPairs.Length; i++)
        {
            if (sk[i] == ValueKind.Unordered || tk[i] == ValueKind.Unordered)
                throw new QuerithmException("compareKey", $"{keyPairs[i].Source} cannot be a key: its type has no order both sides agree on.");
            if (KeyComparers.Family(sk[i]) != KeyComparers.Family(tk[i]))
                throw new QuerithmException("compareKey", $"Key column {keyPairs[i].Source} is {TypeOf(src, keyPairs[i].Source)} in the source and {TypeOf(tgt, keyPairs[i].Target)} in the target: they sort differently.");
        }
        var sv = valuePairs.Select(x => src.Compare.KindOf(TypeOf(src, x.Source))).ToArray();
        var tv = valuePairs.Select(x => tgt.Compare.KindOf(TypeOf(tgt, x.Target))).ToArray();
        if (sv.Concat(tv).Contains(ValueKind.Float)) notes.Add($"Floating-point values are equal within a relative {options.FloatEpsilon:g3}.");
        var loose = valuePairs.Where((_, i) => sv[i] == ValueKind.Unordered || tv[i] == ValueKind.Unordered).Select(x => x.Source).ToList();
        if (loose.Count > 0) notes.Add($"Compared as text (formatting differences count): {string.Join(", ", loose)}.");
        notes.Add(src.Compare.IsolationHint);

        var plan = new ComparePlan(keyPairs.Select(x => x.Source).ToArray(), sk, tk, valuePairs.Select(x => x.Source).ToArray(), sv, tv,
            src.Compare.PadsTrailingSpaces || tgt.Compare.PadsTrailingSpaces, options);
        var exec = executions.OpenStream("-- data compare");
        var state = new State(Ulid.New().ToString(), src, tgt, keyPairs, valuePairs, plan, exec);
        _compares[state.Id] = state;
        foreach (var old in _compares.Values.OrderByDescending(x => x.At).Skip(Keep).ToList())
        {
            old.Cts.Cancel();
            _compares.TryRemove(old.Id, out _);
        }
        _ = Task.Run(() => RunAsync(state));
        return new DataCompareStart { CompareId = state.Id, ExecutionId = exec.IdText, Key = keyPairs, Columns = valuePairs, Notes = notes.ToArray() };
    }

    /// <summary>SELECT of one side: every column as the grid's text form, ordered by the key in the compare's order.</summary>
    static string SelectSql(Resolved r, ColumnPair[] key, ColumnPair[] values, bool source)
    {
        var q = r.Edit;
        string Expr(string col) => r.Table != null ? q.Quote(col) : $"q.{q.Quote(col)}";
        string Name(ColumnPair c) => source ? c.Source : c.Target;
        string Type(string col) => r.Columns.First(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase)).Type;
        var list = key.Concat(values).Select((c, i) => $"{q.ValueText(Expr(Name(c)), Type(Name(c)))} AS {q.Quote($"c{i}")}");
        var order = key.Select(c => r.Compare.OrderExpression(Expr(Name(c)), r.Compare.KindOf(Type(Name(c)))));
        var (prefix, body) = r.Table != null ? ("", "") : r.Compare.DerivedQuery(r.Query!);
        var from = r.Table != null ? $"{q.Quote(r.Schema!)}.{q.Quote(r.Table)}" : $"(\n{body}\n) AS q";
        return $"{prefix}SELECT {string.Join(", ", list)} FROM {from} ORDER BY {string.Join(", ", order)}";
    }

    async Task RunAsync(State st)
    {
        var sw = Stopwatch.StartNew();
        var ct = st.Cts.Token;
        var schema = DiffSchema(st);
        var pending = new List<DiffRow>();
        long pendingBytes = 0;
        var lastProgress = 0L;
        void Progress(bool done) => notifier.Notify("dataCompare.progress", new DataCompareProgressEvent
        {
            CompareId = st.Id, SourceRows = st.Counts.SourceRows, TargetRows = st.Counts.TargetRows, Identical = st.Counts.Identical,
            Different = st.Counts.Different, SourceOnly = st.Counts.SourceOnly, TargetOnly = st.Counts.TargetOnly, Done = done,
            Cancelled = done && ct.IsCancellationRequested, Truncated = st.Truncated, Kept = st.Diffs.Count, TruncatedNote = st.TruncatedNote,
            Error = st.Error, ElapsedMs = sw.Elapsed.TotalMilliseconds,
        });
        async ValueTask Flush()
        {
            if (pending.Count == 0) return;
            using var batch = DiffBatch(schema, st, pending);
            await executions.StreamFrameAsync(st.Exec, FrameKind.Batch, ResultSetEncoder.ToIpc(schema, batch), ct).ConfigureAwait(false);
            pending.Clear();
            pendingBytes = 0;
        }
        var state = ExecState.Done;
        try
        {
            await executions.StreamFrameAsync(st.Exec, FrameKind.Schema, ResultSetEncoder.ToIpc(schema, null), ct).ConfigureAwait(false);
            var srcSql = SelectSql(st.Source, st.Key, st.Columns, true);
            var tgtSql = SelectSql(st.Target, st.Key, st.Columns, false);
            // Both readers stay open while the merge pulls from them: two connections, each streaming in key order.
            var targetRows = Channel(st.Target, tgtSql, ct);
            await ReadAsync(st.Source.Session, st.Source.Database, st.Source.Compare, srcSql, async (r, c) =>
            {
                await using var tgt = targetRows.GetAsyncEnumerator(c);
                await using var src = Rows(r, c).GetAsyncEnumerator(c);
                await DataComparer.MergeAsync(src, tgt, st.Plan, st.Counts, async d =>
                {
                    var size = KeptFootprint(d);
                    if (!st.Truncated && st.Diffs.Count < st.Plan.Options.MaxDiffRows && st.KeptBytes + size <= KeptBytesLimit)
                    {
                        st.Diffs.Add(d);
                        st.KeptBytes += size;
                        pending.Add(d);
                        pendingBytes += WireBytes(d);
                        if (pending.Count >= BatchRows || pendingBytes >= ResultSetEncoder.MaxBatchBytes) await Flush().ConfigureAwait(false);
                    }
                    else if (!st.Truncated)
                    {
                        st.Truncated = true;
                        st.TruncatedNote = st.Diffs.Count >= st.Plan.Options.MaxDiffRows
                            ? $"Showing the first {st.Diffs.Count:N0} differences (Keep): raise Keep to see more."
                            : $"Showing the first {st.Diffs.Count:N0} differences: they fill the {KeptBytesLimit / (1024 * 1024.0):0.#} MB one comparison may keep. " +
                              "Compare fewer or shorter columns, or fewer rows, to see the rest.";
                        log.LogInformation("Data compare {Id}: kept {Kept} differences ({Bytes} bytes), the rest are counted only", st.Id, st.Diffs.Count, st.KeptBytes);
                    }
                    var now = sw.ElapsedMilliseconds;
                    if (now - lastProgress >= 250) { lastProgress = now; Progress(false); }
                }, c).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
            await Flush().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            state = ExecState.Cancelled;
        }
        catch (Exception ex)
        {
            state = ExecState.Failed;
            st.Error = ex is QuerithmException ? ex.Message : $"{(ex is DbException ? "The server said: " : "")}{Logging.LogScrubber.Scrub(ex.Message)}";
            log.LogInformation("Data compare {Id} failed: {Message}", st.Id, st.Error);
        }
        finally
        {
            st.Done = true;
            await executions.EndStreamAsync(st.Exec, st.Truncated, state).ConfigureAwait(false);
            Progress(true);
            log.LogInformation("Data compare {Id}: {Rows} source rows, {Diff} different, {S} source only, {T} target only in {Ms} ms",
                st.Id, st.Counts.SourceRows, st.Counts.Different, st.Counts.SourceOnly, st.Counts.TargetOnly, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>The target side as a lazily started stream (it opens its reader when the merge first asks for a row).</summary>
    async IAsyncEnumerable<string?[]> Channel(Resolved r, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (conn, release) = await BorrowForReadAsync(r.Session, r.Database, ct).ConfigureAwait(false);
        var readOnly = false;
        try
        {
            readOnly = await r.Session.Provider.Transactions.BeginReadOnlyAsync(conn.Db, ct).ConfigureAwait(false);
            await using var cmd = conn.Db.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            await foreach (var row in Rows(reader, ct).ConfigureAwait(false)) yield return row;
        }
        finally
        {
            if (readOnly) { try { using var cleanup = Cleanup.Start(); await r.Session.Provider.Transactions.RollbackAsync(conn.Db, cleanup.Token).ConfigureAwait(false); } catch { /* connection gone */ } }
            await release().ConfigureAwait(false);
        }
    }

    static async IAsyncEnumerable<string?[]> Rows(DbDataReader r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var n = r.FieldCount;
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new string?[n];
            for (var i = 0; i < n; i++) row[i] = await r.IsDBNullAsync(i, ct).ConfigureAwait(false) ? null : r.GetString(i);
            yield return row;
        }
    }

    // ---------------------------------------------------------------------------------------------- diff frames

    static Dictionary<string, string> Meta(params (string Key, string Value)[] items) => items.ToDictionary(x => x.Key, x => x.Value);

    /// <summary>diff (≠ + −), hidden _diff_kind (u8) and _diff_mask (bitmap over compared columns), keys, then each column as source / target.</summary>
    static Schema DiffSchema(State st)
    {
        string TypeOf(Resolved r, string name) => r.Columns.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).Type;
        var fields = new List<Field>
        {
            new("diff", StringType.Default, false, Meta(("q:typeName", "diff"), ("q:ordinal", "0"))),
            new("_diff_kind", UInt8Type.Default, false, Meta(("q:hidden", "1"))),
            new("_diff_mask", BinaryType.Default, false, Meta(("q:hidden", "1"))),
        };
        var ordinal = 1;
        foreach (var k in st.Key)
            fields.Add(new Field(k.Source, StringType.Default, true, Meta(("q:typeName", TypeOf(st.Source, k.Source)), ("q:ordinal", (ordinal++).ToString()), ("q:pk", "1"), ("q:diffKey", "1"))));
        for (var i = 0; i < st.Columns.Length; i++)
        {
            var c = st.Columns[i];
            fields.Add(new Field(c.Source, StringType.Default, true, Meta(("q:typeName", TypeOf(st.Source, c.Source)), ("q:ordinal", (ordinal++).ToString()), ("q:diffBit", i.ToString()), ("q:diffSide", "source"))));
            fields.Add(new Field($"{c.Source}__target", StringType.Default, true, Meta(("q:typeName", TypeOf(st.Target, c.Target)), ("q:ordinal", (ordinal++).ToString()), ("q:diffBit", i.ToString()), ("q:diffSide", "target"))));
        }
        return new Schema(fields, null);
    }

    static RecordBatch DiffBatch(Schema schema, State st, List<DiffRow> rows)
    {
        var kindText = new StringArray.Builder();
        var kind = new UInt8Array.Builder();
        var mask = new BinaryArray.Builder();
        var keys = st.Key.Select(_ => new StringArray.Builder()).ToArray();
        var values = st.Columns.Select(_ => (new StringArray.Builder(), new StringArray.Builder())).ToArray();
        void Add(StringArray.Builder b, string? v) { if (v == null) b.AppendNull(); else b.Append(v); }
        foreach (var d in rows)
        {
            kindText.Append(d.Kind switch { DiffKind.Different => "≠", DiffKind.SourceOnly => "+ source", _ => "− target" });
            kind.Append((byte)d.Kind);
            mask.Append(d.Mask.AsSpan());
            var keyRow = d.Source ?? d.Target!;
            for (var i = 0; i < keys.Length; i++) Add(keys[i], keyRow[i]);
            for (var i = 0; i < values.Length; i++)
            {
                Add(values[i].Item1, d.Source?[st.Key.Length + i]);
                Add(values[i].Item2, d.Target?[st.Key.Length + i]);
            }
        }
        var arrays = new List<IArrowArray> { kindText.Build(), kind.Build(), mask.Build() };
        arrays.AddRange(keys.Select(k => (IArrowArray)k.Build()));
        foreach (var (s, t) in values) { arrays.Add(s.Build()); arrays.Add(t.Build()); }
        return new RecordBatch(schema, arrays, rows.Count);
    }

    // ---------------------------------------------------------------------------------------------- cancel / sync

    State Get(string id) => _compares.TryGetValue(id, out var s) ? s : throw new QuerithmException("compareGone", "This comparison is no longer available. Compare again.");

    public CancelResult Cancel(DataCompareRef p)
    {
        var st = Get(p.CompareId);
        if (st.Done) return new CancelResult { Cancelled = false };
        st.Cts.Cancel();
        return new CancelResult { Cancelled = true };
    }

    /// <summary>INSERT / UPDATE / DELETE for the kept differences, with literals, in one transaction; never executed here.</summary>
    public DataSyncSqlResult SyncSql(DataSyncSqlParams p)
    {
        var st = Get(p.CompareId);
        if (!st.Done) throw new QuerithmException("compareRunning", "The compare is still running: wait for it to finish.");
        if (st.Error != null) throw new QuerithmException("compareFailed", "The compare failed, so there is nothing to sync.");
        var t = st.Target;
        FeatureGuard.Require(t.Session.Provider, Feature.DataCompareApply);
        if (t.Table == null) throw new QuerithmException("compareTarget", "The target is a query: a sync script needs a table to write to.");
        var e = t.Edit;
        var table = $"{e.Quote(t.Schema!)}.{e.Quote(t.Table)}";
        var col = t.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        bool Writable(string name, bool insert)
        {
            var c = col[name];
            if (c.Computed) return false;
            if (c.Identity && !insert) return false;
            return e.LockReason(new SnapColumn { Name = c.Name, Type = c.Type }) == null;
        }
        string Lit(string name, string? text) => e.PreviewLiteral(col[name].Type, text == null ? new GridValue { IsNull = true } : new GridValue { Text = text });
        string Where(string?[] row) =>
            string.Join(" AND ", st.Key.Select((k, i) => $"{e.Quote(k.Target)} = {Lit(k.Target, row[i])}"));
        var term = e.PreviewTerminator;
        var body = new StringBuilder();
        var statements = 0;
        long rows = 0;
        var kept = st.Diffs;

        if (p.Delete)
            foreach (var d in kept.Where(d => d.Kind == DiffKind.TargetOnly))
            {
                body.Append($"DELETE FROM {table} WHERE {Where(d.Target!)}{term}\n");
                statements++;
                rows++;
            }
        if (p.Update)
        {
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in kept.Where(d => d.Kind == DiffKind.Different))
            {
                var sets = new List<string>();
                for (var i = 0; i < st.Columns.Length; i++)
                {
                    if (!d.Differs(i)) continue;
                    var target = st.Columns[i].Target;
                    if (!Writable(target, false)) { skipped.Add(target); continue; }
                    sets.Add($"{e.Quote(target)} = {Lit(target, d.Source![st.Key.Length + i])}");
                }
                if (sets.Count == 0) continue;
                body.Append($"UPDATE {table} SET {string.Join(", ", sets)} WHERE {Where(d.Source!)}{term}\n");
                statements++;
                rows++;
            }
            if (skipped.Count > 0) notes.Add($"Not written (computed, identity or set by the server): {string.Join(", ", skipped)}.");
        }
        if (p.Insert)
        {
            var inserts = kept.Where(d => d.Kind == DiffKind.SourceOnly).ToList();
            var cols = st.Key.Concat(st.Columns).Select((c, i) => (c.Target, Index: i)).Where(c => Writable(c.Target, true)).ToList();
            if (inserts.Count > 0 && cols.Count > 0)
            {
                var identity = cols.Any(c => col[c.Target].Identity);
                var wrap = identity ? t.Compare.IdentityInsert(table) : null;
                if (wrap is { } w1) body.Append(w1.On).Append('\n');
                var overriding = identity && t.Compare.InsertOverriding is { } o ? $" {o}" : "";
                foreach (var chunk in inserts.Chunk(t.Compare.InsertBatch))
                {
                    body.Append($"INSERT INTO {table} ({string.Join(", ", cols.Select(c => e.Quote(c.Target)))}){overriding} VALUES\n");
                    body.Append(string.Join(",\n", chunk.Select(d => "  (" + string.Join(", ", cols.Select(c => Lit(c.Target, d.Source![c.Index]))) + ")")));
                    body.Append(term).Append('\n');
                    statements++;
                }
                rows += inserts.Count;
                if (wrap is { } w2) body.Append(w2.Off).Append('\n');
                if (identity) notes.Add("Identity values are copied from the source.");
            }
        }

        var (begin, commit) = t.Compare.SyncTransaction;
        var header = new StringBuilder()
            .Append("-- Querithm data sync\n")
            .Append($"-- Source: {CommentText.Safe(st.Source.Label)}\n")
            .Append($"-- Target: {CommentText.Safe(t.Label)}\n")
            .Append($"-- {st.Counts.Different} different, {st.Counts.SourceOnly} only in the source, {st.Counts.TargetOnly} only in the target.\n");
        if (st.Truncated)
        {
            var msg = $"Covers the first {kept.Count:N0} differences only (the compare kept that many): run the compare again after applying it.";
            header.Append($"-- {msg}\n");
            notes.Add(msg);
        }
        header.Append("-- One transaction: if any statement fails, nothing is changed.\n\n");
        var sql = statements == 0 ? header + "-- Nothing to change.\n" : $"{header}{begin}\n{body}{commit}\n";
        return new DataSyncSqlResult { Sql = sql, Statements = statements, Rows = rows, Notes = notes.ToArray() };
    }
}
