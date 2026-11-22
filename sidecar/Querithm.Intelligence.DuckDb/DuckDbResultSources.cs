using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;

namespace Querithm.Intelligence.DuckDb;

/// <summary>
/// The table column behind each result column, predicted from the statement's tokens (Phase D3): the DuckDB driver reports
/// no base table or column. Only the plain shape is predicted — <c>SELECT item, … FROM one_table [alias] [WHERE …]
/// [ORDER BY …] [LIMIT …]</c> and DuckDB's <c>FROM one_table [alias] [WHERE …]</c> — where every item is <c>*</c>,
/// <c>alias.*</c> or a column reference with an optional alias; any other item is an expression (one column without an
/// origin). The WHERE / ORDER BY / LIMIT tail may hold parentheses (IN lists, function calls, subqueries). Anything else
/// (joins, DISTINCT, GROUP BY, set operations, CTEs, table functions) predicts nothing, and the result stays read-only
/// with the reason <see cref="Trace"/> gives.
/// </summary>
public static class DuckDbResultSources
{
    /// <summary>Words that end the FROM item: what follows filters or orders the rows without changing their source.</summary>
    static readonly HashSet<string> Tail = new(["WHERE", "ORDER", "LIMIT", "OFFSET"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Words after the table that make it anything but a single plain source.</summary>
    static readonly HashSet<string> NotPlain = new(
        ["JOIN", "CROSS", "NATURAL", "INNER", "LEFT", "RIGHT", "FULL", "POSITIONAL", "ASOF", "ANTI", "SEMI", "GROUP", "HAVING", "WINDOW",
         "QUALIFY", "UNION", "EXCEPT", "INTERSECT", "USING", "SAMPLE", "TABLESAMPLE", "PIVOT", "UNPIVOT", "SELECT", "AT"], StringComparer.OrdinalIgnoreCase);

    /// <summary>What the user is told when the statement is not the one shape this predictor reads.</summary>
    const string NotPlainShape = "on DuckDB only a plain SELECT from one table can be edited (no joins, DISTINCT, GROUP BY, CTEs or table functions)";

    /// <summary>One origin per result column, or null when the statement's shape is not predicted (or the table is unknown).</summary>
    public static ColumnOrigin?[]? Predict(string statement, SchemaSnapshot snapshot, string defaultSchema, IReadOnlyList<string> resultNames) =>
        Trace(statement, snapshot, defaultSchema, resultNames).Columns;

    /// <summary>
    /// <see cref="Predict"/> with the reason when nothing is predicted, and whether newer metadata may change that: the
    /// table is not in the snapshot, or <c>*</c> gave another number of columns than the snapshot lists.
    /// </summary>
    public static TracedOrigins Trace(string statement, SchemaSnapshot snapshot, string defaultSchema, IReadOnlyList<string> resultNames)
    {
        var shape = new TracedOrigins(null, NotPlainShape);
        var code = DuckDbTokenizer.Tokenize(statement).Where(t => !t.IsTrivia).ToList();
        if (code.Count > 0 && code[^1].Text == ";") code.RemoveAt(code.Count - 1);
        if (code.Count < 2) return shape;
        List<PgToken> items;
        int fromAt;
        if (code[0].Is("SELECT"))
        {
            // The statement's own FROM: one inside parentheses belongs to a subquery or to EXTRACT(… FROM …).
            fromAt = TopLevelIndex(code, 1, t => t.Is("FROM"));
            if (fromAt < 2 || code[1].Is("DISTINCT") || code[1].Is("ALL")) return shape;
            items = code.GetRange(1, fromAt - 1);
        }
        else if (code[0].Is("FROM"))
        {
            fromAt = 0;
            items = [new PgToken(PgTokenKind.Operator, 0, 1, 1, "*")];
        }
        else return shape;

        // FROM [[catalog.]schema.]table [[AS] alias] then only the tail.
        var i = fromAt + 1;
        if (i >= code.Count || !Name(code[i])) return shape;
        string schema = defaultSchema, table = DuckDbTokenizer.Unquote(code[i]);
        // catalog.schema.table: only the session's own catalog — the snapshot's database, where Apply writes schema.table —
        // is this table. Another catalog (temp included, whose tables the snapshot files under schema "temp") is not.
        if (i + 4 < code.Count && code[i + 1].Text == "." && Name(code[i + 2]) && code[i + 3].Text == "." && Name(code[i + 4]))
        {
            if (!string.Equals(table, snapshot.Database, StringComparison.OrdinalIgnoreCase)) return shape;
            schema = DuckDbTokenizer.Unquote(code[i + 2]);
            table = DuckDbTokenizer.Unquote(code[i + 4]);
            i += 4;
        }
        else if (i + 2 < code.Count && code[i + 1].Text == "." && Name(code[i + 2]))
        {
            schema = table;
            table = DuckDbTokenizer.Unquote(code[i + 2]);
            i += 2;
        }
        if (i + 1 < code.Count && code[i + 1].Text == ".") return shape; // a fourth part, or a name cut short
        i++;
        string? alias = null;
        if (i < code.Count && code[i].Is("AS")) i++;
        if (i < code.Count && Name(code[i]) && !Tail.Contains(code[i].Text) && !NotPlain.Contains(code[i].Text)) alias = DuckDbTokenizer.Unquote(code[i++]);
        // Anything else right after the table (an opening parenthesis: a table function; a comma: a second table) is not plain.
        if (i < code.Count && !Tail.Contains(code[i].Text)) return shape;
        // The tail may hold parentheses — WHERE id IN (1, 2), lower(v) = 'x', a subquery — whose words are its own; at the
        // statement's level it must not join, group or combine.
        var depth = 0;
        for (var k = i; k < code.Count; k++)
        {
            if (Opens(code[k])) depth++;
            else if (Closes(code[k])) depth--;
            else if (depth == 0 && code[k].Kind == PgTokenKind.Word && NotPlain.Contains(code[k].Text)) return shape;
        }

        var names = StringComparer.OrdinalIgnoreCase;
        var named = snapshot.Objects.Where(o => names.Equals(o.Name, table) && names.Equals(o.Schema, schema)).ToList();
        var obj = named.Where(o => o.Kind == ObjectKind.Table && o.Columns != null).Take(2).ToList();
        if (obj.Count != 1)
        {
            // A view or another object of that name is in the snapshot: nothing newer metadata would turn into a table.
            if (named.FirstOrDefault(o => o.Kind != ObjectKind.Table) is { } other) return new TracedOrigins(null, $"{other.Schema}.{other.Name} is not a table");
            return obj.Count == 0 ? new TracedOrigins(null, $"{schema}.{table} is not in this database's cached metadata", Stale: true) : shape;
        }
        var t = obj[0];
        var columns = t.Columns!.OrderBy(c => c.Ordinal).ToList();

        var origins = new List<ColumnOrigin?>();
        var star = false;
        foreach (var item in SplitItems(items))
        {
            if (item.Count == 1 && item[0].Text == "*") { star = true; origins.AddRange(columns.Select(c => (ColumnOrigin?)new ColumnOrigin(t.Id, c.Name))); continue; }
            // alias.* reads as alias then one ".*" operator token (or ".", "*" when spaced).
            if (((item.Count == 2 && item[1].Text == ".*") || (item.Count == 3 && item[1].Text == "." && item[2].Text == "*")) && Qualifies(item[0], table, alias))
            {
                star = true;
                origins.AddRange(columns.Select(c => (ColumnOrigin?)new ColumnOrigin(t.Id, c.Name)));
                continue;
            }
            if (item.Count == 0 || item.Any(x => x.Text is "*" or ".*" && item.Count <= 3)) return shape;
            // column | qualifier.column, then [AS] alias; any other item is an expression: one column without an origin.
            var at = 0;
            if (item.Count >= 3 && item[1].Text == "." && Name(item[0]) && Name(item[2]))
            {
                if (!Qualifies(item[0], table, alias)) return shape;
                at = 2;
            }
            var rest = item.Count - at - 1;
            var plain = Name(item[at]) && (rest == 0 || (rest == 1 && Name(item[at + 1])) || (rest == 2 && item[at + 1].Is("AS") && Name(item[at + 2])));
            var column = plain ? columns.FirstOrDefault(c => names.Equals(c.Name, DuckDbTokenizer.Unquote(item[at]))) : null;
            origins.Add(column == null ? null : new ColumnOrigin(t.Id, column.Name));
        }
        if (origins.Count == resultNames.Count) return new TracedOrigins(origins.ToArray());
        // * gave another number of columns than the snapshot lists: the table changed since. Without a *, an item expanded
        // to several columns (COLUMNS(…), * EXCLUDE …), which no metadata changes.
        return star ? new TracedOrigins(null, $"the columns this query returned are not the ones cached for {t.Schema}.{t.Name}", Stale: true) : shape;
    }

    static bool Opens(PgToken t) => t.Text is "(" or "[" or "{";
    static bool Closes(PgToken t) => t.Text is ")" or "]" or "}";

    /// <summary>The first index from <paramref name="start"/> outside any brackets where <paramref name="match"/> holds; −1 when none.</summary>
    static int TopLevelIndex(List<PgToken> code, int start, Func<PgToken, bool> match)
    {
        var depth = 0;
        for (var i = start; i < code.Count; i++)
        {
            if (Opens(code[i])) depth++;
            else if (Closes(code[i])) depth--;
            else if (depth == 0 && match(code[i])) return i;
        }
        return -1;
    }

    /// <summary>
    /// Why the result cannot be edited when it reads <c>catalog.schema.table</c> of another catalog than the session's (the
    /// snapshot's database): the snapshot does not know that table and Apply would write the session's. Null otherwise.
    /// </summary>
    public static string? OtherCatalogRefusal(string statement, SchemaSnapshot? snapshot)
    {
        if (snapshot == null) return null;
        var code = DuckDbTokenizer.Tokenize(statement).Where(t => !t.IsTrivia).ToList();
        for (var i = 0; i + 5 < code.Count; i++)
        {
            if (!(code[i].Kind == PgTokenKind.Word && (code[i].Is("FROM") || code[i].Is("JOIN")))) continue;
            var (c, s, t) = (code[i + 1], code[i + 3], code[i + 5]);
            if (!Name(c) || code[i + 2].Text != "." || !Name(s) || code[i + 4].Text != "." || !Name(t)) continue;
            if (i + 6 < code.Count && code[i + 6].Text == ".") continue; // a fourth part: not catalog.schema.table
            var catalog = DuckDbTokenizer.Unquote(c);
            if (string.Equals(catalog, snapshot.Database, StringComparison.OrdinalIgnoreCase)) continue;
            return $"{catalog}.{DuckDbTokenizer.Unquote(s)}.{DuckDbTokenizer.Unquote(t)} is in database {catalog}, not this tab's database ({snapshot.Database})";
        }
        return null;
    }

    static bool Name(PgToken t) => t.Kind == PgTokenKind.QuotedIdent || (t.Kind == PgTokenKind.Word && !t.Is("FROM") && !t.Is("AS") && !t.Is("SELECT"));

    static bool Qualifies(PgToken q, string table, string? alias) =>
        string.Equals(DuckDbTokenizer.Unquote(q), alias ?? table, StringComparison.OrdinalIgnoreCase);

    static IEnumerable<List<PgToken>> SplitItems(List<PgToken> items)
    {
        var cur = new List<PgToken>();
        var depth = 0;
        foreach (var t in items)
        {
            if (Opens(t)) depth++;
            else if (Closes(t)) depth--;
            // A comma inside brackets separates arguments or list elements, not select items.
            if (depth == 0 && t.Text == ",") { yield return cur; cur = []; }
            else cur.Add(t);
        }
        yield return cur;
    }
}
