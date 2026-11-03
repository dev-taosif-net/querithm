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
    sealed record Entry(string Text, SchemaSnapshot Snapshot, List<PredictedColumn[]?>? Result);

    static volatile Entry? _last;

    /// <summary>Per data result set (null = exists but unpredictable); null when the batch is not predictable at all.</summary>
    public static List<PredictedColumn[]?>? Predict(string batchText, SchemaSnapshot snapshot)
    {
        if (_last is { } e && ReferenceEquals(e.Snapshot, snapshot) && e.Text == batchText) return e.Result;
        var result = Compute(batchText, snapshot);
        _last = new Entry(batchText, snapshot, result);
        return result;
    }

    static List<PredictedColumn[]?>? Compute(string text, SchemaSnapshot snapshot)
    {
        if (SchemaIndex.For(snapshot) is not { } index) return null;
        if (TSqlParsing.NewParser().Parse(new StringReader(text), out var errors) is not TSqlScript script || errors.Count > 0) return null;
        var results = new List<PredictedColumn[]?>();
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
                        else results.Add(null); // UNION, FOR XML/JSON: one result set, columns not traced
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

    static HashSet<string> CteNames(SelectStatement s) =>
        new((s.WithCtesAndXmlNamespaces?.CommonTableExpressions ?? []).Select(c => c.ExpressionName.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>A FROM item: a snapshot table/view, or an opaque source (CTE, derived table, temp table, TVF...).</summary>
    sealed record From(string Exposed, string? Schema, SnapObject? Object);

    static PredictedColumn[]? Project(QuerySpecification qs, HashSet<string> ctes, SchemaIndex index)
    {
        var sources = new List<From>();
        foreach (var t in qs.FromClause?.TableReferences ?? []) Flatten(t, ctes, index, sources);
        var output = new List<PredictedColumn>();
        foreach (var element in qs.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression star:
                {
                    var picked = star.Qualifier is { } q ? sources.Where(s => Matches(s, q.Identifiers)).ToList() : sources;
                    if (picked.Count == 0 || picked.Any(s => s.Object?.Columns == null)) return null; // column count unknown
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
        return [.. output];
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

    static void Flatten(TableReference t, HashSet<string> ctes, SchemaIndex index, List<From> into)
    {
        switch (t)
        {
            case JoinTableReference j:
                Flatten(j.FirstTableReference, ctes, index, into);
                Flatten(j.SecondTableReference, ctes, index, into);
                return;
            case JoinParenthesisTableReference jp:
                Flatten(jp.Join, ctes, index, into);
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
                var obj = Find(index, schema, name);
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

    // Exact or nothing: an unqualified name resolves to dbo, else to the only table or view of that name.
    static SnapObject? Find(SchemaIndex index, string? schema, string name)
    {
        static bool Rowset(SnapObject o) => o.Kind is ObjectKind.Table or ObjectKind.View;
        if (schema != null) return index.Find(schema, name, Rowset);
        if (index.Find("dbo", name, Rowset) is { } dbo) return dbo;
        var any = index.Find(null, name, Rowset);
        return any != null && index.Find(null, name, o => Rowset(o) && o.Id != any.Id) == null ? any : null;
    }
}
