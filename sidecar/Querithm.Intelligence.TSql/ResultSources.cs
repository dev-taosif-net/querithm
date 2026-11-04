using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql.Completion;

namespace Querithm.Intelligence.TSql;

/// <summary>One predicted output column: its name (null for an unnamed expression, not checked) and source.</summary>
public sealed record PredictedColumn(string? Name, ColumnOrigin? Origin);

/// <summary>
/// Predicts, from the ScriptDom AST, which table column feeds each column of each data result set of a batch.
/// SQL Server reports no base table without browse mode, which would change how the user's query runs.
/// Conservative by design: a statement that might add or reorder result sets (EXEC, IF, OUTPUT, ...) gives up
/// on the whole batch, and an ambiguous column gets no origin. Callers verify the names against the real result.
/// </summary>
public static class ResultSources
{
    /// <summary>A data result set's prediction, and why it cannot be edited whatever its columns say (null = no objection).</summary>
    sealed record Set(PredictedColumn[]? Columns, string? Refusal);

    sealed record Entry(string Text, SchemaSnapshot Snapshot, List<Set>? Result);

    static volatile Entry? _last;

    /// <summary>Per data result set (null = exists but unpredictable); null when the batch is not predictable at all.</summary>
    public static List<PredictedColumn[]?>? Predict(string batchText, SchemaSnapshot snapshot) =>
        Sets(batchText, snapshot)?.Select(s => s.Columns).ToList();

    /// <summary>
    /// Why data result set <paramref name="resultInBatch"/> cannot be edited even where its columns trace to a table: the
    /// same table is read twice (a self-join: both aliases carry one object id, so a row key could match another row than
    /// the one shown), it combines several queries (UNION, EXCEPT, INTERSECT), or an unqualified table name exists in more
    /// than one schema (SQL Server resolves it through the login's default schema, which the snapshot does not know).
    /// </summary>
    public static string? Refusal(string batchText, SchemaSnapshot snapshot, int resultInBatch) =>
        Sets(batchText, snapshot) is { } sets && resultInBatch >= 0 && resultInBatch < sets.Count ? sets[resultInBatch].Refusal : null;

    static List<Set>? Sets(string batchText, SchemaSnapshot snapshot)
    {
        if (_last is { } e && ReferenceEquals(e.Snapshot, snapshot) && e.Text == batchText) return e.Result;
        var result = Compute(batchText, snapshot);
        _last = new Entry(batchText, snapshot, result);
        return result;
    }

    static List<Set>? Compute(string text, SchemaSnapshot snapshot)
    {
        if (SchemaIndex.For(snapshot) is not { } index) return null;
        if (TSqlParsing.NewParser().Parse(new StringReader(text), out var errors) is not TSqlScript script || errors.Count > 0) return null;
        var results = new List<Set>();
        foreach (var batch in script.Batches)
            foreach (var stmt in batch.Statements)
            {
                switch (stmt)
                {
                    case SelectStatement s:
                        if (s.Into != null) break;
                        if (s.QueryExpression is QuerySpecification qs && s.QueryExpression.ForClause == null)
                        {
                            if (qs.SelectElements.Any(x => x is SelectSetVariable)) break; // SELECT @x = ...: no rows
                            results.Add(Project(qs, CteNames(s), index));
                        }
                        // UNION, FOR XML/JSON: one result set, columns not traced
                        else results.Add(new Set(null, Combined(s.QueryExpression) is { } op ? $"the query combines several queries ({op})" : null));
                        break;
                    case InsertStatement i when i.InsertSpecification.OutputClause == null:
                    case UpdateStatement u when u.UpdateSpecification.OutputClause == null:
                    case DeleteStatement d when d.DeleteSpecification.OutputClause == null:
                    case MergeStatement m when m.MergeSpecification.OutputClause == null:
                        break;
                    case SetStatisticsStatement st when (st.Options & SetStatisticsOptions.Profile) == 0:
                    case DeclareVariableStatement or DeclareTableVariableStatement or SetVariableStatement or PredicateSetStatement or SetCommandStatement
                        or SetRowCountStatement or SetTextSizeStatement or SetTransactionIsolationLevelStatement or SetIdentityInsertStatement
                        or UseStatement or PrintStatement or RaiseErrorStatement or ThrowStatement
                        or BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement or SaveTransactionStatement:
                        break;
                    default:
                        // DDL returns no rows; anything else (EXEC, IF, WHILE, TRY, OUTPUT, DBCC...) might.
                        var name = stmt.GetType().Name;
                        if (!(name.StartsWith("Create", StringComparison.Ordinal) || name.StartsWith("Alter", StringComparison.Ordinal) || name.StartsWith("Drop", StringComparison.Ordinal))) return null;
                        break;
                }
            }
        return results;
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

    static Set Project(QuerySpecification qs, HashSet<string> ctes, SchemaIndex index)
    {
        var sources = new List<From>();
        string? ambiguous = null;
        foreach (var t in qs.FromClause?.TableReferences ?? []) Flatten(t, ctes, index, sources, ref ambiguous);
        // Exact or nothing: the snapshot cannot tell which schema SQL Server picks for the name.
        if (ambiguous != null)
            return new Set(null, $"table {ambiguous} exists in more than one schema (ambiguous schema — qualify the table name)");
        // A self-join gives every alias the same object id: a key read from one alias could update the row of another.
        if (sources.Where(s => s.Object != null).GroupBy(s => s.Object!.Id).FirstOrDefault(g => g.Count() > 1)?.First().Object is { } twice)
            return new Set(null, $"the same table ({twice.Schema}.{twice.Name}) appears more than once in the query");
        var output = new List<PredictedColumn>();
        foreach (var element in qs.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression star:
                {
                    var picked = star.Qualifier is { } q ? sources.Where(s => Matches(s, q.Identifiers)).ToList() : sources;
                    if (picked.Count == 0 || picked.Any(s => s.Object?.Columns == null)) return new Set(null, null); // column count unknown
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
        return new Set([.. output], null);
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

    static void Flatten(TableReference t, HashSet<string> ctes, SchemaIndex index, List<From> into, ref string? ambiguous)
    {
        switch (t)
        {
            case JoinTableReference j:
                Flatten(j.FirstTableReference, ctes, index, into, ref ambiguous);
                Flatten(j.SecondTableReference, ctes, index, into, ref ambiguous);
                return;
            case JoinParenthesisTableReference jp:
                Flatten(jp.Join, ctes, index, into, ref ambiguous);
                return;
            case NamedTableReference n:
            {
                var so = n.SchemaObject;
                var name = so.BaseIdentifier.Value;
                var exposed = n.Alias?.Value ?? name;
                var schema = so.SchemaIdentifier?.Value;
                // Other databases, CTEs and temp tables are not in this snapshot.
                if (so.DatabaseIdentifier != null || so.ServerIdentifier != null || (schema == null && ctes.Contains(name)) || name.StartsWith('#'))
                {
                    into.Add(new From(exposed, schema, null));
                    return;
                }
                var obj = Find(index, schema, name, out var several);
                if (several) ambiguous ??= name;
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
