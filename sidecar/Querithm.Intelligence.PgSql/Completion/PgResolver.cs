using Querithm.Contract;

namespace Querithm.Intelligence.PgSql.Completion;

/// <summary>
/// Columns of sources, CTEs, temp tables and subqueries: the snapshot for real relations, the script for the rest.
/// Projection names follow PostgreSQL (an alias, else the column or function name; casts keep the inner name).
/// </summary>
sealed class PgResolver(PgSchema? schema, PgDocument doc, int cursor)
{
    public PgSchema? Schema => schema;
    public PgDocument Doc => doc;
    readonly Dictionary<TempTable, List<Col>> _temps = [];
    readonly HashSet<object> _resolving = [];
    int _depth;

    /// <summary>The temp table the script created last before the cursor under this name.</summary>
    public TempTable? Temp(string name) => doc.TempTables.LastOrDefault(t => doc.Rules.Names.Equals(t.Name, name) && t.Offset < cursor);

    public IEnumerable<TempTable> Temps => doc.TempTables.Where(t => t.Offset < cursor).GroupBy(t => t.Name).Select(g => g.Last());

    /// <summary>Guards recursion (a recursive CTE reading itself, a temp table AS SELECT from itself).</summary>
    T Guard<T>(object key, Func<T> work, T fallback)
    {
        if (_depth > 8 || !_resolving.Add(key)) return fallback;
        _depth++;
        try { return work(); }
        finally { _depth--; _resolving.Remove(key); }
    }

    public List<Col> Columns(Source src, Scope scope)
    {
        var cols = src.Kind switch
        {
            SourceKind.Derived => Guard(src, () => Projection(src.Query!, scope), []),
            SourceKind.Function => src.ColumnDefs ?? FunctionColumns(src),
            _ => Relation(src.Schema, src.Name, scope, src.Offset) ?? [],
        };
        if (src.ColumnAliases is { Length: > 0 } names)
            cols = cols.Select((c, i) => i < names.Length && names[i].Length > 0 ? c with { Name = names[i] } : c).ToList();
        return cols;
    }

    List<Col> FunctionColumns(Source src)
    {
        var routines = schema?.Routines(src.Schema, src.Name).ToList() ?? [];
        if (routines.FirstOrDefault(o => o.Columns is { Length: > 0 }) is { } tvf) return tvf.Columns!.Select(c => new Col(c.Name, c.Type)).ToList();
        // A scalar function in FROM yields one column named after the alias (or the function).
        var scalar = routines.FirstOrDefault();
        return scalar == null ? [] : [new Col(src.Alias ?? src.Name, scalar.ReturnType)];
    }

    /// <summary>A name in FROM: a CTE in scope, a temp table of the script, then the snapshot.</summary>
    public List<Col>? Relation(string? schemaName, string name, Scope? scope, int at)
    {
        if (schemaName == null && scope?.CteAt(name, at) is { } cte) return CteColumns(cte, scope);
        if (schemaName is null or "pg_temp" && Temp(name) is { } temp) return TempColumns(temp);
        if (schema?.Find(schemaName, name, schema.IsRowset) is { } o && schema.Resolve(o) is { Columns: { } cols }) return cols.Select(c => new Col(c.Name, c.Type)).ToList();
        return null;
    }

    public bool IsRelation(string? schemaName, string name, Scope? scope, int at) =>
        (schemaName == null && scope?.CteAt(name, at) != null) || (schemaName is null or "pg_temp" && Temp(name) != null) ||
        schema?.Find(schemaName, name, schema.IsRowset) != null;

    /// <summary>A CTE's columns; its body also sees the CTEs of <paramref name="scope"/> (a later CTE reading an earlier one).</summary>
    public List<Col> CteColumns(Cte cte, Scope? scope = null) => Guard(cte, () =>
    {
        var cols = Projection(cte.Body, scope);
        if (cte.ColumnNames is { Length: > 0 } names)
            cols = names.Select((n, i) => new Col(n, i < cols.Count ? cols[i].Type : null)).Where(c => c.Name.Length > 0).ToList();
        return cols;
    }, []);

    public List<Col> TempColumns(TempTable t)
    {
        if (_temps.TryGetValue(t, out var cached)) return cached;
        var cols = Guard(t, () =>
        {
            var d = t.Definition;
            if (d.Count == 0) return new List<Col>();
            if (d[0].IsP("("))
            {
                var match = PgScopeBuilder.Matches(d);
                var list = new List<Col>();
                foreach (var item in PgScopeBuilder.Split(d, 1, match[0]))
                {
                    if (item.Count == 0 || !item[0].IsIdent) continue;
                    if (item[0].Is("LIKE") && item.Count >= 2)
                    {
                        var chain = PgScopeBuilder.Chain(item, 1);
                        list.AddRange(Relation(chain.Count >= 2 ? item[chain[^2]].Name : null, item[chain[^1]].Name, null, t.Offset) ?? []);
                        continue;
                    }
                    if (item[0].Kind == PgTokenKind.Word && ConstraintWords.Contains(item[0].Text)) continue;
                    var end = 1;
                    while (end < item.Count && !(item[end].Kind == PgTokenKind.Word && TypeEnd.Contains(item[end].Text))) end++;
                    list.Add(new Col(item[0].Name, PgScopeBuilder.Join(item, 1, end)));
                }
                return list;
            }
            var asAt = d.FindIndex(x => x.Is("AS"));
            return asAt >= 0 ? Projection(d.GetRange(asAt + 1, d.Count - asAt - 1)) : [];
        }, []);
        _temps[t] = cols;
        return cols;
    }

    static readonly HashSet<string> ConstraintWords = new(["CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN", "EXCLUDE"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> TypeEnd = new(["NOT", "NULL", "DEFAULT", "PRIMARY", "REFERENCES", "CHECK", "UNIQUE", "CONSTRAINT", "GENERATED", "COLLATE"], StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> ListEnd = new(["FROM", "INTO", "WHERE", "GROUP", "ORDER", "LIMIT", "UNION", "EXCEPT", "INTERSECT", "HAVING", "WINDOW", "OFFSET", "FETCH", "FOR", "RETURNING", "ON", "LOOP"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The output columns of a query (SELECT list, VALUES, TABLE, or a DML statement's RETURNING). A nested query also
    /// sees the CTEs of its enclosing <paramref name="outer"/> scope; its own CTEs shadow them.
    /// </summary>
    public List<Col> Projection(List<Tok> q, Scope? outer = null)
    {
        if (q.Count == 0) return [];
        var verb = -1;
        var depth = 0;
        for (var i = 0; i < q.Count && verb < 0; i++)
        {
            if (q[i].IsP("(")) depth++;
            else if (q[i].IsP(")")) depth--;
            else if (depth == 0 && (q[i].Is("SELECT") || q[i].Is("VALUES") || q[i].Is("TABLE") || q[i].Is("INSERT") || q[i].Is("UPDATE") || q[i].Is("DELETE") || q[i].Is("MERGE")))
                verb = i;
        }
        if (verb < 0) return [];
        var scope = PgScopeBuilder.Build(q);
        if (outer is { Ctes.Count: > 0 }) scope = new Scope { Sources = scope.Sources, Ctes = [.. scope.Ctes, .. outer.Ctes] };
        if (q[verb].Is("VALUES"))
        {
            if (verb + 1 >= q.Count || !q[verb + 1].IsP("(")) return [];
            var row = PgScopeBuilder.Split(q, verb + 2, PgScopeBuilder.Matches(q)[verb + 1]);
            return row.Select((item, i) => new Col($"column{i + 1}", Literal(item))).ToList();
        }
        if (q[verb].Is("TABLE"))
        {
            if (verb + 1 >= q.Count || !q[verb + 1].IsIdent) return [];
            var chain = PgScopeBuilder.Chain(q, verb + 1);
            return Relation(chain.Count >= 2 ? q[chain[^2]].Name : null, q[chain[^1]].Name, scope, q[verb].Offset) ?? [];
        }
        var start = verb + 1;
        if (!q[verb].Is("SELECT"))
        {
            // Data-modifying CTE: its RETURNING list.
            start = -1;
            depth = 0;
            for (var i = verb; i < q.Count; i++)
            {
                if (q[i].IsP("(")) depth++;
                else if (q[i].IsP(")")) depth--;
                else if (depth == 0 && q[i].Is("RETURNING")) { start = i + 1; break; }
            }
            if (start < 0) return [];
        }
        if (start < q.Count && (q[start].Is("DISTINCT") || q[start].Is("ALL")))
        {
            start++;
            if (start + 1 < q.Count && q[start].Is("ON") && q[start + 1].IsP("(")) start = PgScopeBuilder.Matches(q)[start + 1] + 1;
        }
        var end = start;
        depth = 0;
        for (; end < q.Count; end++)
        {
            if (q[end].IsP("(")) depth++;
            else if (q[end].IsP(")")) depth--;
            else if (depth == 0 && (q[end].IsP(";") || (q[end].Kind == PgTokenKind.Word && ListEnd.Contains(q[end].Text) && !(q[end].Is("ON") && start == end)))) break;
        }
        var cols = new List<Col>();
        foreach (var item in PgScopeBuilder.Split(q, start, end))
        {
            if (item.Count == 0) continue;
            if (item.Count == 1 && item[0].IsP("*"))
            {
                foreach (var src in scope.VisibleAt(item[0].Offset).Where(s => !s.InsertTarget)) cols.AddRange(Columns(src, scope));
                continue;
            }
            if (item.Count == 3 && item[0].IsIdent && item[1].Text == ".*" || item.Count >= 2 && item[^1].IsP(".*"))
            {
                var label = item[0].Name;
                if (scope.VisibleAt(item[0].Offset).FirstOrDefault(s => s.Label == label) is { } src) cols.AddRange(Columns(src, scope));
                continue;
            }
            if (Item(item, scope) is { } c) cols.Add(c);
        }
        return cols;
    }

    static readonly HashSet<string> NotAliasWords = new(["END", "NULL", "TRUE", "FALSE", "ASC", "DESC", "AND", "OR", "NOT", "IS", "THEN", "ELSE"], StringComparer.OrdinalIgnoreCase);

    public const string Unnamed = "?column?";

    /// <summary>One select-list item: its output name (<see cref="Unnamed"/> where PostgreSQL has none) and type.</summary>
    Col? Item(List<Tok> it, Scope scope)
    {
        string? alias = null;
        var expr = it;
        if (it.Count >= 2 && it[^2].Is("AS") && it[^1].IsIdent) { alias = it[^1].Name; expr = it[..^2]; }
        else if (it.Count >= 2 && it[^1].IsIdent && !(it[^1].Kind == PgTokenKind.Word && (NotAliasWords.Contains(it[^1].Text) || PgCompletionRules.Current.IsReserved(it[^1].Text)))
                 && (it[^2].IsIdent || it[^2].IsP(")") || it[^2].Kind is PgTokenKind.Number or PgTokenKind.String))
        { alias = it[^1].Name; expr = it[..^1]; }
        var (name, type) = Expr(expr, scope);
        // Unnamed (?column?) still holds its place for an alias list: (SELECT 1, 2) AS d(a, b).
        return new Col(alias ?? name ?? Unnamed, type);
    }

    (string? Name, string? Type) Expr(List<Tok> e, Scope scope)
    {
        if (e.Count == 0) return (null, null);
        // expr::type — the cast decides the type; the name is the inner expression's.
        var depth = 0;
        for (var i = e.Count - 1; i > 0; i--)
        {
            if (e[i].IsP(")")) depth++;
            else if (e[i].IsP("(")) depth--;
            else if (depth == 0 && e[i].IsP("::")) return (Expr(e[..i], scope).Name, PgScopeBuilder.Join(e, i + 1, e.Count));
        }
        if (e[0].IsIdent && PgScopeBuilder.Chain(e, 0) is var chain && chain[^1] == e.Count - 1)
            return (e[^1].Name, ColumnType(chain.Select(k => e[k].Name).ToList(), scope, e[0].Offset));
        if (e[0].Is("CAST") && e.Count > 2 && e[1].IsP("("))
        {
            var close = PgScopeBuilder.Matches(e)[1];
            var asAt = -1;
            depth = 0;
            for (var i = 2; i < close; i++)
            {
                if (e[i].IsP("(")) depth++;
                else if (e[i].IsP(")")) depth--;
                else if (depth == 0 && e[i].Is("AS")) asAt = i;
            }
            return asAt < 0 ? (null, null) : (Expr(e.GetRange(2, asAt - 2), scope).Name, PgScopeBuilder.Join(e, asAt + 1, close));
        }
        if (e[0].Is("CASE")) return ("case", null);
        if (e[0].IsIdent && PgScopeBuilder.Chain(e, 0) is var fn && fn[^1] + 1 < e.Count && e[fn[^1] + 1].IsP("(") && PgScopeBuilder.Matches(e)[fn[^1] + 1] == e.Count - 1)
        {
            var fname = e[fn[^1]].Name;
            var args = PgScopeBuilder.Split(e, fn[^1] + 2, e.Count - 1);
            return (fname, FunctionType(fn.Count >= 2 ? e[fn[^2]].Name : null, fname, args, scope));
        }
        return e.Count == 1 ? (null, Literal(e)) : (null, null);
    }

    static string? Literal(List<Tok> e) => e.Count != 1 ? null : e[0] switch
    {
        { Kind: PgTokenKind.Number } n => n.Text.Contains('.') || n.Text.Contains('e') || n.Text.Contains('E') ? "numeric" : "integer",
        { Kind: PgTokenKind.String } => "text",
        var w when w.Is("TRUE") || w.Is("FALSE") => "boolean",
        _ => null,
    };

    string? FunctionType(string? schemaName, string name, List<List<Tok>> args, Scope scope)
    {
        switch (name)
        {
            case "count" or "row_number" or "rank" or "dense_rank" or "ntile": return "bigint";
            case "sum" or "avg": return "numeric";
            case "now" or "statement_timestamp" or "clock_timestamp" or "transaction_timestamp": return "timestamp with time zone";
            case "lower" or "upper" or "concat" or "concat_ws" or "format" or "string_agg" or "trim" or "substring" or "replace" or "left" or "right" or "md5": return "text";
            case "json_agg" or "json_build_object" or "to_json": return "json";
            case "jsonb_agg" or "jsonb_build_object" or "to_jsonb": return "jsonb";
            case "gen_random_uuid": return "uuid";
            case "length" or "char_length": return "integer";
            case "coalesce" or "max" or "min" or "nullif" or "greatest" or "least" or "abs" or "round":
                return args.Count > 0 ? Expr(args[0], scope).Type : null;
        }
        return schema?.Routines(schemaName, name).FirstOrDefault(o => o.ReturnType != null)?.ReturnType;
    }

    /// <summary>col / q.col / s.t.col against the sources visible at <paramref name="at"/>.</summary>
    public string? ColumnType(List<string> parts, Scope scope, int at)
    {
        var col = parts[^1];
        if (parts.Count == 1)
        {
            foreach (var src in scope.VisibleAt(at).Where(s => !s.InsertTarget))
                if (Columns(src, scope).FirstOrDefault(c => doc.Rules.Names.Equals(c.Name, col)) is { } c) return c.Type;
            return null;
        }
        if (parts.Count == 2 && scope.VisibleAt(at).FirstOrDefault(s => s.Label == parts[0]) is { } q)
            return Columns(q, scope).FirstOrDefault(c => doc.Rules.Names.Equals(c.Name, col))?.Type;
        var table = parts[^2];
        var sch = parts.Count >= 3 ? parts[^3] : null;
        return Relation(sch, table, scope, at)?.FirstOrDefault(c => doc.Rules.Names.Equals(c.Name, col))?.Type;
    }
}
