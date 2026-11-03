using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.PgSql.Completion;

/// <summary>
/// Tier-2 completion and signature help for PostgreSQL (§9): scope-aware on tokens, not a full parse. Columns through
/// aliases, CTEs, derived tables, LATERAL and DML targets; PL/pgSQL variables, parameters and record fields; temp tables
/// the script creates; INSERT column lists; named arguments of CALL / function calls. Anything it cannot place yields
/// nothing, and the webview's tier 1 answers alone.
/// </summary>
public static class PgTier2Completion
{
    public static CompletionItem[] Complete(CompleteParams p, ISchemaSource source)
    {
        try { return Run(p, source); }
        catch { return []; }
    }

    public static SignatureHelp Signature(SignatureParams p, ISchemaSource source)
    {
        try { return Sig(p, source); }
        catch { return new SignatureHelp { Signatures = [] }; }
    }

    static SchemaSnapshot? Snapshot(ISchemaSource source, string? profileId, string? database)
    {
        try { return source.Get(profileId, database); }
        catch { return null; }
    }

    static readonly SchemaSnapshot NoSnapshot = new() { ProfileId = "", Database = "", Objects = [], Fks = [] };

    /// <summary>The snapshot's names; without one, the dialect's built-in routines alone (SQLite's work before any connection).</summary>
    static PgSchema? SchemaOf(ISchemaSource source, string? profileId, string? database) =>
        PgSchema.For(Snapshot(source, profileId, database) ?? (PgCompletionRules.Current.BuiltinRoutines.Count > 0 ? NoSnapshot : null));

    sealed class Items
    {
        readonly Dictionary<string, CompletionItem> _items = new(StringComparer.Ordinal);
        public void Add(string label, CompletionKind kind, string? detail, string sort, string? insert = null, string? doc = null)
        {
            if (label.Length == 0 || label.EndsWith(PgResolver.Unnamed, StringComparison.Ordinal) || _items.ContainsKey(label)) return;
            _items[label] = new CompletionItem
            {
                Label = label, Kind = kind, Detail = detail, SortText = sort + label, InsertText = insert == label ? null : insert, Documentation = doc,
            };
        }
        public CompletionItem[] ToArray() => _items.Values.ToArray();
    }

    static CompletionItem[] Run(CompleteParams p, ISchemaSource source)
    {
        if (string.IsNullOrEmpty(p.Text)) return [];
        var doc = PgDocument.Get(p.Text, p.DocId);
        var offset = Math.Clamp(p.Offset, 0, p.Text.Length);
        var ctx = PgCompletionContext.At(doc, offset);
        if (ctx == null) return [];
        var schema = SchemaOf(source, p.ProfileId, p.Database);
        var r = new PgResolver(schema, doc, ctx.WordStart);
        var scope = PgScopeBuilder.Build(ctx.Statement);
        var vars = ctx.InBody ? PlpgsqlBlockScanner.Scan(ctx.Header, ctx.Code, ctx.WordStart, r) : [];
        var items = new Items();
        var b = ctx.Before;
        var prev = b.Count > 0 ? b[^1] : default;

        // q.| / s.t.|
        if (b.Count >= 2 && prev.IsP(".") && b[^2].IsIdent)
        {
            var chain = new List<string> { b[^2].Name };
            for (var k = b.Count - 3; k >= 1 && b[k].IsP(".") && b[k - 1].IsIdent; k -= 2) chain.Insert(0, b[k - 1].Name);
            Qualified(chain, ctx, scope, vars, r, items);
            return items.ToArray();
        }
        if (TypePosition(ctx, b))
        {
            Types(schema, items);
            return items.ToArray();
        }
        if (prev.Is("AS") || DeclaringName(ctx, b)) return [];

        var match = PgScopeBuilder.Matches(b);
        var open = -1;
        for (var k = b.Count - 1; k >= 0; k--) if (b[k].IsP("(") && match[k] == b.Count) { open = k; break; }
        var argStart = open >= 0 && (prev.IsP("(") || prev.IsP(","));
        if (open >= 1 && argStart && InsertTarget(b, open) is { } target)
        {
            var listed = new HashSet<string>(b.Skip(open + 1).Where(t => t.IsIdent).Select(t => t.Name), PgCompletionRules.Current.Names);
            foreach (var c in r.Relation(target.Schema, target.Name, scope, b[open].Offset) ?? [])
                if (!listed.Contains(c.Name)) items.Add(c.Name, CompletionKind.Column, c.Type, "0", PgIdent.Quote(c.Name), $"{target.Name}.{c.Name}");
            return items.ToArray();
        }
        if (open >= 1 && argStart && PgCompletionRules.Current.NamedArguments && Call(b, open, schema) is { Count: > 0 } routines)
        {
            var named = new HashSet<string>(Enumerable.Range(open + 1, Math.Max(0, b.Count - open - 2)).Where(k => b[k].IsIdent && b[k + 1].IsP("=>")).Select(k => b[k].Name), PgCompletionRules.Current.Names);
            foreach (var o in routines)
                foreach (var prm in o.Params ?? [])
                    if (prm.Name.Length > 0 && (o.Kind == ObjectKind.Proc || !prm.Output) && !named.Contains(prm.Name))
                        items.Add($"{prm.Name} =>", CompletionKind.Parameter, prm.Type + (prm.HasDefault ? " (optional)" : ""), "0", $"{PgIdent.Quote(prm.Name)} => ", $"{o.Schema}.{o.Name}");
        }
        if (ObjectPosition(b))
        {
            Relations(ctx, scope, r, schema, items);
            return items.ToArray();
        }
        if (b.Count == 0 && !ctx.InBody) return items.ToArray(); // a statement starts: keywords are tier 1's
        Expression(ctx, scope, vars, r, items);
        return items.ToArray();
    }

    static void Qualified(List<string> chain, PgCompletionContext ctx, Scope scope, List<Var> vars, PgResolver r, Items items)
    {
        var visible = scope.VisibleAt(ctx.WordStart);
        if (chain.Count == 1)
        {
            var q = chain[0];
            // An unaliased name that is no relation (FROM sales.| while typing) leaves the schema reading below.
            var names = PgCompletionRules.Current.Names;
            if ((visible.FirstOrDefault(s => names.Equals(s.Label, q) && !s.InsertTarget) ?? visible.FirstOrDefault(s => names.Equals(s.Label, q))) is { } src &&
                !(src is { Kind: SourceKind.Relation, Alias: null } && !r.IsRelation(src.Schema, src.Name, scope, src.Offset)))
            {
                foreach (var c in r.Columns(src, scope)) items.Add(c.Name, CompletionKind.Column, c.Type, "0", PgIdent.Quote(c.Name), $"{src.Label}.{c.Name}");
                return;
            }
            if (vars.LastOrDefault(v => v.Name == q) is { Fields: { } fields } rec)
            {
                foreach (var c in fields()) items.Add(c.Name, CompletionKind.Column, c.Type, "0", PgIdent.Quote(c.Name), $"{rec.Name}.{c.Name}");
                return;
            }
            if (r.Relation(null, q, scope, ctx.WordStart) is { } cols)
            {
                foreach (var c in cols) items.Add(c.Name, CompletionKind.Column, c.Type, "0", PgIdent.Quote(c.Name), $"{q}.{c.Name}");
                return;
            }
            if (r.Schema is { } s && s.Schemas.Contains(q))
                foreach (var o in s.Snapshot.Objects.Where(o => names.Equals(o.Schema, q) && o.Kind is not ObjectKind.Trigger))
                    items.Add(o.Name, Kind(o), o.ReturnType ?? KindText(o), PgSchema.IsRelation(o) ? "1" : "3", PgIdent.Quote(o.Name));
            return;
        }
        if (chain.Count == 2)
            foreach (var c in r.Relation(chain[0], chain[1], scope, ctx.WordStart) ?? [])
                items.Add(c.Name, CompletionKind.Column, c.Type, "0", PgIdent.Quote(c.Name), $"{chain[0]}.{chain[1]}.{c.Name}");
    }

    static CompletionKind Kind(SnapObject o) => o.Kind switch
    {
        ObjectKind.Table => CompletionKind.Table, ObjectKind.View => CompletionKind.View, ObjectKind.Proc => CompletionKind.Proc,
        ObjectKind.Type => CompletionKind.Type, _ => CompletionKind.Func,
    };

    static string KindText(SnapObject o) => o.Kind.ToString().ToLowerInvariant();

    static void Expression(PgCompletionContext ctx, Scope scope, List<Var> vars, PgResolver r, Items items)
    {
        var visible = scope.VisibleAt(ctx.WordStart).Where(s => !s.InsertTarget || ctx.Statement.Any(t => t.Is("RETURNING") && t.Offset < ctx.WordStart)).ToList();
        // UPDATE … SET | and INSERT … RETURNING | see the target's columns.
        var labels = visible.Select(s => s.Label).Distinct().Count();
        foreach (var src in visible)
            foreach (var c in r.Columns(src, scope))
            {
                if (labels > 1) items.Add($"{src.Label}.{c.Name}", CompletionKind.Column, c.Type, "0", $"{PgIdent.Quote(src.Label)}.{PgIdent.Quote(c.Name)}", $"{src.Label}.{c.Name}");
                else items.Add(c.Name, CompletionKind.Column, c.Type, "0", PgIdent.Quote(c.Name), $"{src.Label}.{c.Name}");
            }
        foreach (var v in vars)
            items.Add(v.Name, v.Kind, v.Type, v.Kind == CompletionKind.Parameter ? "1" : "2", v.Name.StartsWith('$') ? v.Name : PgIdent.Quote(v.Name), v.Origin);
    }

    static void Relations(PgCompletionContext ctx, Scope scope, PgResolver r, PgSchema? schema, Items items)
    {
        foreach (var cte in scope.Ctes.Where(c => c.ScopeStart <= ctx.WordStart && ctx.WordStart <= c.ScopeEnd && c.Offset < ctx.WordStart))
            items.Add(cte.Name, CompletionKind.Cte, "CTE", "0", PgIdent.Quote(cte.Name));
        foreach (var t in r.Temps) items.Add(t.Name, CompletionKind.TempTable, "temp table", "1", PgIdent.Quote(t.Name));
        if (schema == null) return;
        foreach (var o in schema.Snapshot.Objects.Where(o => PgSchema.IsRelation(o) || o.Kind == ObjectKind.Tvf))
        {
            var label = o.Schema == PgCompletionRules.Current.DefaultSchema ? o.Name : $"{o.Schema}.{o.Name}";
            var insert = o.Schema == PgCompletionRules.Current.DefaultSchema ? PgIdent.Quote(o.Name) : $"{PgIdent.Quote(o.Schema)}.{PgIdent.Quote(o.Name)}";
            items.Add(label, Kind(o), KindText(o), o.Schema == PgCompletionRules.Current.DefaultSchema ? "2" : "3", insert);
        }
        foreach (var s in schema.Schemas.Where(s => s != PgCompletionRules.Current.DefaultSchema).Order()) items.Add(s, CompletionKind.Schema, "schema", "4", PgIdent.Quote(s));
    }

    static void Types(PgSchema? schema, Items items)
    {
        foreach (var t in PgCompletionRules.Current.BuiltinTypes) items.Add(t, CompletionKind.Type, "built-in type", "1");
        if (schema == null) return;
        foreach (var o in schema.Snapshot.Objects.Where(o => o.Kind == ObjectKind.Type))
            items.Add(o.Schema == PgCompletionRules.Current.DefaultSchema ? o.Name : $"{o.Schema}.{o.Name}", CompletionKind.Type, "type", "0",
                o.Schema == PgCompletionRules.Current.DefaultSchema ? PgIdent.Quote(o.Name) : $"{PgIdent.Quote(o.Schema)}.{PgIdent.Quote(o.Name)}");
    }

    /// <summary>The name slot of a DECLARE entry (DECLARE | / …; |) — a new name, nothing to offer.</summary>
    static bool DeclaringName(PgCompletionContext ctx, List<Tok> b) =>
        ctx.InBody && InDeclare(ctx, ctx.WordStart) && (b.Count == 0 || b[^1].Is("DECLARE") || b[^1].IsP(";"));

    /// <summary>A DECLARE section is open at the offset: its DECLARE comes after the last BEGIN.</summary>
    static bool InDeclare(PgCompletionContext ctx, int offset)
    {
        var declare = ctx.Code.FindLastIndex(t => t.Offset < offset && t.Is("DECLARE"));
        return declare >= 0 && !ctx.Code.Skip(declare).TakeWhile(t => t.Offset < offset).Any(t => t.Is("BEGIN"));
    }

    static bool TypePosition(PgCompletionContext ctx, List<Tok> b)
    {
        if (b.Count == 0) return false;
        if (b[^1].IsP("::")) return true;
        // DECLARE name | (the statement starts at the name in a DECLARE section).
        if (ctx.InBody && b.Count == 1 && b[0].IsIdent && InDeclare(ctx, ctx.WordStart)) return true;
        if (ctx.InBody && b.Count == 2 && b[1].Is("CONSTANT") && InDeclare(ctx, ctx.WordStart)) return true;
        // CAST(x AS |
        if (b[^1].Is("AS"))
        {
            var depth = 0;
            for (var k = b.Count - 2; k >= 1; k--)
            {
                if (b[k].IsP(")")) depth++;
                else if (b[k].IsP("(") && depth-- == 0) return b[k - 1].Is("CAST");
            }
        }
        return false;
    }

    /// <summary>INSERT INTO t [AS x] ( — the target whose column list holds the cursor.</summary>
    static (string? Schema, string Name)? InsertTarget(List<Tok> b, int open)
    {
        var k = open - 1;
        if (k >= 1 && b[k].IsIdent && b[k - 1].Is("AS")) k -= 2;
        if (k < 0 || !b[k].IsIdent) return null;
        var first = k;
        while (first - 2 >= 0 && b[first - 1].IsP(".") && b[first - 2].IsIdent) first -= 2;
        if (first < 2 || !b[first - 1].Is("INTO") || !(b[first - 2].Is("INSERT") || b[first - 2].Is("REPLACE"))) return null;
        return (first < k ? b[k - 2].Name : null, b[k].Name);
    }

    /// <summary>The routines a call at <paramref name="open"/> may be (CALL name( / name( in an expression).</summary>
    static List<SnapObject> Call(List<Tok> b, int open, PgSchema? schema)
    {
        if (schema == null || !b[open - 1].IsIdent) return [];
        var k = open - 1;
        var sch = k >= 2 && b[k - 1].IsP(".") && b[k - 2].IsIdent ? b[k - 2].Name : null;
        return schema.Routines(sch, b[k].Name).ToList();
    }

    static readonly HashSet<string> ObjectWords = new(["FROM", "JOIN", "INTO", "UPDATE", "TABLE", "ONLY", "LATERAL", "USING"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> FromEnd = new(["WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "ON", "SET", "RETURNING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "SELECT", "VALUES"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The cursor names a table: after FROM / JOIN / INTO / UPDATE …, or after a comma in a FROM list.</summary>
    static bool ObjectPosition(List<Tok> b)
    {
        if (b.Count == 0) return false;
        var prev = b[^1];
        if (prev.Kind == PgTokenKind.Word && ObjectWords.Contains(prev.Text))
        {
            if (prev.Is("INTO")) return b.Count >= 2 && (b[^2].Is("INSERT") || b[^2].Is("MERGE") || b[^2].Is("REPLACE"));
            if (prev.Is("UPDATE")) return b.Count == 1 || !(b[^2].Is("FOR") || b[^2].Is("KEY") || b[^2].Is("DO") || b[^2].Is("THEN"));
            if (prev.Is("USING")) return true;
            return true;
        }
        if (!prev.IsP(",")) return false;
        var depth = 0;
        for (var k = b.Count - 2; k >= 0; k--)
        {
            if (b[k].IsP(")")) depth++;
            else if (b[k].IsP("(")) { if (depth-- == 0) return false; }
            else if (depth == 0 && b[k].Is("FROM")) return true;
            else if (depth == 0 && b[k].Kind == PgTokenKind.Word && FromEnd.Contains(b[k].Text)) return false;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------- signature help

    static SignatureHelp Sig(SignatureParams p, ISchemaSource source)
    {
        var none = new SignatureHelp { Signatures = [] };
        if (string.IsNullOrEmpty(p.Text)) return none;
        var doc = PgDocument.Get(p.Text, p.DocId);
        var ctx = PgCompletionContext.At(doc, Math.Clamp(p.Offset, 0, p.Text.Length));
        var schema = SchemaOf(source, p.ProfileId, p.Database);
        if (ctx == null || schema == null) return none;
        var b = ctx.Before;
        var match = PgScopeBuilder.Matches(b);
        for (var open = b.Count - 1; open >= 1; open--)
        {
            if (!b[open].IsP("(") || match[open] != b.Count) continue;
            var routines = Call(b, open, schema);
            if (routines.Count == 0) continue;
            var args = PgScopeBuilder.Split(b, open + 1, b.Count);
            var current = args[^1];
            var active = args.Count - 1;
            var sigs = routines.Select(o =>
            {
                var builtin = o.Schema.Length == 0; // PgCompletionRules.BuiltinRoutines: names as written, a description
                var ps = (o.Params ?? []).Where(x => o.Kind == ObjectKind.Proc || !x.Output).OrderBy(x => x.Ordinal).ToArray();
                var labels = ps.Select(x => builtin ? x.Name : $"{(x.Name.Length > 0 ? PgIdent.Quote(x.Name) + " " : "")}{x.Type}{(x.HasDefault ? " DEFAULT …" : "")}".TrimEnd()).ToArray();
                return (Info: new SignatureInfo
                {
                    Label = builtin ? $"{o.Name}({string.Join(", ", labels)})" : $"{PgIdent.Quote(o.Schema)}.{PgIdent.Quote(o.Name)}({string.Join(", ", labels)})",
                    Documentation = builtin ? o.ReturnType : o.Kind == ObjectKind.Proc ? "procedure" : o.ReturnType is { } rt ? "returns " + rt : null,
                    Parameters = labels.Select(l => new ParameterInfo { Label = l }).ToArray(),
                }, Params: ps);
            }).ToList();
            // name => value: the parameter by name.
            if (current.Count >= 2 && current[0].IsIdent && current[1].IsP("=>"))
            {
                var name = current[0].Name;
                var hit = sigs.FindIndex(s => s.Params.Any(x => PgCompletionRules.Current.Names.Equals(x.Name, name)));
                if (hit >= 0) return new SignatureHelp { Signatures = sigs.Select(s => s.Info).ToArray(), ActiveSignature = hit, ActiveParameter = Array.FindIndex(sigs[hit].Params, x => PgCompletionRules.Current.Names.Equals(x.Name, name)) };
            }
            var best = sigs.FindIndex(s => s.Params.Length > active);
            return new SignatureHelp { Signatures = sigs.Select(s => s.Info).ToArray(), ActiveSignature = Math.Max(0, best), ActiveParameter = active };
        }
        return none;
    }
}
