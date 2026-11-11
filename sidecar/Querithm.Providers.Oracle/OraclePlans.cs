using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Oracle plans (D4 S09), per SQL statement of the text (<see cref="OracleSplitter"/>):
/// <list type="bullet">
/// <item>Estimated: <c>EXPLAIN PLAN SET STATEMENT_ID = 'Q…' FOR stmt</c>, then that statement id's rows of <c>PLAN_TABLE</c>
/// (since 10g a public synonym for <c>SYS.PLAN_TABLE$</c>, a global temporary table with session duration — measured on the
/// Free fixture: rows are the session's own). The rows are always deleted again, within <see cref="Cleanup.Limit"/>. The
/// insert and delete are DML on that temporary table: outside a transaction each commits on its own (autocommit), inside the
/// user's open transaction they are part of it and leave nothing behind. Nothing runs.</item>
/// <item>Actual: the statement with a <c>GATHER_PLAN_STATISTICS</c> hint (added on tokens, merged into a hint already there)
/// runs and its rows are read to the end; then <c>V$SESSION.PREV_SQL_ID / PREV_CHILD_NUMBER</c> name its cursor and
/// <c>V$SQL_PLAN_STATISTICS_ALL</c> gives the plan with the last run's statistics — what <c>DBMS_XPLAN.DISPLAY_CURSOR(NULL,
/// NULL, 'ALLSTATS LAST')</c> formats as text, read as columns instead (ARCHITECTURE §16). Core runs it inside a transaction
/// it rolls back (a savepoint inside the user's), after the query guard. Both views need a grant: without one the plan
/// refuses with the reason before the statement runs.</item>
/// </list>
/// Raw is <c>[{text, actual, operations: [{id, parentId, operation, …}]}]</c>, format "oracle-json", which plan.parse reads back.
/// </summary>
public sealed class OraclePlans : IPlanProvider
{
    public const string Format = "oracle-json";

    /// <summary>Most operations one statement's plan may hold, and the deepest operator nesting (the IPC JSON limit, as for DuckDB).</summary>
    internal const int MaxOperations = 100_000;
    internal const int MaxDepth = 256;

    internal const string NoPlanTable =
        "Oracle has no PLAN_TABLE this login can use (ORA-00942), so there is no estimated plan. Ask the DBA to restore the PUBLIC.PLAN_TABLE synonym (Oracle creates it with the database), or run ?/rdbms/admin/utlxplan.sql in your schema.";

    internal const string NoStatistics =
        "An actual plan reads the statement's run statistics from V$SESSION and V$SQL_PLAN_STATISTICS_ALL, which this login cannot read. Ask the DBA for SELECT on both views (or SELECT_CATALOG_ROLE), or use the estimated plan.";

    static readonly HashSet<string> Plannable = new(StringComparer.Ordinal) { "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE" };

    /// <summary>The SQL statements of the text, as the driver takes them (no ';').</summary>
    static List<(OracleStatement Statement, string Text)> Statements(string sql) =>
        OracleSplitter.Split(sql).Where(st => st.Kind != OracleStatementKind.ClientCommand).Select(st => (st, sql[st.Start..st.End])).ToList();

    public string? Refusal(string sql, PlanMode mode)
    {
        var actual = mode == PlanMode.Actual;
        var count = 0;
        foreach (var st in OracleSplitter.Split(sql ?? ""))
        {
            if (st.Kind == OracleStatementKind.ClientCommand)
                return $"{st.Command!.Name} on line {st.StartLine} is a SQL*Plus command, and a plan covers SQL statements only. Remove the line and ask for the plan again.";
            count++;
            var where = $"The statement on line {st.StartLine}";
            if (st.Kind == OracleStatementKind.PlSql)
                return $"{where} is a PL/SQL unit, which has no plan of its own. Ask for the plan of the SQL statements inside it on their own.";
            var code = st.Code.ToList();
            var verb = OracleLanguageService.Verb(code);
            if (Plannable.Contains(verb))
            {
                if (actual && OracleLanguageService.Binds(sql![st.Start..st.End]) is { Count: > 0 } binds)
                    return $"{where} uses bind variables ({string.Join(", ", binds.Select(b => ":" + b))}), which an actual plan cannot fill in. Use the estimated plan, or write values in their place.";
                continue;
            }
            if (Ddl(code))
            {
                if (actual) return $"{where} is DDL ({verb}), which Oracle commits before and after it runs, so its actual plan could not be rolled back. The estimated plan is available.";
                continue;
            }
            return $"{where} ({verb}) has no plan in Oracle: plans cover SELECT, INSERT, UPDATE, DELETE and MERGE{(actual ? "" : ", and CREATE TABLE … AS SELECT / CREATE INDEX")}.";
        }
        return count == 0 ? "There is no SQL statement to plan." : null;
    }

    /// <summary>The DDL EXPLAIN PLAN accepts: CREATE TABLE … AS SELECT and CREATE [UNIQUE | BITMAP] INDEX.</summary>
    static bool Ddl(List<OracleToken> code)
    {
        if (code.Count < 3 || !code[0].Is("CREATE")) return false;
        var i = 1;
        if (code[i].Is("UNIQUE") || code[i].Is("BITMAP")) return code[i + 1].Is("INDEX");
        if (code[i].Is("INDEX")) return true;
        if (code[i].Is("GLOBAL") || code[i].Is("PRIVATE")) i += 2; // GLOBAL / PRIVATE TEMPORARY TABLE
        return i < code.Count && code[i].Is("TABLE") && OracleLanguageService.HasTopLevel(code, "CREATE", "AS");
    }

    public async Task<PlanResult> GetPlanAsync(DbConnection connection, string sql, PlanMode mode, CancellationToken ct)
    {
        var actual = mode == PlanMode.Actual;
        if (Refusal(sql, mode) is { } refusal) throw new NotSupportedException(refusal);
        // Before anything runs: a missing grant must not cost the user a run (and its rollback).
        await ProbeAsync(connection, actual ? "SELECT 1 FROM V$SESSION s, V$SQL_PLAN_STATISTICS_ALL p WHERE 1 = 0" : "SELECT 1 FROM PLAN_TABLE WHERE 1 = 0",
            actual ? NoStatistics : NoPlanTable, ct).ConfigureAwait(false);
        var statements = new List<(string Text, List<Operation> Rows)>();
        foreach (var (_, text) in Statements(sql))
            statements.Add((text, actual ? await ActualAsync(connection, text, ct).ConfigureAwait(false) : await EstimatedAsync(connection, text, ct).ConfigureAwait(false)));
        var raw = Write(statements, actual);
        return new PlanResult { Plan = Parse(raw), Raw = raw, Format = Format };
    }

    static async Task ProbeAsync(DbConnection connection, string sql, string reason, CancellationToken ct)
    {
        await using var cmd = Command(connection, sql);
        try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        catch (OracleException ex) when (ex.Number is 942 or 1031 or 41900) { throw new NotSupportedException(reason, ex); }
    }

    static OracleCommand Command(DbConnection connection, string sql)
    {
        var cmd = (OracleCommand)connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        cmd.FetchSize = OracleProvider.FetchSizeBytes;
        return cmd;
    }

    /// <summary>The columns PLAN_TABLE and V$SQL_PLAN_STATISTICS_ALL share, in <see cref="Read"/>'s order.</summary>
    const string Columns =
        "id, parent_id, operation, options, object_owner, object_name, object_alias, object_type, optimizer, cost, cardinality, bytes, cpu_cost, io_cost, time, " +
        "partition_start, partition_stop, access_predicates, filter_predicates, qblock_name";

    async Task<List<Operation>> EstimatedAsync(DbConnection connection, string text, CancellationToken ct)
    {
        // Ours alone (statement_id is VARCHAR2(30)); a constant of Querithm's, never text from the request.
        var id = "Q" + Guid.NewGuid().ToString("N")[..28];
        try
        {
            await using (var explain = Command(connection, $"EXPLAIN PLAN SET STATEMENT_ID = '{id}' FOR {text}"))
                await explain.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using var read = Command(connection, $"SELECT {Columns} FROM PLAN_TABLE WHERE statement_id = :id ORDER BY id");
            read.Parameters.Add(new OracleParameter("id", id));
            return await Read(read, false, ct).ConfigureAwait(false);
        }
        finally
        {
            // Bounded, and whatever ended the plan (a cancel included): the session's temporary rows go.
            using var limit = Cleanup.Start();
            try
            {
                await using var delete = Command(connection, "DELETE FROM PLAN_TABLE WHERE statement_id = :id");
                delete.Parameters.Add(new OracleParameter("id", id));
                await delete.ExecuteNonQueryAsync(limit.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!limit.IsCancellationRequested) { /* the rows are the session's own and end with it */ }
        }
    }

    async Task<List<Operation>> ActualAsync(DbConnection connection, string text, CancellationToken ct)
    {
        await using (var run = Command(connection, Hinted(text)))
        {
            run.InitialLONGFetchSize = 0;
            await using var r = await run.ExecuteReaderAsync(ct).ConfigureAwait(false);
            // Every row: the statistics are the last run's, so a run stopped early would show part of it.
            while (await r.ReadAsync(ct).ConfigureAwait(false)) { }
        }
        string? sqlId;
        long child;
        await using (var who = Command(connection, "SELECT prev_sql_id, prev_child_number FROM V$SESSION WHERE sid = SYS_CONTEXT('USERENV', 'SID')"))
        await using (var r = await who.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false) || r.IsDBNull(0)) throw new NotSupportedException("Oracle did not say which cursor the statement ran in, so its actual plan cannot be read.");
            sqlId = r.GetString(0);
            child = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
        }
        await using var read = Command(connection,
            $"SELECT {Columns}, last_starts, last_output_rows, last_elapsed_time, last_cr_buffer_gets, last_cu_buffer_gets, last_disk_reads, last_tempseg_size, last_memory_used " +
            "FROM V$SQL_PLAN_STATISTICS_ALL WHERE sql_id = :s AND child_number = :c ORDER BY id");
        read.Parameters.Add(new OracleParameter("s", sqlId));
        read.Parameters.Add(new OracleParameter("c", child));
        var rows = await Read(read, true, ct).ConfigureAwait(false);
        if (rows.Count == 0) throw new NotSupportedException("Oracle no longer holds the statement's cursor (it was aged out of the shared pool), so its actual plan cannot be read. Ask for it again.");
        return rows;
    }

    /// <summary>
    /// The statement with <c>GATHER_PLAN_STATISTICS</c> right after its verb (the main query's SELECT after a WITH clause),
    /// merged into the hint comment already there — Oracle reads only the first one. Tokens, never text search.
    /// </summary>
    internal static string Hinted(string text)
    {
        var tokens = OracleTokenizer.Tokenize(text);
        var verb = VerbIndex(tokens);
        if (verb < 0) return text;
        for (var i = verb + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == OracleTokenKind.Hint)
            {
                var at = t.Offset + 3; // after "/*+" or "--+"
                return text[..at] + " GATHER_PLAN_STATISTICS" + text[at..];
            }
            if (!t.IsTrivia) break;
        }
        var end = tokens[verb].End;
        return text[..end] + " /*+ GATHER_PLAN_STATISTICS */" + text[end..];
    }

    static int VerbIndex(List<OracleToken> tokens)
    {
        var first = tokens.FindIndex(t => !t.IsTrivia);
        if (first < 0) return -1;
        if (!tokens[first].Is("WITH")) return first;
        var depth = 0;
        for (var i = first + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsP("(")) depth++;
            else if (t.IsP(")")) depth--;
            else if (depth == 0 && t.Kind == OracleTokenKind.Word && Plannable.Contains(t.Text.ToUpperInvariant())) return i;
        }
        return -1;
    }

    // ---------------------------------------------------------------------------------------------- rows ⇄ raw JSON

    internal sealed record Operation(
        int Id, int? ParentId, string Operation_, string? Options, string? ObjectOwner, string? ObjectName, string? ObjectAlias, string? ObjectType,
        string? Optimizer, double? Cost, double? Cardinality, double? Bytes, double? CpuCost, double? IoCost, double? Time,
        string? PartitionStart, string? PartitionStop, string? AccessPredicates, string? FilterPredicates, string? QueryBlock,
        double? Starts = null, double? OutputRows = null, double? ElapsedUs = null, double? CrGets = null, double? CuGets = null,
        double? DiskReads = null, double? TempBytes = null, double? MemoryBytes = null);

    static async Task<List<Operation>> Read(OracleCommand cmd, bool actual, CancellationToken ct)
    {
        var rows = new List<Operation>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        double? D(int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count >= MaxOperations) throw new NotSupportedException($"The plan has more than {MaxOperations:N0} operations, more than Querithm shows.");
            rows.Add(new Operation(
                (int)D(0)!.Value, D(1) is { } p ? (int)p : null, S(2) ?? "?", S(3), S(4), S(5), S(6), S(7), S(8), D(9), D(10), D(11), D(12), D(13), D(14),
                S(15), S(16), S(17), S(18), S(19),
                actual ? D(20) : null, actual ? D(21) : null, actual ? D(22) : null, actual ? D(23) : null, actual ? D(24) : null,
                actual ? D(25) : null, actual ? D(26) : null, actual ? D(27) : null));
        }
        return rows;
    }

    internal static string Write(IEnumerable<(string Text, List<Operation> Rows)> statements, bool actual)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var (text, rows) in statements)
            {
                w.WriteStartObject();
                w.WriteString("text", text);
                w.WriteBoolean("actual", actual);
                w.WriteStartArray("operations");
                foreach (var o in rows)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", o.Id);
                    if (o.ParentId is { } p) w.WriteNumber("parentId", p);
                    w.WriteString("operation", o.Operation_);
                    Str(w, "options", o.Options); Str(w, "objectOwner", o.ObjectOwner); Str(w, "objectName", o.ObjectName);
                    Str(w, "objectAlias", o.ObjectAlias); Str(w, "objectType", o.ObjectType); Str(w, "optimizer", o.Optimizer);
                    Num(w, "cost", o.Cost); Num(w, "cardinality", o.Cardinality); Num(w, "bytes", o.Bytes); Num(w, "cpuCost", o.CpuCost);
                    Num(w, "ioCost", o.IoCost); Num(w, "time", o.Time);
                    Str(w, "partitionStart", o.PartitionStart); Str(w, "partitionStop", o.PartitionStop);
                    Str(w, "accessPredicates", o.AccessPredicates); Str(w, "filterPredicates", o.FilterPredicates); Str(w, "queryBlock", o.QueryBlock);
                    Num(w, "starts", o.Starts); Num(w, "outputRows", o.OutputRows); Num(w, "elapsedUs", o.ElapsedUs); Num(w, "crGets", o.CrGets);
                    Num(w, "cuGets", o.CuGets); Num(w, "diskReads", o.DiskReads); Num(w, "tempBytes", o.TempBytes); Num(w, "memoryBytes", o.MemoryBytes);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static void Str(Utf8JsonWriter w, string name, string? value) { if (value != null) w.WriteString(name, value); }
    static void Num(Utf8JsonWriter w, string name, double? value) { if (value is { } v && double.IsFinite(v)) w.WriteNumber(name, v); }

    /// <summary>
    /// Reads the raw JSON back (also what plan.parse gets for Oracle). Untrusted: every operation names a parent listed
    /// before it (Oracle numbers a parent below its children), one root, unique ids, at most <see cref="MaxOperations"/>
    /// operations nested at most <see cref="MaxDepth"/> deep; anything else is refused. The tree is built from the leaves
    /// up, so no recursion follows the text's nesting.
    /// </summary>
    public PlanTree Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
        var statements = new List<PlanStatement>();
        foreach (var st in doc.RootElement.EnumerateArray())
        {
            var ops = st.GetProperty("operations");
            if (ops.GetArrayLength() > MaxOperations) throw new InvalidOperationException($"The plan has more than {MaxOperations} operations.");
            var root = Tree(ops.EnumerateArray().Select(ReadOperation).ToList());
            statements.Add(new PlanStatement
            {
                Text = st.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                Root = root, SubtreeCost = root?.SubtreeCost, ElapsedMs = root?.ElapsedMs, MissingIndexes = [], Warnings = [],
            });
        }
        return new PlanTree { Statements = statements.ToArray() };
    }

    static Operation ReadOperation(JsonElement e)
    {
        string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? D(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var id = e.GetProperty("id").GetInt32();
        int? parent = e.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
        return new Operation(id, parent, S("operation") ?? throw new InvalidOperationException("An operation has no name."), S("options"), S("objectOwner"),
            S("objectName"), S("objectAlias"), S("objectType"), S("optimizer"), D("cost"), D("cardinality"), D("bytes"), D("cpuCost"), D("ioCost"), D("time"),
            S("partitionStart"), S("partitionStop"), S("accessPredicates"), S("filterPredicates"), S("queryBlock"),
            D("starts"), D("outputRows"), D("elapsedUs"), D("crGets"), D("cuGets"), D("diskReads"), D("tempBytes"), D("memoryBytes"));
    }

    static PlanNode? Tree(List<Operation> rows)
    {
        if (rows.Count == 0) return null;
        var index = new Dictionary<int, int>();
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id < 0 || !index.TryAdd(rows[i].Id, i)) throw new InvalidOperationException($"Operation id {rows[i].Id} is negative or listed twice.");
        var depth = new int[rows.Count];
        var parentOf = new int[rows.Count];
        var root = -1;
        // In id order a parent comes first; the depth check then holds without following the text's nesting.
        var order = Enumerable.Range(0, rows.Count).OrderBy(i => rows[i].Id).ToArray();
        foreach (var i in order)
        {
            if (rows[i].ParentId is not { } p)
            {
                if (root >= 0) throw new InvalidOperationException("The plan has more than one root operation.");
                root = i;
                parentOf[i] = -1;
                continue;
            }
            if (!index.TryGetValue(p, out var pi) || rows[pi].Id >= rows[i].Id) throw new InvalidOperationException($"Operation {rows[i].Id} names parent {p}, which is not listed before it.");
            parentOf[i] = pi;
            depth[i] = depth[pi] + 1;
            if (depth[i] > MaxDepth) throw new InvalidOperationException($"The plan nests deeper than {MaxDepth} operations.");
        }
        if (root < 0) throw new InvalidOperationException("The plan has no root operation.");
        var children = new List<int>[rows.Count];
        foreach (var i in order) if (parentOf[i] >= 0) (children[parentOf[i]] ??= []).Add(i);
        var rootCost = rows[root].Cost;
        var built = new PlanNode?[rows.Count];
        for (var n = order.Length - 1; n >= 0; n--)
        {
            var i = order[n];
            var kids = (children[i] ?? []).Select(k => built[k]!).ToArray();
            built[i] = Node(rows[i], kids, rootCost);
            foreach (var k in children[i] ?? []) built[k] = null;
        }
        // An operation outside the root's tree (its own parent chain never reaches the root) cannot be: every chain ends at the root.
        return built[root];
    }

    static PlanNode Node(Operation o, PlanNode[] children, double? rootCost)
    {
        var name = string.IsNullOrEmpty(o.Options) ? o.Operation_ : $"{o.Operation_} {o.Options}";
        var subtree = o.Cost ?? 0;
        var own = Math.Max(0, subtree - children.Sum(c => c.SubtreeCost));
        var props = new Dictionary<string, string>();
        void P(string key, string? value) { if (!string.IsNullOrEmpty(value)) props[key] = value; }
        string? N(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture);
        P("Access predicates", o.AccessPredicates);
        P("Filter predicates", o.FilterPredicates);
        P("Object type", o.ObjectType);
        P("Object alias", o.ObjectAlias);
        P("Query block", o.QueryBlock);
        P("Optimizer", o.Optimizer);
        P("Bytes", N(o.Bytes));
        P("CPU cost", N(o.CpuCost));
        P("I/O cost", N(o.IoCost));
        P("Time (s)", N(o.Time));
        P("Partition start", o.PartitionStart);
        P("Partition stop", o.PartitionStop);
        P("Disk reads", N(o.DiskReads));
        P("Memory used (bytes)", N(o.MemoryBytes));
        P("Temp space used (bytes)", N(o.TempBytes));
        var warnings = new List<PlanWarning>();
        if (o.TempBytes is > 0) warnings.Add(new PlanWarning { Kind = "spill", Message = $"Spilled {o.TempBytes.Value:N0} bytes to temporary space." });
        if (o.Operation_ == "MERGE JOIN" && o.Options == "CARTESIAN") warnings.Add(new PlanWarning { Kind = "noJoinPredicate", Message = "Cartesian join: no join condition between these inputs." });
        // Oracle's E-Rows is per start; A-Rows is the total of every start.
        if (o.Cardinality is { } est && o.OutputRows is { } got && o.Starts is > 0)
        {
            var perStart = got / o.Starts.Value;
            var hi = Math.Max(est, perStart);
            var lo = Math.Max(1, Math.Min(est, perStart));
            if (hi >= 100 && hi / lo >= 10)
                warnings.Add(new PlanWarning { Kind = "cardinality", Message = $"Estimated {est:N0} rows per start, got {perStart:N0}." });
        }
        return new PlanNode
        {
            Id = o.Id.ToString(CultureInfo.InvariantCulture), Operator = name, PhysicalOp = name, LogicalOp = o.Operation_,
            ObjectName = o.ObjectName == null ? null : o.ObjectOwner == null ? o.ObjectName : $"{o.ObjectOwner}.{o.ObjectName}",
            EstRows = o.Cardinality ?? 0, ActualRows = o.OutputRows, ActualExecutions = o.Starts,
            EstCost = own, SubtreeCost = subtree, CostPercent = rootCost is > 0 ? own / rootCost.Value * 100 : null,
            ElapsedMs = o.ElapsedUs / 1000, LogicalReads = o.CrGets is null && o.CuGets is null ? null : (long)((o.CrGets ?? 0) + (o.CuGets ?? 0)),
            Warnings = warnings.ToArray(), Properties = props, Children = children,
        };
    }
}
