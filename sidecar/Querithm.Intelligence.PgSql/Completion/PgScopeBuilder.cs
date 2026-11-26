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
    /// <summary>LATERAL (…): a derived table or function that sees the FROM items before it.</summary>
    public bool Lateral { get; init; }
    /// <summary>A derived table's closing parenthesis (int.MaxValue while it is still open).</summary>
    public int QueryEnd { get; init; } = int.MaxValue;
    public string Label => Alias ?? Name;
}

/// <summary>WITH name [(columns)] AS (body), visible in the SELECT level of its WITH.</summary>
sealed record Cte(string Name, string[]? ColumnNames, List<Tok> Body, int Offset, int ScopeStart, int ScopeEnd);

sealed class Scope
{
    public required List<Source> Sources { get; init; }
    public required List<Cte> Ctes { get; init; }

    /// <summary>
    /// The sources visible at <paramref name="offset"/>, innermost level first. A derived table of a FROM list that is not
    /// LATERAL does not see its siblings — the items of the FROM level it sits in: `JOIN (SELECT | FROM customers) d`
    /// offered the outer query's `o.*`, which PostgreSQL, MySQL, Oracle and SQLite reject there (completion audit
    /// 2026-11-25, M1). It still sees the query levels around that one, as a correlated subquery does: in `… WHERE EXISTS
    /// (SELECT 1 FROM (SELECT … WHERE o.cid = c.|) d)` every engine accepts `c` (checked on each), and the first M1 fix had
    /// hidden it too. DuckDB derived tables see their siblings as well (<see cref="PgCompletionRules.ImplicitLateral"/>).
    /// </summary>
    public List<Source> VisibleAt(int offset)
    {
        var visible = Sources.Where(s => s.ScopeStart <= offset && offset <= s.ScopeEnd).OrderBy(s => (long)s.ScopeEnd - s.ScopeStart).ToList();
        // A derived table never sees itself (its alias inside its own query, lateral or not); without LATERAL it does not
        // see its FROM level either — a level's items share its span.
        HashSet<Source>? around = null;
        HashSet<(int, int)>? sealedLevels = null;
        var implicitLateral = PgCompletionRules.Current.ImplicitLateral;
        foreach (var s in Sources)
            if (s.Kind == SourceKind.Derived && s.Offset < offset && offset <= s.QueryEnd)
            {
                (around ??= []).Add(s);
                if (!s.Lateral && !implicitLateral) (sealedLevels ??= []).Add((s.ScopeStart, s.ScopeEnd));
            }
        return around == null ? visible : visible.Where(s => !around.Contains(s) && !(sealedLevels?.Contains((s.ScopeStart, s.ScopeEnd)) ?? false)).ToList();
    }

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
        like ilike in between outer tablesample when matched on conflict straight_join
        """);

    /// <summary>The word at <paramref name="k"/> ends a FROM item instead of naming it, in every dialect or in this one (<see cref="PgCompletionRules.EndsFromItem"/>).</summary>
    static bool EndsItem(List<Tok> s, int k) =>
        s[k].Kind == PgTokenKind.Word
        && (NotAlias.Contains(s[k].Text) || PgCompletionRules.Current.EndsFromItem(s[k].Text, k + 1 < s.Count ? s[k + 1].Text : null));

    static readonly HashSet<string> ConflictWords = Set("rollback abort replace fail ignore");

    /// <summary>
    /// The index of the verb (INSERT, REPLACE, MERGE, UPDATE …) whose modifiers end at <paramref name="k"/>: MySQL's
    /// IGNORE / LOW_PRIORITY / DELAYED / HIGH_PRIORITY and SQLite's <c>OR conflict</c> are skipped where the dialect has
    /// them (<see cref="PgCompletionRules.DmlModifiers"/>); otherwise <paramref name="k"/> itself.
    /// </summary>
    internal static int VerbBefore(List<Tok> s, int k)
    {
        var rules = PgCompletionRules.Current;
        if (rules.OrConflictClause && k >= 1 && s[k].Kind == PgTokenKind.Word && ConflictWords.Contains(s[k].Text) && s[k - 1].Is("OR")) k -= 2;
        while (k >= 0 && s[k].Kind == PgTokenKind.Word && rules.DmlModifiers.Contains(s[k].Text)) k--;
        return k;
    }

    /// <summary>The first token after the modifiers of UPDATE at <paramref name="update"/> (<c>UPDATE IGNORE t</c>, <c>UPDATE OR REPLACE t</c>).</summary>
    internal static int AfterUpdateModifiers(List<Tok> s, int update)
    {
        var rules = PgCompletionRules.Current;
        var j = update + 1;
        if (rules.OrConflictClause && j + 1 < s.Count && s[j].Is("OR") && s[j + 1].Kind == PgTokenKind.Word && ConflictWords.Contains(s[j + 1].Text)) j += 2;
        while (j < s.Count && s[j].Kind == PgTokenKind.Word && rules.DmlModifiers.Contains(s[j].Text)) j++;
        return j;
    }

    /// <summary>
    /// FROM at <paramref name="k"/> belongs to <c>IS [NOT] DISTINCT FROM</c>, a comparison, not a FROM clause: it made
    /// <c>UPDATE t SET a = b IS DISTINCT FROM c, |</c> leave the SET list and named <c>c</c> a source (code audit
    /// 2026-11-25, L8).
    /// </summary>
    internal static bool IsDistinctFrom(List<Tok> s, int k) =>
        s[k].Is("FROM") && k >= 2 && s[k - 1].Is("DISTINCT") && (s[k - 2].Is("IS") || (s[k - 2].Is("NOT") && k >= 3 && s[k - 3].Is("IS")));

    /// <summary>INTO at <paramref name="into"/> names the target of INSERT / REPLACE (true for <paramref name="insert"/>) or MERGE.</summary>
    internal static bool IsTargetInto(List<Tok> s, int into, out bool insert)
    {
        insert = false;
        if (into < 1) return false;
        var v = VerbBefore(s, into - 1);
        if (v < 0) return false;
        insert = s[v].Is("INSERT") || s[v].Is("REPLACE");
        return insert || s[v].Is("MERGE");
    }

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
        // UNION / INTERSECT / EXCEPT (Oracle: MINUS) operands are separate query levels: a FROM item is visible in its own
        // operand only (CTEs keep the whole level). setOps[level] holds the operators directly at that level (-1: the statement).
        var setOps = new Dictionary<int, List<int>>();
        var setOperators = PgCompletionRules.Current.SetOperators;
        {
            var lv = new Stack<int>();
            for (var i = 0; i < s.Count; i++)
            {
                if (s[i].IsP("(")) lv.Push(i);
                else if (s[i].IsP(")")) { if (lv.Count > 0) lv.Pop(); }
                else if (s[i].Kind == PgTokenKind.Word && setOperators.Contains(s[i].Text))
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
            // Fourth round (M8): the operators are in text order — a binary search, not a scan per source (O(U²) with
            // thousands of UNION ALL branches).
            var at = ops.BinarySearch(i);
            if (at < 0) at = ~at;
            if (at > 0) start = s[ops[at - 1]].Offset;
            if (at < ops.Count) end = s[ops[at]].Offset;
            return (start, end);
        }

        // The parentheses that only group FROM items (`JOIN (b JOIN (SELECT …) d ON …)`) are no query level: before, the
        // items inside got the group's span, so a derived table there was sealed from its group but saw the items outside
        // it (code audit 2026-11-25, L5).
        var grouping = new HashSet<int>();
        for (var i = 0; i < s.Count; i++)
        {
            var t = s[i];
            if (t.IsP("(")) { if (!grouping.Contains(i)) stack.Push(i); continue; }
            if (t.IsP(")")) { if (stack.Count > 0 && match[stack.Peek()] == i) stack.Pop(); continue; }
            if (t.Is("WITH")) { Ctes(s, i, match, Span(), ctes); continue; }
            var prev = i > 0 ? s[i - 1] : default;
            var insert = false;
            var update = t.Is("UPDATE") && !(i > 0 && (prev.Is("FOR") || prev.Is("KEY") || prev.Is("DO") || prev.Is("BEFORE") || prev.Is("AFTER") || prev.Is("OR") || prev.Is("THEN") || prev.Is("INSTEAD")));
            // Oracle's CROSS / OUTER APPLY (…) d: a derived table (or function) that sees the items before it, like LATERAL
            // (code audit 2026-11-25, L9). No other engine of the family has APPLY.
            var apply = t.Is("APPLY") && (prev.Is("CROSS") || prev.Is("OUTER"));
            // STRAIGHT_JOIN: MySQL's JOIN that keeps the table order (a reserved word there, never a name in practice elsewhere).
            var intro = (t.Is("FROM") && !IsDistinctFrom(s, i)) || t.Is("JOIN") || t.Is("STRAIGHT_JOIN") || update
                        || (t.Is("INTO") && IsTargetInto(s, i, out insert))
                        // USING (cols) is a join condition, and DuckDB's USING SAMPLE a sample clause; else USING names a source.
                        || (t.Is("USING") && !(i + 1 < s.Count && (s[i + 1].IsP("(") || EndsItem(s, i + 1))))
                        || (t.Is("TABLE") && i == 0)
                        || apply;
            if (!intro) continue;
            var span = OperandSpan(i);
            var j = Item(s, update ? AfterUpdateModifiers(s, i) : i + 1, match, span, sources, insert, grouping, lateral: apply);
            // FROM a, b, LATERAL (…) c: more items at the same level; also MySQL's multi-table UPDATE a, b SET (code audit
            // 2026-11-25, M8).
            if (t.Is("FROM") || t.Is("USING") || (update && PgCompletionRules.Current.MultiTableUpdate))
                while (j < s.Count && s[j].IsP(",")) j = Item(s, j + 1, match, span, sources, insert: false, grouping);
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
    static int Item(List<Tok> s, int j, int[] match, (int Start, int End) span, List<Source> sources, bool insert, HashSet<int> grouping, bool lateral = false)
    {
        // (a JOIN b …) groups items: the first item inside is read here, later ones are introduced by their JOIN at the same
        // level, and the item ends after the outermost group's ')'. A loop, not one call per '(' (third round: `FROM ((((…`
        // nested 100,000 deep overflowed the stack and ended the process).
        var group = -1;
        while (true)
        {
            while (j < s.Count && (s[j].Is("ONLY") || s[j].Is("LATERAL"))) { lateral |= s[j].Is("LATERAL"); j++; }
            if (j >= s.Count) return group >= 0 ? Math.Min(match[group] + 1, s.Count) : j;
            if (!s[j].IsP("(")) break;
            var inner = j + 1 < s.Count ? s[j + 1] : default;
            if (inner.Is("SELECT") || inner.Is("WITH") || inner.Is("VALUES") || inner.Is("TABLE")) break;
            grouping.Add(j);
            if (group < 0) group = j;
            insert = false;
            j++;
        }
        var end = ItemAt(s, j, match, span, sources, insert, lateral);
        return group >= 0 ? Math.Min(match[group] + 1, s.Count) : end;
    }

    /// <summary>One FROM item at <paramref name="j"/> that is no group: a derived table, a function or a relation.</summary>
    static int ItemAt(List<Tok> s, int j, int[] match, (int Start, int End) span, List<Source> sources, bool insert, bool lateral)
    {
        Source? src;
        int after;
        if (s[j].IsP("("))
        {
            var close = match[j];
            {
                src = new Source
                {
                    Kind = SourceKind.Derived, Name = "", Offset = s[j].Offset, ScopeStart = span.Start, ScopeEnd = span.End, Query = s.GetRange(j + 1, Math.Min(close, s.Count) - j - 1),
                    Lateral = lateral, QueryEnd = close < s.Count ? s[close].Offset : int.MaxValue,
                };
                after = close + 1;
            }
        }
        // Fourth round (L7): a word that ends a FROM item in this dialect (DuckDB QUALIFY, Oracle START …) names no item.
        else if (s[j].IsIdent && !EndsItem(s, j))
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
        if (after < s.Count && s[after].IsIdent && !EndsItem(s, after))
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
