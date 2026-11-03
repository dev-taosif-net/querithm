using Querithm.Contract;

namespace Querithm.Intelligence.PgSql.Completion;

enum SourceKind { Relation, Derived, Function }

/// <summary>
/// A FROM item or DML target: what the statement calls it, where its columns come from, and the SELECT level that
/// declares it (offsets; a source is visible inside that level, nested subqueries included).
/// </summary>
sealed class Source
{
    public required SourceKind Kind { get; init; }
    public string? Schema { get; init; }
    public required string Name { get; init; }
    public string? Alias { get; set; }
    public int Offset { get; init; }
    public int ScopeStart { get; init; }
    public int ScopeEnd { get; init; }
    public List<Tok>? Query { get; init; }
    /// <summary>AS x(a, b): new names for the first columns.</summary>
    public string[]? ColumnAliases { get; set; }
    /// <summary>AS x(a int, b text): a set-returning function's column definition list.</summary>
    public List<Col>? ColumnDefs { get; set; }
    public bool InsertTarget { get; init; }
    public string Label => Alias ?? Name;
}

/// <summary>WITH name [(columns)] AS (body), visible in the SELECT level of its WITH.</summary>
sealed record Cte(string Name, string[]? ColumnNames, List<Tok> Body, int Offset, int ScopeStart, int ScopeEnd);

sealed class Scope
{
    public required List<Source> Sources { get; init; }
    public required List<Cte> Ctes { get; init; }

    /// <summary>The sources visible at <paramref name="offset"/>, innermost level first.</summary>
    public List<Source> VisibleAt(int offset) =>
        Sources.Where(s => s.ScopeStart <= offset && offset <= s.ScopeEnd).OrderBy(s => (long)s.ScopeEnd - s.ScopeStart).ToList();

    public Cte? CteAt(string name, int offset) =>
        Ctes.Where(c => PgCompletionRules.Current.Names.Equals(c.Name, name) && c.ScopeStart <= offset && offset <= c.ScopeEnd).OrderBy(c => (long)c.ScopeEnd - c.ScopeStart).FirstOrDefault();
}

/// <summary>
/// Statement scopes from <see cref="PgTokenizer"/> tokens (§9 tier 2 for PostgreSQL), never a parse tree: FROM / JOIN
/// sources with aliases, CTEs with explicit or inferred columns, derived tables, LATERAL, set-returning functions and
/// the targets of INSERT / UPDATE / DELETE / MERGE (whose columns RETURNING sees).
/// </summary>
static class PgScopeBuilder
{
    static HashSet<string> Set(string words) => new(words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>Words that end a FROM item instead of naming it.</summary>
    internal static readonly HashSet<string> NotAlias = Set("""
        where join inner left right full cross on using group order having limit offset union except intersect set values returning
        window for natural lateral and or select from as with do into default overriding fetch then else end when loop is not null
        like ilike in between outer tablesample when matched on conflict
        """);

    /// <summary>Index of the matching ')' for every '(' (the statement end when a parenthesis is still open).</summary>
    internal static int[] Matches(List<Tok> s)
    {
        var m = new int[s.Count];
        var stack = new Stack<int>();
        for (var i = 0; i < s.Count; i++)
        {
            m[i] = -1;
            if (s[i].IsP("(")) stack.Push(i);
            else if (s[i].IsP(")") && stack.Count > 0) m[stack.Pop()] = i;
        }
        while (stack.Count > 0) m[stack.Pop()] = s.Count;
        return m;
    }

    /// <summary>A dotted name starting at <paramref name="i"/>: the identifier indices.</summary>
    internal static List<int> Chain(List<Tok> s, int i)
    {
        var parts = new List<int> { i };
        while (parts[^1] + 2 < s.Count && s[parts[^1] + 1].IsP(".") && s[parts[^1] + 2].IsIdent) parts.Add(parts[^1] + 2);
        return parts;
    }

    public static Scope Build(List<Tok> s)
    {
        var match = Matches(s);
        var sources = new List<Source>();
        var ctes = new List<Cte>();
        var stack = new Stack<int>();
        (int, int) Span() => stack.Count == 0 ? (int.MinValue, int.MaxValue)
            : (s[stack.Peek()].Offset, match[stack.Peek()] < s.Count ? s[match[stack.Peek()]].Offset : int.MaxValue);
        // UNION / INTERSECT / EXCEPT operands are separate query levels: a FROM item is visible in its own operand only
        // (CTEs keep the whole level). setOps[level] holds the operators directly at that level (-1: the statement).
        var setOps = new Dictionary<int, List<int>>();
        {
            var lv = new Stack<int>();
            for (var i = 0; i < s.Count; i++)
            {
                if (s[i].IsP("(")) lv.Push(i);
                else if (s[i].IsP(")")) { if (lv.Count > 0) lv.Pop(); }
                else if (s[i].Is("UNION") || s[i].Is("INTERSECT") || s[i].Is("EXCEPT"))
                {
                    var key = lv.Count == 0 ? -1 : lv.Peek();
                    if (!setOps.TryGetValue(key, out var ops)) setOps[key] = ops = [];
                    ops.Add(i);
                }
            }
        }
        (int, int) OperandSpan(int i)
        {
            var (start, end) = Span();
            if (!setOps.TryGetValue(stack.Count == 0 ? -1 : stack.Peek(), out var ops)) return (start, end);
            foreach (var op in ops)
            {
                if (op < i) start = s[op].Offset;
                else { end = s[op].Offset; break; }
            }
            return (start, end);
        }

        for (var i = 0; i < s.Count; i++)
        {
            var t = s[i];
            if (t.IsP("(")) { stack.Push(i); continue; }
            if (t.IsP(")")) { if (stack.Count > 0) stack.Pop(); continue; }
            if (t.Is("WITH")) { Ctes(s, i, match, Span(), ctes); continue; }
            var prev = i > 0 ? s[i - 1] : default;
            var intro = t.Is("FROM") || t.Is("JOIN")
                        || (t.Is("UPDATE") && !(i > 0 && (prev.Is("FOR") || prev.Is("KEY") || prev.Is("DO") || prev.Is("BEFORE") || prev.Is("AFTER") || prev.Is("OR") || prev.Is("THEN") || prev.Is("INSTEAD"))))
                        || (t.Is("INTO") && i > 0 && (prev.Is("INSERT") || prev.Is("MERGE")))
                        || (t.Is("USING") && !(i + 1 < s.Count && s[i + 1].IsP("(")))
                        || (t.Is("TABLE") && i == 0);
            if (!intro) continue;
            var span = OperandSpan(i);
            var j = Item(s, i + 1, match, span, sources, insert: t.Is("INTO") && prev.Is("INSERT"));
            // FROM a, b, LATERAL (…) c: more items at the same level.
            if (t.Is("FROM") || t.Is("USING"))
                while (j < s.Count && s[j].IsP(",")) j = Item(s, j + 1, match, span, sources, insert: false);
        }
        return new Scope { Sources = sources, Ctes = ctes };
    }

    static void Ctes(List<Tok> s, int with, int[] match, (int Start, int End) span, List<Cte> ctes)
    {
        var i = with + 1;
        if (i < s.Count && s[i].Is("RECURSIVE")) i++;
        while (i < s.Count && s[i].IsIdent)
        {
            var name = s[i];
            var j = i + 1;
            string[]? cols = null;
            if (j < s.Count && s[j].IsP("("))
            {
                var close = match[j];
                cols = Split(s, j + 1, close).Select(r => r.Count > 0 && r[0].IsIdent ? r[0].Name : "").ToArray();
                j = close + 1;
            }
            if (j >= s.Count || !s[j].Is("AS")) return; // WITH ORDINALITY / WITH TIME ZONE / WITH (options): not a CTE
            j++;
            if (j < s.Count && s[j].Is("NOT")) j++;
            if (j < s.Count && s[j].Is("MATERIALIZED")) j++;
            if (j >= s.Count || !s[j].IsP("(")) return;
            var end = Math.Min(match[j], s.Count);
            ctes.Add(new Cte(name.Name, cols, s.GetRange(j + 1, end - j - 1), name.Offset, span.Start, span.End));
            i = end + 1;
            if (i < s.Count && s[i].IsP(",")) i++;
            else return;
        }
    }

    /// <summary>Items between <paramref name="from"/> and <paramref name="to"/> split at their own level's commas.</summary>
    internal static List<List<Tok>> Split(List<Tok> s, int from, int to)
    {
        var list = new List<List<Tok>>();
        var cur = new List<Tok>();
        var depth = 0;
        for (var i = from; i < Math.Min(to, s.Count); i++)
        {
            if (s[i].IsP("(")) depth++;
            else if (s[i].IsP(")")) depth--;
            if (depth == 0 && s[i].IsP(",")) { list.Add(cur); cur = []; continue; }
            cur.Add(s[i]);
        }
        list.Add(cur);
        return list;
    }

    /// <summary>One FROM item at <paramref name="j"/>; returns the index after it.</summary>
    static int Item(List<Tok> s, int j, int[] match, (int Start, int End) span, List<Source> sources, bool insert)
    {
        while (j < s.Count && (s[j].Is("ONLY") || s[j].Is("LATERAL"))) j++;
        if (j >= s.Count) return j;
        Source? src;
        int after;
        if (s[j].IsP("("))
        {
            var close = match[j];
            var first = j + 1 < s.Count ? s[j + 1] : default;
            if (first.Is("SELECT") || first.Is("WITH") || first.Is("VALUES") || first.Is("TABLE"))
            {
                src = new Source { Kind = SourceKind.Derived, Name = "", Offset = s[j].Offset, ScopeStart = span.Start, ScopeEnd = span.End, Query = s.GetRange(j + 1, Math.Min(close, s.Count) - j - 1) };
                after = close + 1;
            }
            else
            {
                // (a JOIN b …): the first item inside; later ones are introduced by their JOIN.
                Item(s, j + 1, match, span, sources, false);
                return Math.Min(close + 1, s.Count);
            }
        }
        else if (s[j].IsIdent && !(s[j].Kind == PgTokenKind.Word && NotAlias.Contains(s[j].Text)))
        {
            var chain = Chain(s, j);
            var last = chain[^1];
            var schema = chain.Count >= 2 ? s[chain[^2]].Name : null;
            after = last + 1;
            if (!insert && after < s.Count && s[after].IsP("("))
            {
                src = new Source { Kind = SourceKind.Function, Schema = schema, Name = s[last].Name, Offset = s[last].Offset, ScopeStart = span.Start, ScopeEnd = span.End };
                after = match[after] + 1;
                if (after + 1 < s.Count && s[after].Is("WITH") && s[after + 1].Is("ORDINALITY")) after += 2;
            }
            else src = new Source { Kind = SourceKind.Relation, Schema = schema, Name = s[last].Name, Offset = s[last].Offset, ScopeStart = span.Start, ScopeEnd = span.End, InsertTarget = insert };
        }
        else return j;

        if (after < s.Count && s[after].Is("AS")) after++;
        if (after < s.Count && s[after].IsIdent && !(s[after].Kind == PgTokenKind.Word && NotAlias.Contains(s[after].Text)))
        {
            src.Alias = s[after].Name;
            after++;
            if (!src.InsertTarget && after < s.Count && s[after].IsP("("))
            {
                var close = match[after];
                var items = Split(s, after + 1, close).Where(r => r.Count > 0 && r[0].IsIdent).ToList();
                if (items.Any(r => r.Count > 1)) src.ColumnDefs = items.Select(r => new Col(r[0].Name, Join(r, 1, r.Count))).ToList();
                else src.ColumnAliases = items.Select(r => r[0].Name).ToArray();
                after = close + 1;
            }
        }
        sources.Add(src);
        return Math.Min(after, s.Count);
    }

    /// <summary>Tokens as SQL text: no space around '.', '::', '%' or inside brackets and parentheses.</summary>
    internal static string? Join(List<Tok> s, int from, int to)
    {
        if (from >= to) return null;
        var sb = new System.Text.StringBuilder();
        for (var i = from; i < to; i++)
        {
            var t = s[i];
            var tight = i == from || t.Text is "." or "::" or "%" or "(" or ")" or "," or "[" or "]" || s[i - 1].Text is "." or "::" or "%" or "(" or "[" or ",";
            if (!tight) sb.Append(' ');
            sb.Append(t.Text);
            if (t.Text == ",") sb.Append(' ');
        }
        return sb.ToString().Replace(", )", ")");
    }
}
