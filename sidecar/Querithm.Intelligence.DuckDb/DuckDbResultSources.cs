using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;

namespace Querithm.Intelligence.DuckDb;

/// <summary>
/// The table column behind each result column, predicted from the statement's tokens (Phase D3): the DuckDB driver reports
/// no base table or column. Only the plain shape is predicted — <c>SELECT item, … FROM one_table [alias] [WHERE …]
/// [ORDER BY …] [LIMIT …]</c> and DuckDB's <c>FROM one_table [alias] [WHERE …]</c> — where every item is <c>*</c>,
/// <c>alias.*</c> or a column reference with an optional alias. Anything else (joins, expressions, DISTINCT, GROUP BY, set
/// operations, CTEs) predicts nothing, and the result stays read-only with the analyzer's reason.
/// </summary>
public static class DuckDbResultSources
{
    /// <summary>Words that end the FROM item: what follows filters or orders the rows without changing their source.</summary>
    static readonly HashSet<string> Tail = new(["WHERE", "ORDER", "LIMIT", "OFFSET"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Words after the table that make it anything but a single plain source.</summary>
    static readonly HashSet<string> NotPlain = new(
        ["JOIN", "CROSS", "NATURAL", "INNER", "LEFT", "RIGHT", "FULL", "POSITIONAL", "ASOF", "ANTI", "SEMI", "GROUP", "HAVING", "WINDOW",
         "QUALIFY", "UNION", "EXCEPT", "INTERSECT", "USING", "SAMPLE", "TABLESAMPLE", "PIVOT", "UNPIVOT", "SELECT", "AT"], StringComparer.OrdinalIgnoreCase);

    /// <summary>One origin per result column, or null when the statement's shape is not predicted (or the table is unknown).</summary>
    public static ColumnOrigin?[]? Predict(string statement, SchemaSnapshot snapshot, string defaultSchema, IReadOnlyList<string> resultNames)
    {
        var code = DuckDbTokenizer.Tokenize(statement).Where(t => !t.IsTrivia).ToList();
        if (code.Count > 0 && code[^1].Text == ";") code.RemoveAt(code.Count - 1);
        if (code.Count < 2 || code.Any(t => t.Text is "(" or ")")) return null;
        List<PgToken> items;
        int fromAt;
        if (code[0].Is("SELECT"))
        {
            fromAt = code.FindIndex(t => t.Is("FROM"));
            if (fromAt < 2 || code[1].Is("DISTINCT") || code[1].Is("ALL")) return null;
            items = code.GetRange(1, fromAt - 1);
        }
        else if (code[0].Is("FROM"))
        {
            fromAt = 0;
            items = [new PgToken(PgTokenKind.Operator, 0, 1, 1, "*")];
        }
        else return null;

        // FROM [schema.]table [[AS] alias] then only the tail.
        var i = fromAt + 1;
        if (i >= code.Count || !Name(code[i])) return null;
        string schema = defaultSchema, table = DuckDbTokenizer.Unquote(code[i]);
        if (i + 2 < code.Count && code[i + 1].Text == "." && Name(code[i + 2]))
        {
            schema = table;
            table = DuckDbTokenizer.Unquote(code[i + 2]);
            i += 2;
        }
        if (i + 2 < code.Count && code[i + 1].Text == "." ) return null; // catalog.schema.table: another catalog is never the session's
        i++;
        string? alias = null;
        if (i < code.Count && code[i].Is("AS")) i++;
        if (i < code.Count && Name(code[i]) && !Tail.Contains(code[i].Text) && !NotPlain.Contains(code[i].Text)) alias = DuckDbTokenizer.Unquote(code[i++]);
        if (i < code.Count && !Tail.Contains(code[i].Text)) return null;
        for (var k = i; k < code.Count; k++)
            if (code[k].Kind == PgTokenKind.Word && NotPlain.Contains(code[k].Text)) return null;

        var names = StringComparer.OrdinalIgnoreCase;
        var obj = snapshot.Objects.Where(o => o.Kind == ObjectKind.Table && names.Equals(o.Name, table) && names.Equals(o.Schema, schema) && o.Columns != null).Take(2).ToList();
        if (obj.Count != 1) return null;
        var t = obj[0];
        var columns = t.Columns!.OrderBy(c => c.Ordinal).ToList();

        var origins = new List<ColumnOrigin?>();
        foreach (var item in SplitItems(items))
        {
            if (item.Count == 1 && item[0].Text == "*") { origins.AddRange(columns.Select(c => (ColumnOrigin?)new ColumnOrigin(t.Id, c.Name))); continue; }
            // alias.* reads as alias then one ".*" operator token (or ".", "*" when spaced).
            if (((item.Count == 2 && item[1].Text == ".*") || (item.Count == 3 && item[1].Text == "." && item[2].Text == "*")) && Qualifies(item[0], table, alias))
            {
                origins.AddRange(columns.Select(c => (ColumnOrigin?)new ColumnOrigin(t.Id, c.Name)));
                continue;
            }
            if (item.Count == 0 || item.Any(x => x.Text is "*" or ".*" && item.Count <= 3)) return null;
            // column | qualifier.column, then [AS] alias; any other item is an expression: one column without an origin.
            var at = 0;
            if (item.Count >= 3 && item[1].Text == "." && Name(item[0]) && Name(item[2]))
            {
                if (!Qualifies(item[0], table, alias)) return null;
                at = 2;
            }
            var rest = item.Count - at - 1;
            var plain = Name(item[at]) && (rest == 0 || (rest == 1 && Name(item[at + 1])) || (rest == 2 && item[at + 1].Is("AS") && Name(item[at + 2])));
            var column = plain ? columns.FirstOrDefault(c => names.Equals(c.Name, DuckDbTokenizer.Unquote(item[at]))) : null;
            origins.Add(column == null ? null : new ColumnOrigin(t.Id, column.Name));
        }
        return origins.Count == resultNames.Count ? origins.ToArray() : null;
    }

    static bool Name(PgToken t) => t.Kind == PgTokenKind.QuotedIdent || (t.Kind == PgTokenKind.Word && !t.Is("FROM") && !t.Is("AS") && !t.Is("SELECT"));

    static bool Qualifies(PgToken q, string table, string? alias) =>
        string.Equals(DuckDbTokenizer.Unquote(q), alias ?? table, StringComparison.OrdinalIgnoreCase);

    static IEnumerable<List<PgToken>> SplitItems(List<PgToken> items)
    {
        var cur = new List<PgToken>();
        foreach (var t in items)
        {
            if (t.Text == ",") { yield return cur; cur = []; }
            else cur.Add(t);
        }
        yield return cur;
    }
}
