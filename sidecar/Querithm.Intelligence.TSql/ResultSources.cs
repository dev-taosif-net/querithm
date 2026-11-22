using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql.Completion;

namespace Querithm.Intelligence.TSql;

/// <summary>One predicted output column: its name (null for an unnamed expression, not checked) and source.</summary>
public sealed record PredictedColumn(string? Name, ColumnOrigin? Origin);

/// <summary>
/// One data result set as <see cref="ResultSources.Trace"/> read it. <see cref="Columns"/>: null when its columns could not
/// be predicted. <see cref="Why"/>: the reason in words for the user when a table's columns could not be traced.
/// <see cref="Stale"/>: the query names a table the snapshot does not hold, so newer metadata may trace it.
/// <see cref="Tables"/>: the snapshot tables the query reads, for messages.
/// </summary>
public sealed record TracedSet(PredictedColumn[]? Columns, string? Why = null, bool Stale = false, string? Tables = null);

/// <summary>
/// Predicts, from the ScriptDom AST, which table column feeds each column of each data result set of a batch.
/// SQL Server reports no base table without browse mode, which would change how the user's query runs.
/// Conservative by design, and positional: the result sets of the statements before the first one whose results cannot be
/// counted (EXEC, IF or WHILE around a SELECT, TRY, DBCC, ...) are predicted, the ones after it are not, and an ambiguous
/// column gets no origin. Callers verify the count and the names against the real result.
/// </summary>
public static class ResultSources
{
    /// <summary>A data result set's prediction, and why it cannot be edited whatever its columns say (null = no objection).</summary>
    sealed record Set(PredictedColumn[]? Columns, string? Refusal, string? Why = null, bool Stale = false, string? Tables = null);

    /// <summary>
    /// The result sets of a batch in order. <see cref="Open"/>: the statement after which the batch's further result sets
    /// cannot be counted (its first keyword), or null when every statement was accounted for.
    /// </summary>
    sealed record Batch(List<Set> Sets, string? Open);

    sealed record Entry(string Text, SchemaSnapshot Snapshot, Batch? Result);

    static volatile Entry? _last;

    /// <summary>
    /// Per data result set (null = exists but unpredictable), up to the first statement whose results cannot be counted;
    /// null when the batch does not parse.
    /// </summary>
    public static List<PredictedColumn[]?>? Predict(string batchText, SchemaSnapshot snapshot) =>
        Sets(batchText, snapshot)?.Sets.Select(s => s.Columns).ToList();

    /// <summary>
    /// Why data result set <paramref name="resultInBatch"/> cannot be edited even where its columns trace to a table: the
    /// same table is read twice (a self-join: both aliases carry one object id, so a row key could match another row than
    /// the one shown), it combines several queries (UNION, EXCEPT, INTERSECT), or an unqualified table name exists in more
    /// than one schema (SQL Server resolves it through the login's default schema, which the snapshot does not know), or it
    /// reads only tables of another database or a linked server (the snapshot, and Apply, are the tab's database).
    /// </summary>
    public static string? Refusal(string batchText, SchemaSnapshot snapshot, int resultInBatch) =>
        Sets(batchText, snapshot) is { } batch && resultInBatch >= 0 && resultInBatch < batch.Sets.Count ? batch.Sets[resultInBatch].Refusal : null;

    /// <summary>
    /// Data result set <paramref name="resultInBatch"/> of the batch: its predicted columns, or why there are none — in
    /// words for the user, because "run the SELECT on its own" is something they can do.
    /// </summary>
    public static TracedSet Trace(string batchText, SchemaSnapshot snapshot, int resultInBatch)
    {
        if (Sets(batchText, snapshot) is not { } batch)
            return new TracedSet(null, "Querithm could not parse this batch, so it cannot tell which table the rows come from — run the SELECT on its own to edit its rows");
        if (resultInBatch >= 0 && resultInBatch < batch.Sets.Count)
        {
            var s = batch.Sets[resultInBatch];
            return new TracedSet(s.Columns, s.Why, s.Stale, s.Tables);
        }
        return new TracedSet(null, batch.Open is { } keyword
            ? $"this result comes after {keyword} in the same batch, so Querithm cannot tell which statement returned it — run the SELECT on its own to edit its rows"
            : "the batch returned more result sets than its statements account for (a trigger may return rows) — run the SELECT on its own to edit its rows");
    }

    static Batch? Sets(string batchText, SchemaSnapshot snapshot)
    {
        if (_last is { } e && ReferenceEquals(e.Snapshot, snapshot) && e.Text == batchText) return e.Result;
        var result = Compute(batchText, snapshot);
        _last = new Entry(batchText, snapshot, result);
        return result;
    }

    static Batch? Compute(string text, SchemaSnapshot snapshot)
    {
        if (SchemaIndex.For(snapshot) is not { } index) return null;
        if (TSqlParsing.NewParser().Parse(new StringReader(text), out var errors) is not TSqlScript script || errors.Count > 0) return null;
        var walk = new Walk(index, snapshot.Database);
        foreach (var batch in script.Batches)
            if (!walk.Run(batch.Statements)) break;
        return new Batch(walk.Sets, walk.Open);
    }

    /// <summary>Walks a batch's statements in execution order, adding one <see cref="Set"/> per data result set.</summary>
    sealed class Walk(SchemaIndex index, string? database)
    {
        public List<Set> Sets { get; } = [];
        /// <summary>The first keyword of the statement that ended the count; null while every statement is accounted for.</summary>
        public string? Open { get; private set; }

        /// <summary>False once the walk must stop: the count is open, or the batch returns (RETURN) at this point.</summary>
        public bool Run(IList<TSqlStatement> statements)
        {
            foreach (var stmt in statements)
                if (!Step(stmt)) return false;
            return true;
        }

        bool Step(TSqlStatement stmt)
        {
            switch (stmt)
            {
                case SelectStatement s:
                    if (s.Into != null) return true;
                    if (s.QueryExpression is QuerySpecification qs && s.QueryExpression.ForClause == null)
                    {
                        if (qs.SelectElements.Any(x => x is SelectSetVariable)) return true; // SELECT @x = ...: no rows
                        Sets.Add(Project(qs, CteNames(s), index, database));
                    }
                    // UNION, FOR XML/JSON: one result set, columns not traced
                    else if (Combined(s.QueryExpression) is { } op) Sets.Add(new Set(null, $"the query combines several queries ({op})"));
                    else Sets.Add(new Set(null, null, "FOR XML / FOR JSON / FOR BROWSE output and parenthesised queries are not traced to a table"));
                    return true;
                case InsertStatement i when i.InsertSpecification.OutputClause == null:
                case UpdateStatement u when u.UpdateSpecification.OutputClause == null:
                case DeleteStatement d when d.DeleteSpecification.OutputClause == null:
                case MergeStatement m when m.MergeSpecification.OutputClause == null:
                    return true;
                case InsertStatement or UpdateStatement or DeleteStatement or MergeStatement:
                    // OUTPUT without INTO: exactly one result set, whose rows are not a table's rows.
                    Sets.Add(new Set(null, null, "the rows come from an OUTPUT clause, not from a table"));
                    return true;
                case SetStatisticsStatement st when (st.Options & SetStatisticsOptions.Profile) == 0:
                case DeclareVariableStatement or DeclareTableVariableStatement or SetVariableStatement or PredicateSetStatement or SetCommandStatement
                    or SetRowCountStatement or SetTextSizeStatement or SetTransactionIsolationLevelStatement or SetIdentityInsertStatement
                    or UseStatement or PrintStatement or RaiseErrorStatement or ThrowStatement
                    or BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement or SaveTransactionStatement:
                    return true;
                // Statements that return no rows whatever they do.
                case TruncateTableStatement or SecurityStatement or UpdateStatisticsStatement or CheckpointStatement or ExecuteAsStatement or RevertStatement
                    or DeclareCursorStatement or OpenCursorStatement or CloseCursorStatement or DeallocateCursorStatement
                    or LabelStatement or BreakStatement or ContinueStatement:
                    return true;
                case WaitForStatement w when w.Statement == null: // WAITFOR DELAY / TIME (not WAITFOR (RECEIVE ...))
                    return true;
                case FetchCursorStatement f when f.IntoVariables.Count > 0:
                    return true;
                case FetchCursorStatement:
                    Sets.Add(new Set(null, null, "the row comes from a cursor FETCH, not from a table"));
                    return true;
                case ReturnStatement:
                    return false; // the batch ends here: nothing after it returns rows
                case BeginEndBlockStatement block:
                    return Run(block.StatementList?.Statements ?? []);
                case IfStatement branch:
                    return Silent(branch, branch.ThenStatement, branch.ElseStatement);
                case WhileStatement loop:
                    return Silent(loop, loop.Statement);
                case TryCatchStatement t:
                    return Silent(t, [.. t.TryStatements?.Statements ?? [], .. t.CatchStatements?.Statements ?? []]);
                default:
                    // DDL returns no rows; anything else (EXEC, DBCC, GOTO, ...) might, any number of times.
                    var name = stmt.GetType().Name;
                    if (name.StartsWith("Create", StringComparison.Ordinal) || name.StartsWith("Alter", StringComparison.Ordinal) || name.StartsWith("Drop", StringComparison.Ordinal)) return true;
                    return Stop(stmt);
            }
        }

        /// <summary>
        /// A statement that may run its parts or not, or more than once (IF, WHILE, TRY...CATCH): accounted for only when no
        /// part returns rows — a conditional DROP or a loop of updates. Otherwise the count is open from here.
        /// </summary>
        bool Silent(TSqlStatement owner, params TSqlStatement?[] parts)
        {
            var inner = new Walk(index, database);
            // A RETURN inside ends that part only as far as counting goes: what follows it in the batch may still not run,
            // which only shortens the list of result sets, never reorders it.
            foreach (var part in parts)
                if (part != null) inner.Step(part);
            return inner.Sets.Count == 0 && inner.Open == null || Stop(owner);
        }

        bool Stop(TSqlStatement stmt)
        {
            Open = Keyword(stmt);
            return false;
        }

        static string Keyword(TSqlStatement stmt)
        {
            if (stmt is TryCatchStatement) return "BEGIN TRY";
            var tokens = stmt.ScriptTokenStream;
            return tokens != null && stmt.FirstTokenIndex >= 0 && stmt.FirstTokenIndex < tokens.Count && !string.IsNullOrWhiteSpace(tokens[stmt.FirstTokenIndex].Text)
                ? tokens[stmt.FirstTokenIndex].Text.ToUpperInvariant()
                : "another statement";
        }
    }

    /// <summary>The set operator of a combined query (UNION, EXCEPT, INTERSECT), looking through parentheses.</summary>
    static string? Combined(QueryExpression? q)
    {
        while (q is QueryParenthesisExpression p) q = p.QueryExpression;
        return q is BinaryQueryExpression b
            ? b.BinaryQueryExpressionType switch { BinaryQueryExpressionType.Except => "EXCEPT", BinaryQueryExpressionType.Intersect => "INTERSECT", _ => "UNION" }
            : null;
    }

    static HashSet<string> CteNames(SelectStatement s) =>
        new((s.WithCtesAndXmlNamespaces?.CommonTableExpressions ?? []).Select(c => c.ExpressionName.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>A FROM item: a snapshot table/view, or an opaque source (CTE, derived table, temp table, TVF...).</summary>
    sealed record From(string Exposed, string? Schema, SnapObject? Object);

    /// <summary>What <see cref="Flatten"/> noticed besides the sources themselves.</summary>
    sealed class Notes
    {
        /// <summary>An unqualified table name found in more than one schema.</summary>
        public string? Ambiguous;
        /// <summary>Why a table of another database or a linked server is not this snapshot's.</summary>
        public string? Elsewhere;
        /// <summary>A table named in FROM that the snapshot does not hold at all: created since it was read, or mistyped.</summary>
        public string? Missing;
    }

    static Set Project(QuerySpecification qs, HashSet<string> ctes, SchemaIndex index, string? database)
    {
        var sources = new List<From>();
        var notes = new Notes();
        foreach (var t in qs.FromClause?.TableReferences ?? []) Flatten(t, ctes, index, database, sources, notes);
        // Exact or nothing: the snapshot cannot tell which schema SQL Server picks for the name.
        if (notes.Ambiguous != null)
            return new Set(null, $"table {notes.Ambiguous} exists in more than one schema (ambiguous schema — qualify the table name)");
        // Only another database's (or linked server's) table is read: say so, rather than "no table columns". With a table of
        // this database beside it, that table's columns stay editable and the other's are locked like any expression.
        if (notes.Elsewhere != null && sources.All(s => s.Object == null)) return new Set(null, notes.Elsewhere);
        // A self-join gives every alias the same object id: a key read from one alias could update the row of another.
        if (sources.Where(s => s.Object != null).GroupBy(s => s.Object!.Id).FirstOrDefault(g => g.Count() > 1)?.First().Object is { } twice)
            return new Set(null, $"the same table ({twice.Schema}.{twice.Name}) appears more than once in the query");
        var tables = string.Join(", ", sources.Where(s => s.Object != null).Select(s => $"{s.Object!.Schema}.{s.Object.Name}").Distinct(StringComparer.Ordinal));
        // A table the snapshot does not hold: newer metadata may (it was created since), so the caller reads it again.
        var missing = notes.Missing != null ? $"{notes.Missing} is not in this database's cached metadata" : null;
        var output = new List<PredictedColumn>();
        foreach (var element in qs.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression star:
                {
                    var picked = star.Qualifier is { } q ? sources.Where(s => Matches(s, q.Identifiers)).ToList() : sources;
                    // The column count is unknown: nothing of this result set is traced.
                    if (picked.Count == 0 || picked.Any(s => s.Object?.Columns == null))
                    {
                        if (missing != null) return new Set(null, null, missing, Stale: true, tables);
                        if (picked.FirstOrDefault(s => s.Object is { Columns: null }) is { Object: { } bare })
                            return new Set(null, null, $"the columns of {bare.Schema}.{bare.Name} are not in this database's cached metadata", Stale: true, tables);
                        return new Set(null, null, "SELECT * reads a source whose columns are not traced to a table (a temporary table, CTE, subquery, function or synonym)", Tables: tables);
                    }
                    foreach (var s in picked)
                        foreach (var c in s.Object!.Columns!) output.Add(new PredictedColumn(c.Name, new ColumnOrigin(s.Object.Id, c.Name)));
                    break;
                }
                case SelectScalarExpression se:
                {
                    var alias = se.ColumnName?.Value;
                    var expr = se.Expression;
                    while (expr is ParenthesisExpression p) expr = p.Expression;
                    if (expr is ColumnReferenceExpression { ColumnType: ColumnType.Regular, MultiPartIdentifier: { Identifiers.Count: > 0 } m })
                    {
                        var column = m.Identifiers[^1].Value;
                        output.Add(new PredictedColumn(alias ?? column, Resolve(sources, m.Identifiers.Take(m.Identifiers.Count - 1).ToList(), column)));
                    }
                    else output.Add(new PredictedColumn(alias, null));
                    break;
                }
                default:
                    output.Add(new PredictedColumn(null, null));
                    break;
            }
        }
        return new Set([.. output], null, missing, Stale: missing != null, tables.Length > 0 ? tables : null);
    }

    static ColumnOrigin? Resolve(List<From> sources, IList<Identifier> qualifier, string column)
    {
        var candidates = qualifier.Count > 0 ? sources.Where(s => Matches(s, qualifier)).ToList() : sources;
        // An opaque candidate might own the column, so the answer would be a guess.
        if (candidates.Any(s => s.Object?.Columns == null)) return null;
        var owners = candidates.Where(s => s.Object!.Columns!.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))).ToList();
        if (owners.Count != 1) return null;
        var col = owners[0].Object!.Columns!.First(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        return new ColumnOrigin(owners[0].Object!.Id, col.Name);
    }

    static bool Matches(From s, IList<Identifier> qualifier)
    {
        if (!string.Equals(s.Exposed, qualifier[^1].Value, StringComparison.OrdinalIgnoreCase)) return false;
        return qualifier.Count < 2 || string.Equals(s.Schema, qualifier[^2].Value, StringComparison.OrdinalIgnoreCase);
    }

    static void Flatten(TableReference t, HashSet<string> ctes, SchemaIndex index, string? database, List<From> into, Notes notes)
    {
        switch (t)
        {
            case JoinTableReference j:
                Flatten(j.FirstTableReference, ctes, index, database, into, notes);
                Flatten(j.SecondTableReference, ctes, index, database, into, notes);
                return;
            case JoinParenthesisTableReference jp:
                Flatten(jp.Join, ctes, index, database, into, notes);
                return;
            case NamedTableReference n:
            {
                var so = n.SchemaObject;
                var name = so.BaseIdentifier.Value;
                var exposed = n.Alias?.Value ?? name;
                // db..table leaves the schema part empty: the default schema, resolved like an unqualified name.
                var schema = string.IsNullOrEmpty(so.SchemaIdentifier?.Value) ? null : so.SchemaIdentifier!.Value;
                // A linked server and another database are not in this snapshot, nor are CTEs and temp tables. The tab's own
                // database named in front (SSMS "Select Top 1000 Rows" writes it) is this snapshot's: the snapshot is the
                // tab's database, and Apply checks the tab is still on it before writing schema.table there.
                var otherDatabase = so.DatabaseIdentifier is { } db && !string.Equals(db.Value, database, StringComparison.OrdinalIgnoreCase);
                if (so.ServerIdentifier != null || otherDatabase || (schema == null && ctes.Contains(name)) || name.StartsWith('#'))
                {
                    if (!name.StartsWith('#'))
                    {
                        var full = string.Join(".", so.Identifiers.Select(i => i.Value));
                        if (so.ServerIdentifier != null) notes.Elsewhere ??= $"{full} is on linked server {so.ServerIdentifier.Value}";
                        else if (otherDatabase)
                            notes.Elsewhere ??= $"{full} is in database {so.DatabaseIdentifier!.Value}, not this tab's database ({database}) — switch the tab to {so.DatabaseIdentifier.Value} to edit it";
                    }
                    into.Add(new From(exposed, schema, null));
                    return;
                }
                var obj = Find(index, schema, name, out var several);
                if (several) notes.Ambiguous ??= name;
                // Not a table or view of the snapshot, nor anything else it knows by that name (a synonym, a function), nor
                // a system view: the table may have been created after the snapshot was read.
                else if (obj == null && index.Find(schema, name) == null && !SystemObject(schema, name))
                    notes.Missing ??= schema != null ? $"{schema}.{name}" : name;
                into.Add(new From(exposed, n.Alias == null ? obj?.Schema ?? schema : null, obj));
                return;
            }
            case TableReferenceWithAlias a:
                into.Add(new From(a.Alias?.Value ?? "", null, null));
                return;
            default:
                into.Add(new From("", null, null));
                return;
        }
    }

    /// <summary>The SQL Server 2000 system tables, still readable without a schema (compatibility views).</summary>
    static readonly HashSet<string> LegacySystemTables = new(
        ["sysobjects", "syscolumns", "sysindexes", "sysindexkeys", "sysusers", "sysprocesses", "sysdatabases", "syscomments", "sysfiles", "sysfilegroups", "systypes",
         "sysdepends", "sysconstraints", "sysforeignkeys", "sysreferences", "syslogins", "sysmessages", "sysservers", "sysperfinfo", "syslockinfo", "syspermissions",
         "sysprotects", "sysmembers", "sysaltfiles", "sysconfigures", "syscurconfigs", "sysdevices", "syslanguages", "syscharsets", "syscacheobjects", "sysfulltextcatalogs"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>sys and INFORMATION_SCHEMA hold system views, which no snapshot lists and no refresh adds.</summary>
    static bool SystemObject(string? schema, string name) =>
        string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase) || string.Equals(schema, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)
        || (schema is null or "dbo" && LegacySystemTables.Contains(name));

    /// <summary>
    /// Exact or nothing: an unqualified name resolves only when one table or view in the snapshot has it. SQL Server looks
    /// in the login's default schema first, then dbo; the snapshot knows neither, so a name in several schemas is
    /// <paramref name="ambiguous"/> rather than resolved to dbo, which could be another table than the one the server read.
    /// </summary>
    static SnapObject? Find(SchemaIndex index, string? schema, string name, out bool ambiguous)
    {
        static bool Rowset(SnapObject o) => o.Kind is ObjectKind.Table or ObjectKind.View;
        ambiguous = false;
        if (schema != null) return index.Find(schema, name, Rowset);
        var any = index.Find(null, name, Rowset);
        if (any == null) return null;
        ambiguous = index.Find(null, name, o => Rowset(o) && o.Id != any.Id) != null;
        return ambiguous ? null : any;
    }
}
