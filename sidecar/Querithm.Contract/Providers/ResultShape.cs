namespace Querithm.Contract.Providers;

/// <summary>What a shape check needs to know of one engine token (trivia already dropped).</summary>
public enum ShapeTokenKind
{
    /// <summary>A bare word: keyword or unquoted identifier.</summary>
    Word,
    /// <summary>A quoted identifier; its text is already unquoted.</summary>
    QuotedName,
    /// <summary>Punctuation or an operator: ( ) , . ; …</summary>
    Punct,
    /// <summary>Strings, numbers, parameters: never a name or a keyword.</summary>
    Other,
}

/// <summary>One token of an engine's own lexer, reduced to what <see cref="ResultShape"/> reads.</summary>
public readonly record struct ShapeToken(ShapeTokenKind Kind, string Text)
{
    public bool Is(string word) => Kind == ShapeTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    public bool IsPunct(string text) => Kind == ShapeTokenKind.Punct && Text == text;
    public bool IsName => Kind is ShapeTokenKind.Word or ShapeTokenKind.QuotedName;
}

/// <summary>
/// Grid-edit audit: shapes of a row-returning statement whose driver-reported column origins cannot be trusted to name the
/// row a key identifies. PostgreSQL (table OID) and SQLite (base table name) give every alias of a self-join the same
/// table, and SQLite names the leftmost query's table for every column of a UNION. Read from the engine's own tokens
/// (never a regular expression over the text): a set operator anywhere, or a table named more than once in a FROM / JOIN
/// (subqueries, CTEs and derived tables included), refuses editing. Conservative by design: a refusal only costs an edit.
/// </summary>
public static class ResultShape
{
    /// <summary>Clause words that end a FROM list at its own nesting level (a comma after them is no longer a table).</summary>
    static readonly HashSet<string> EndsFrom = new(
        ["WHERE", "GROUP", "HAVING", "ORDER", "LIMIT", "OFFSET", "FETCH", "WINDOW", "QUALIFY", "RETURNING", "FOR", "SELECT", "VALUES", "SET"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Words a table reference may start with before its name.</summary>
    static readonly HashSet<string> Prefixes = new(["ONLY", "LATERAL"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The words that open a query inside parentheses (a subquery, a CTE body, a derived table).</summary>
    static readonly HashSet<string> Opens = new(["SELECT", "WITH", "VALUES", "FROM", "TABLE"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Why a result of <paramref name="code"/> cannot be edited (without the "Read-only: " prefix), or null.</summary>
    public static string? EditRefusal(IReadOnlyList<ShapeToken> code)
    {
        // Per open parenthesis: does it hold a query (so FROM there lists tables, not EXTRACT(x FROM y)), and is a FROM
        // list open at that level (so a comma starts another table).
        var frames = new List<(bool Query, bool InFrom)> { (true, false) };
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < code.Count; i++)
        {
            var t = code[i];
            if (t.Is("UNION") || t.Is("INTERSECT") || t.Is("EXCEPT"))
                return $"the query combines several queries ({t.Text.ToUpperInvariant()})";
            if (t.IsPunct("("))
            {
                var next = i + 1 < code.Count ? code[i + 1] : default;
                var prev = i > 0 ? code[i - 1] : default;
                var here = frames[^1];
                // Where a table may stand: FROM (a JOIN b), JOIN (…), a comma or another parenthesis inside a FROM list.
                var slot = here.Query && (prev.Is("FROM") || prev.Is("JOIN") || prev.Is("LATERAL") || (here.InFrom && (prev.IsPunct(",") || prev.IsPunct("("))));
                if (next.Kind == ShapeTokenKind.Word && Opens.Contains(next.Text)) frames.Add((true, false));
                else if (slot)
                {
                    frames.Add((true, true));
                    if (Seen(seen, TableAt(code, i + 1)) is { } again) return again;
                }
                else frames.Add((next.IsPunct("("), false));
                continue;
            }
            if (t.IsPunct(")"))
            {
                if (frames.Count > 1) frames.RemoveAt(frames.Count - 1);
                continue;
            }
            var (query, inFrom) = frames[^1];
            if (!query) continue;
            string? table = null;
            if (t.Is("FROM") || t.Is("JOIN"))
            {
                if (t.Is("FROM")) frames[^1] = (true, true);
                table = TableAt(code, i + 1);
            }
            else if (inFrom && t.IsPunct(",")) table = TableAt(code, i + 1);
            else if (inFrom && t.Kind == ShapeTokenKind.Word && EndsFrom.Contains(t.Text)) frames[^1] = (true, false);
            if (Seen(seen, table) is { } twice) return twice;
        }
        return null;
    }

    static string? Seen(Dictionary<string, string> seen, string? table)
    {
        if (table == null) return null;
        if (seen.TryGetValue(table, out var first)) return $"the same table ({first}) appears more than once in the query";
        seen[table] = table;
        return null;
    }

    /// <summary>The (last part of the) table name a FROM / JOIN / comma item at <paramref name="i"/> names; null for a subquery or a function.</summary>
    static string? TableAt(IReadOnlyList<ShapeToken> code, int i)
    {
        while (i < code.Count && code[i].Kind == ShapeTokenKind.Word && Prefixes.Contains(code[i].Text)) i++;
        if (i >= code.Count || !code[i].IsName) return null;
        var name = code[i].Text;
        while (i + 2 < code.Count && code[i + 1].IsPunct(".") && code[i + 2].IsName)
        {
            i += 2;
            name = code[i].Text;
        }
        // name( … ) is a table function, not a table.
        return i + 1 < code.Count && code[i + 1].IsPunct("(") ? null : name;
    }
}
