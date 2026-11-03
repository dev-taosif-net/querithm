using Querithm.Contract;

namespace Querithm.Intelligence.PgSql.Completion;

/// <summary>A name the body can use: routine parameter, RETURNS TABLE column, declared variable, loop record, trigger variable.</summary>
sealed record Var(string Name, string? Type, CompletionKind Kind, int Offset, string Origin)
{
    /// <summary>Record fields (FOR rec IN query, SELECT … INTO rec, %ROWTYPE, a table's row type); null when not a row.</summary>
    public Func<List<Col>>? Fields { get; init; }
}

/// <summary>
/// The names in scope inside a dollar-quoted routine or DO body (§9 tier 2 for PL/pgSQL), from tokens: the CREATE
/// FUNCTION / PROCEDURE header's parameters and RETURNS TABLE columns, DECLARE sections, FOR … IN loops, SELECT … INTO a
/// record, and NEW / OLD / TG_* in a trigger function. Block scoping is approximated: a name counts from where it is
/// declared to the end of the body.
/// </summary>
static class PlpgsqlBlockScanner
{
    static readonly HashSet<string> Modes = new(["IN", "OUT", "INOUT", "VARIADIC"], StringComparer.OrdinalIgnoreCase);

    /// <summary>First words of a type: an argument made only of these (and its modifiers) has no name.</summary>
    static readonly HashSet<string> TypeWords = new("""
        int int2 int4 int8 integer bigint smallint numeric decimal real double float float4 float8 text varchar char character bpchar
        boolean bool date time timestamp timestamptz timetz interval uuid json jsonb bytea record anyelement anyarray anycompatible
        money inet cidr macaddr oid regclass xml tsvector tsquery point setof refcursor void trigger name serial bigserial bit
        precision varying with without zone
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> DeclEnd = new(["DEFAULT", "NOT", "COLLATE"], StringComparer.OrdinalIgnoreCase);

    public static List<Var> Scan(List<Tok> header, List<Tok> body, int cursor, PgResolver r)
    {
        var vars = new List<Var>();
        Header(header, vars, r);
        Body(body, cursor, vars, r);
        // The latest declaration of a name wins (an inner block's DECLARE shadows an outer one).
        return vars.GroupBy(v => v.Name).Select(g => g.Last()).ToList();
    }

    static void Header(List<Tok> h, List<Var> vars, PgResolver r)
    {
        var k = h.FindIndex(t => t.Is("FUNCTION") || t.Is("PROCEDURE"));
        if (k < 0 || k + 1 >= h.Count || !h[k + 1].IsIdent) return;
        var chain = PgScopeBuilder.Chain(h, k + 1);
        var open = chain[^1] + 1;
        if (open >= h.Count || !h[open].IsP("(")) return;
        var match = PgScopeBuilder.Matches(h);
        var close = match[open];
        var ordinal = 0;
        foreach (var item in PgScopeBuilder.Split(h, open + 1, close))
        {
            if (item.Count == 0) continue;
            ordinal++;
            var a = item.TakeWhile(t => !t.Is("DEFAULT") && !t.IsP("=")).ToList();
            if (a.Count > 0 && a[0].Kind == PgTokenKind.Word && Modes.Contains(a[0].Text)) a.RemoveAt(0);
            if (a.Count == 0) continue;
            var named = a.Count >= 2 && !a.All(t => t.Kind != PgTokenKind.Word || TypeWords.Contains(t.Text)) && a[0].IsIdent;
            var type = PgScopeBuilder.Join(a, named ? 1 : 0, a.Count);
            vars.Add(new Var(named ? a[0].Name : $"${ordinal}", type, CompletionKind.Parameter, a[0].Offset, "parameter"));
        }
        for (var i = close + 1; i + 1 < h.Count; i++)
        {
            if (!h[i].Is("RETURNS")) continue;
            if (h[i + 1].Is("TABLE") && i + 2 < h.Count && h[i + 2].IsP("("))
            {
                foreach (var item in PgScopeBuilder.Split(h, i + 3, match[i + 2]))
                    if (item.Count >= 1 && item[0].IsIdent) vars.Add(new Var(item[0].Name, PgScopeBuilder.Join(item, 1, item.Count), CompletionKind.Parameter, item[0].Offset, "RETURNS TABLE column"));
            }
            else if (h[i + 1].Is("TRIGGER"))
            {
                // NEW / OLD are rows of whichever table the trigger is on: no fields can be listed.
                foreach (var (n, t) in new[] { ("new", "record"), ("old", "record"), ("tg_op", "text"), ("tg_name", "name"), ("tg_when", "text"), ("tg_level", "text"),
                                                ("tg_table_name", "name"), ("tg_table_schema", "name"), ("tg_relid", "oid"), ("tg_nargs", "integer"), ("tg_argv", "text[]") })
                    vars.Add(new Var(n, t, CompletionKind.Variable, h[i].Offset, "trigger variable"));
            }
            break;
        }
    }

    static void Body(List<Tok> b, int cursor, List<Var> vars, PgResolver r)
    {
        for (var i = 0; i < b.Count && b[i].Offset < cursor; i++)
        {
            if (b[i].Is("DECLARE")) i = Declare(b, i + 1, cursor, vars, r);
            else if (b[i].Is("FOR") && i + 2 < b.Count && b[i + 1].IsIdent && b[i + 2].Is("IN") && b[i + 1].Offset < cursor)
            {
                var name = b[i + 1];
                var q = i + 3;
                if (q < b.Count && b[q].Is("REVERSE")) q++;
                var loop = q;
                for (var depth = 0; loop < b.Count && !(depth == 0 && b[loop].Is("LOOP")); loop++)
                    depth += b[loop].IsP("(") ? 1 : b[loop].IsP(")") ? -1 : 0;
                var query = b.GetRange(q, loop - q);
                if (query.Count > 0 && (query[0].Is("SELECT") || query[0].Is("WITH") || query[0].Is("VALUES") || query[0].Is("TABLE")))
                    vars.Add(new Var(name.Name, "record", CompletionKind.Variable, name.Offset, "loop record") { Fields = () => r.Projection(query) });
                else if (query.Any(t => t.Text.Contains("..")))
                    vars.Add(new Var(name.Name, "integer", CompletionKind.Variable, name.Offset, "loop variable"));
                else
                    vars.Add(new Var(name.Name, "record", CompletionKind.Variable, name.Offset, "loop record"));
            }
            else if (b[i].Is("FOREACH") && i + 1 < b.Count && b[i + 1].IsIdent)
                vars.Add(new Var(b[i + 1].Name, null, CompletionKind.Variable, b[i + 1].Offset, "loop variable"));
            else if (b[i].Is("INTO") && i + 1 < b.Count)
            {
                // SELECT … INTO [STRICT] rec FROM …: the record takes the select list's columns.
                var at = b[i + 1].Is("STRICT") ? i + 2 : i + 1;
                if (at >= b.Count || !b[at].IsIdent) continue;
                var target = b[at].Name;
                var known = vars.FindLastIndex(v => v.Name == target);
                if (known < 0 || vars[known].Type is not "record") continue;
                var stmt = PgCompletionContext.BodyStatement(b, b[i].Offset);
                if (!stmt.Any(t => t.Is("SELECT"))) continue;
                vars.Add(vars[known] with { Offset = b[at].Offset, Origin = "record", Fields = () => r.Projection(stmt) });
            }
        }
    }

    /// <summary>A DECLARE section up to its BEGIN; returns the index of that BEGIN.</summary>
    static int Declare(List<Tok> b, int j, int cursor, List<Var> vars, PgResolver r)
    {
        while (j < b.Count && !b[j].Is("BEGIN"))
        {
            var end = j;
            for (var depth = 0; end < b.Count && !(depth == 0 && (b[end].IsP(";") || b[end].Is("BEGIN"))); end++)
                depth += b[end].IsP("(") ? 1 : b[end].IsP(")") ? -1 : 0;
            if (b[j].Offset < cursor && b[j].IsIdent && end - j >= 2) vars.Add(Declaration(b, j, end, r));
            j = end < b.Count && b[end].IsP(";") ? end + 1 : end;
        }
        return j;
    }

    static Var Declaration(List<Tok> b, int j, int end, PgResolver r)
    {
        var name = b[j];
        var k = j + 1;
        if (b[k].Is("ALIAS")) return new Var(name.Name, null, CompletionKind.Variable, name.Offset, "alias");
        if (b[k].Is("CURSOR") || b[k].Is("SCROLL") || (b[k].Is("NO") && k + 1 < end && b[k + 1].Is("SCROLL")))
            return new Var(name.Name, "refcursor", CompletionKind.Cursor, name.Offset, "cursor");
        if (b[k].Is("CONSTANT")) k++;
        var typeEnd = k;
        while (typeEnd < end && !b[typeEnd].IsP(":=") && !b[typeEnd].IsP("=") && !(b[typeEnd].Kind == PgTokenKind.Word && DeclEnd.Contains(b[typeEnd].Text))) typeEnd++;
        var type = PgScopeBuilder.Join(b, k, typeEnd);
        var v = new Var(name.Name, type, CompletionKind.Variable, name.Offset, "variable");
        // tbl%ROWTYPE / s.tbl%ROWTYPE / t.col%TYPE / a table's own row type.
        var pct = -1;
        for (var i = k; i < typeEnd; i++) if (b[i].IsP("%")) pct = i;
        if (pct > k && pct + 1 < typeEnd && b[k].IsIdent)
        {
            var parts = new List<string>();
            for (var i = k; i < pct; i++) if (b[i].IsIdent) parts.Add(b[i].Name);
            if (b[pct + 1].Is("ROWTYPE") && parts.Count is 1 or 2)
                return v with { Type = $"{string.Join(".", parts)}%ROWTYPE", Fields = () => r.Relation(parts.Count == 2 ? parts[0] : null, parts[^1], null, name.Offset) ?? [] };
            if (b[pct + 1].Is("TYPE") && parts.Count >= 2)
                return v with { Type = r.Relation(parts.Count == 3 ? parts[0] : null, parts[^2], null, name.Offset)?.FirstOrDefault(c => c.Name == parts[^1])?.Type ?? type };
        }
        if (typeEnd - k is 1 or 3 && b[k].IsIdent)
        {
            var chain = PgScopeBuilder.Chain(b, k);
            if (chain[^1] == typeEnd - 1 && !(b[k].Kind == PgTokenKind.Word && TypeWords.Contains(b[k].Text)))
            {
                var sch = chain.Count == 2 ? b[chain[0]].Name : null;
                var table = b[chain[^1]].Name;
                if (r.IsRelation(sch, table, null, name.Offset)) return v with { Fields = () => r.Relation(sch, table, null, name.Offset) ?? [] };
            }
        }
        return v;
    }
}
