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
/// (never a regular expression over the text): a set operator, or a table named more than once in a FROM / JOIN (CTEs
/// and derived tables included), refuses editing. Conservative by design: a refusal only costs an edit.
/// <para>
/// One thing is left out, because it cannot change which row a result row is: a subquery used as a value or a condition
/// — in the select list, or after WHERE, HAVING, ORDER BY — of a SELECT whose FROM list has ended. Its tables feed no
/// result column (the drivers report no origin for an expression; SQLite's does for a select-list subquery, see
/// <c>selectListTraced</c>) and add no rows, so
/// <c>SELECT * FROM t WHERE v = (SELECT max(v) FROM t)</c> is as editable as <c>SELECT * FROM t</c>. A subquery inside the
/// FROM list (a derived table, a join condition), a CTE body, and a statement that is not a SELECT are still read as before.
/// </para>
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
    /// <param name="code">The statement's tokens, trivia left out.</param>
    /// <param name="selectListTraced">
    /// The driver traces a subquery in the select list to that subquery's column, so it names its table as FROM does and
    /// is not set aside. SQLite does: <c>SELECT id, (SELECT name FROM t p WHERE p.id = t.id + 1) FROM t</c> reports the
    /// second column as t.name, and the grid wrote the next row's name into this row's (regression). Its WHERE, HAVING
    /// and ORDER BY subqueries feed no column and stay aside.
    /// </param>
    public static string? EditRefusal(IReadOnlyList<ShapeToken> code, bool selectListTraced = false)
    {
        // Per open parenthesis: does it hold a query (so FROM there lists tables, not EXTRACT(x FROM y)), and is a FROM
        // list open at that level (so a comma starts another table).
        // Selecting: the level has read its SELECT keyword. Aside: the level is, or lies inside, a subquery used as a value
        // or a condition, whose tables and set operators say nothing about the rows of the result. FromSeen: the level's
        // select list has ended (its FROM came).
        var frames = new List<Frame> { new(true, false, false, false, false) };
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < code.Count; i++)
        {
            var t = code[i];
            if (!frames[^1].Aside && (t.Is("UNION") || t.Is("INTERSECT") || t.Is("EXCEPT")))
                return $"the query combines several queries ({t.Text.ToUpperInvariant()})";
            if (t.IsPunct("("))
            {
                var next = i + 1 < code.Count ? code[i + 1] : default;
                var prev = i > 0 ? code[i - 1] : default;
                var here = frames[^1];
                // Where a table may stand: FROM (a JOIN b), JOIN (…), a comma or another parenthesis inside a FROM list.
                var slot = here.Query && (prev.Is("FROM") || prev.Is("JOIN") || prev.Is("LATERAL") || (here.InFrom && (prev.IsPunct(",") || prev.IsPunct("("))));
                if (next.Kind == ShapeTokenKind.Word && Opens.Contains(next.Text)) frames.Add(new(true, false, false, here.Aside || AsAValue(frames, selectListTraced), false));
                else if (slot)
                {
                    frames.Add(new(true, true, false, here.Aside, true));
                    if (!here.Aside && Seen(seen, TableAt(code, i + 1)) is { } again) return again;
                }
                // Parentheses around an operand, ((SELECT …)) or f((SELECT …)): the query inside is a value wherever these are.
                else frames.Add(new(next.IsPunct("("), false, next.IsPunct("(") && AsAValue(frames, selectListTraced), here.Aside, true));
                continue;
            }
            if (t.IsPunct(")"))
            {
                if (frames.Count > 1) frames.RemoveAt(frames.Count - 1);
                continue;
            }
            var frame = frames[^1];
            if (!frame.Query) continue;
            string? table = null;
            if (t.Is("SELECT")) frames[^1] = frame with { Selecting = true, InFrom = false, FromSeen = false };
            else if (t.Is("FROM") || t.Is("JOIN"))
            {
                if (t.Is("FROM")) frames[^1] = frame with { InFrom = true, FromSeen = true };
                table = TableAt(code, i + 1);
            }
            else if (frame.InFrom && t.IsPunct(",")) table = TableAt(code, i + 1);
            else if (frame.InFrom && t.Kind == ShapeTokenKind.Word && EndsFrom.Contains(t.Text)) frames[^1] = frame with { InFrom = false };
            if (!frame.Aside && Seen(seen, table) is { } twice) return twice;
        }
        return null;
    }

    /// <summary>One open parenthesis (the first is the statement itself).</summary>
    readonly record struct Frame(bool Query, bool InFrom, bool Selecting, bool Aside, bool FromSeen);

    /// <summary>
    /// Whether a query opening here is a value or a condition of the SELECT around it: that SELECT — looking out through
    /// the parentheses of expressions and function calls — has read its SELECT keyword and is not inside its FROM list.
    /// A CTE body comes before the SELECT keyword, a derived table inside the FROM list, and a statement that begins with
    /// a parenthesis or is not a SELECT never reads the keyword: none of them is one. With <paramref name="selectListTraced"/>
    /// a query in the select list (before its FROM) is not one either.
    /// </summary>
    static bool AsAValue(List<Frame> frames, bool selectListTraced)
    {
        for (var k = frames.Count - 1; k >= 0; k--)
            if (frames[k].Query) return frames[k].Selecting && !frames[k].InFrom && (!selectListTraced || frames[k].FromSeen);
        return false;
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
