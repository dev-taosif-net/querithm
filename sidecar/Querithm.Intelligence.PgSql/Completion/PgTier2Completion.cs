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
    /// <summary>
    /// An empty answer leaves tier 1 alone. A cancelled request (<paramref name="ct"/>: a newer one for the document, or
    /// its deadline) throws instead, so the webview never caches an empty answer for a word it still shows.
    /// </summary>
    public static CompletionItem[] Complete(CompleteParams p, ISchemaSource source, CancellationToken ct)
    {
        using var cancel = Tier2Cancellation.Use(ct); // the tokenizers and splitters below stop on it too
        try { return Run(p, source, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Report(e); return []; }
    }

    public static SignatureHelp Signature(SignatureParams p, ISchemaSource source, CancellationToken ct)
    {
        using var cancel = Tier2Cancellation.Use(ct);
        try { return Sig(p, source, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Report(e); return new SignatureHelp { Signatures = [] }; }
    }

    /// <summary>
    /// Told about an exception tier 2 swallowed (the answer is then empty and tier 1 answers alone): the host logs it, so a
    /// resolver bug is no longer only a missing list (completion audit 2026-11-25, low). The message may hold document
    /// text — the host scrubs it and logs at Debug. Never throws back into completion.
    /// </summary>
    public static Action<Exception>? OnError { get; set; }

    static void Report(Exception e)
    {
        try { OnError?.Invoke(e); }
        catch { /* a logging failure must not change the answer */ }
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

    static CompletionItem[] Run(CompleteParams p, ISchemaSource source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(p.Text)) return [];
        ct.ThrowIfCancellationRequested();
        var doc = PgDocument.Get(p.Text, p.DocId);
        var offset = Math.Clamp(p.Offset, 0, p.Text.Length);
        var ctx = PgCompletionContext.At(doc, offset);
        if (ctx == null) return [];
        var schema = SchemaOf(source, p.ProfileId, p.Database);
        var r = new PgResolver(schema, doc, ctx.WordStart, ct);
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
                    items.Add(o.Name, Kind(o), o.ReturnType ?? KindText(o), s.IsRowset(o) ? "1" : "3", PgIdent.Quote(o.Name));
            return;
        }
        if (chain.Count == 2)
            foreach (var c in r.Relation(chain[0], chain[1], scope, ctx.WordStart) ?? [])
                items.Add(c.Name, CompletionKind.Column, c.Type, "0", PgIdent.Quote(c.Name), $"{chain[0]}.{chain[1]}.{c.Name}");
    }

    static CompletionKind Kind(SnapObject o) => o.Kind switch
    {
        ObjectKind.Table => CompletionKind.Table, ObjectKind.Synonym => CompletionKind.Synonym, ObjectKind.View => CompletionKind.View,
        ObjectKind.Proc => CompletionKind.Proc, ObjectKind.Type => CompletionKind.Type, ObjectKind.Tvf => CompletionKind.TableFunction, _ => CompletionKind.Func,
    };

    static string KindText(SnapObject o) => o.Kind.ToString().ToLowerInvariant();

    static void Expression(PgCompletionContext ctx, Scope scope, List<Var> vars, PgResolver r, Items items)
    {
        // A SET target (UPDATE … SET |, … SET a = 1, |, MERGE … UPDATE SET |, ON CONFLICT … DO UPDATE SET |, ON DUPLICATE KEY
        // UPDATE |): only the target's columns, bare — PostgreSQL, SQLite and DuckDB reject a qualified target, and no other
        // source's column can be set (audit M9). The value after '=' sees every source, below.
        if (SetTargetSlot(ctx.Before) is { } set && SetTarget(ctx.Before, set, scope) is { } target)
        {
            foreach (var c in r.Columns(target, scope)) items.Add(c.Name, CompletionKind.Column, c.Type, "0", PgIdent.Quote(c.Name), $"{target.Label}.{c.Name}");
            return;
        }
        var visible = scope.VisibleAt(ctx.WordStart).Where(s => !s.InsertTarget || ctx.Statement.Any(t => t.Is("RETURNING") && t.Offset < ctx.WordStart)).ToList();
        // UPDATE … SET | and INSERT … RETURNING | see the target's columns.
        var labels = visible.Select(s => s.Label).Distinct().Count();
        // A source the user aliased is referred to through its alias, also when it is the only one (tier 1 does the same,
        // complete.ts expression); never in a SET list, whose target columns cannot be qualified (PostgreSQL, SQLite, DuckDB).
        var aliased = !InSetList(ctx.Before);
        foreach (var src in visible)
            foreach (var c in r.Columns(src, scope))
            {
                if (labels > 1 || (aliased && src.Alias != null)) items.Add($"{src.Label}.{c.Name}", CompletionKind.Column, c.Type, "0", $"{PgIdent.Quote(src.Label)}.{PgIdent.Quote(c.Name)}", $"{src.Label}.{c.Name}");
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
        var rules = PgCompletionRules.Current;
        foreach (var o in schema.Snapshot.Objects.Where(o => schema.IsRowset(o) || o.Kind == ObjectKind.Tvf))
        {
            var bare = rules.Unqualified(o.Schema);
            var label = bare ? o.Name : $"{o.Schema}.{o.Name}";
            var insert = bare ? PgIdent.Quote(o.Name) : $"{PgIdent.Quote(o.Schema)}.{PgIdent.Quote(o.Name)}";
            items.Add(label, Kind(o), KindText(o), rules.Names.Equals(o.Schema, rules.DefaultSchema) ? "2" : "3", insert);
        }
        foreach (var s in schema.Schemas.Where(s => !rules.Unqualified(s)).Order()) items.Add(s, CompletionKind.Schema, "schema", "4", PgIdent.Quote(s));
    }

    static void Types(PgSchema? schema, Items items)
    {
        foreach (var t in PgCompletionRules.Current.BuiltinTypes) items.Add(t, CompletionKind.Type, "built-in type", "1");
        if (schema == null) return;
        var rules = PgCompletionRules.Current;
        foreach (var o in schema.Snapshot.Objects.Where(o => o.Kind == ObjectKind.Type))
        {
            var bare = rules.Names.Equals(o.Schema, rules.DefaultSchema);
            items.Add(bare ? o.Name : $"{o.Schema}.{o.Name}", CompletionKind.Type, "type", "0", bare ? PgIdent.Quote(o.Name) : $"{PgIdent.Quote(o.Schema)}.{PgIdent.Quote(o.Name)}");
        }
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
        // INSERT IGNORE INTO / INSERT OR REPLACE INTO: the dialect's modifiers stand between the verb and INTO.
        if (first < 2 || !b[first - 1].Is("INTO") || !PgScopeBuilder.IsTargetInto(b, first - 1, out var insert) || !insert) return null;
        return (first < k ? b[k - 2].Name : null, b[k].Name);
    }

    /// <summary>
    /// The routines a call at <paramref name="open"/> may be (CALL name( / name( in an expression), and the dialect's member
    /// routines (<see cref="PgCompletionRules.MemberRoutines"/>: pkg.member( / schema.pkg.member() when the catalog has none.
    /// </summary>
    static List<SnapObject> Call(List<Tok> b, int open, PgSchema? schema)
    {
        if (schema == null || !b[open - 1].IsIdent) return [];
        var k = open - 1;
        var chain = new List<string> { b[k].Name };
        for (var j = k - 1; j >= 1 && chain.Count < 3 && b[j].IsP(".") && b[j - 1].IsIdent; j -= 2) chain.Insert(0, b[j - 1].Name);
        var sch = chain.Count >= 2 ? chain[^2] : null;
        var members = PgCompletionRules.Current.MemberRoutines;
        // db.schema.fn( (PostgreSQL, DuckDB) resolves by schema and name; with member routines a three-part chain is
        // schema.pkg.member( instead, never a schema named after the package.
        var found = chain.Count <= 2 || members == null ? schema.Routines(sch, b[k].Name).ToList() : [];
        if (found.Count == 0 && chain.Count >= 2 && members != null) found = members(schema.Snapshot, chain).ToList();
        return found;
    }

    static readonly HashSet<string> ClauseWords = new(["SELECT", "FROM", "JOIN", "ON", "USING", "WHERE", "GROUP", "HAVING", "WINDOW", "QUALIFY", "ORDER", "LIMIT", "OFFSET", "FETCH", "RETURNING", "VALUES", "SET", "UPDATE", "INTO", "CONNECT", "START"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The cursor is in a SET list: the nearest clause word before it at its own nesting level is SET (UPDATE … SET,
    /// ON CONFLICT … DO UPDATE SET, MERGE … UPDATE SET) or MySQL's ON DUPLICATE KEY UPDATE. On tokens, never on text.
    /// </summary>
    static bool InSetList(List<Tok> b)
    {
        var depth = 0;
        for (var k = b.Count - 1; k >= 0; k--)
        {
            if (b[k].IsP(")")) depth++;
            else if (b[k].IsP("(")) { if (depth-- == 0) return false; }
            else if (depth == 0 && IsClauseWord(b, k))
                return b[k].Is("SET") || (b[k].Is("UPDATE") && k >= 1 && b[k - 1].Is("KEY"));
        }
        return false;
    }

    /// <summary>
    /// A clause word at <paramref name="k"/> — except VALUES called as a function in an expression (MySQL's
    /// <c>ON DUPLICATE KEY UPDATE a = VALUES(a)</c>: after '=', an operator, ',' or '('), which is no VALUES clause.
    /// </summary>
    static bool IsClauseWord(List<Tok> b, int k) =>
        b[k].Kind == PgTokenKind.Word && ClauseWords.Contains(b[k].Text)
        && !(b[k].Is("VALUES") && k >= 1 && k + 1 < b.Count && b[k + 1].IsP("(") && (b[k - 1].Kind == PgTokenKind.Operator || b[k - 1].IsP(",") || b[k - 1].IsP("(")))
        && !PgScopeBuilder.IsDistinctFrom(b, k);

    /// <summary>
    /// The cursor names a SET target: the nearest token before it at its nesting level that is a clause word, ',' or '='
    /// is SET (or MySQL's KEY UPDATE) or a comma of the SET list, never '=' (the value side). Returns the index of that
    /// SET / UPDATE word in <paramref name="b"/>.
    /// </summary>
    static int? SetTargetSlot(List<Tok> b)
    {
        if (!InSetList(b)) return null;
        var depth = 0;
        var pastComma = false; // a comma of the SET list was passed: the cursor starts the next assignment
        for (var k = b.Count - 1; k >= 0; k--)
        {
            // ARRAY[1, |: the comma is the array's, not the SET list's (code audit 2026-11-25, L3).
            if (b[k].IsP(")") || b[k].IsP("]")) { depth++; continue; }
            if (b[k].IsP("(") || b[k].IsP("[")) { if (depth-- == 0) return null; continue; }
            if (depth > 0) continue;
            if (b[k].IsP(",")) pastComma = true;
            // An operator run is one token in some dialects (MySQL's `=-`): an assignment is any run that starts with '='
            // (code audit 2026-11-25, L6), never '=>' (a named argument).
            else if (IsAssignment(b[k]) && !pastComma) return null;
            else if (IsClauseWord(b, k)) return b[k].Is("SET") || b[k].Is("UPDATE") ? k : null;
        }
        return null;
    }

    static bool IsAssignment(Tok t) =>
        t.Kind is PgTokenKind.Operator or PgTokenKind.Punct && t.Text.StartsWith('=') && !t.Text.StartsWith("=>", StringComparison.Ordinal);

    /// <summary>
    /// The table a SET list at <paramref name="set"/> updates: the UPDATE target, the MERGE INTO target (… THEN UPDATE SET),
    /// or the INSERT target (ON CONFLICT … DO UPDATE SET, ON DUPLICATE KEY UPDATE). Null for a MySQL multi-table UPDATE,
    /// whose SET list takes every table's columns (<see cref="PgCompletionRules.MultiTableUpdate"/>), and when the target
    /// is not a plain table name.
    /// </summary>
    static Source? SetTarget(List<Tok> b, int set, Scope scope)
    {
        // The UPDATE word of this SET list: SET itself is preceded by its target (UPDATE t [AS] x SET); KEY UPDATE is its own.
        var update = b[set].Is("UPDATE") ? set : -1;
        // At the SET's own level: a subquery's FOR UPDATE before it is not this SET's verb (code audit 2026-11-25, L3).
        for (int k = set - 1, level = 0; k >= 0 && update < 0; k--)
            if (b[k].IsP(")")) level++;
            else if (b[k].IsP("(")) { if (level-- == 0) break; }
            else if (level > 0) continue;
            else if (b[k].Is("UPDATE") && !(k > 0 && b[k - 1].Is("FOR"))) update = k;
            else if (b[k].IsP(";")) break;
        if (update < 0) return null;
        var before = update > 0 ? b[update - 1] : default;
        int intro;
        // INSERT IGNORE INTO / INSERT OR IGNORE INTO: the dialect's modifiers stand between the verb and INTO.
        if (before.Is("KEY") || before.Is("DO"))
            intro = b.FindLastIndex(update, t => t.Is("INTO")) is var i && PgScopeBuilder.IsTargetInto(b, i, out var insert) && insert ? i : -1;
        else if (before.Is("THEN"))
            intro = b.FindLastIndex(update, t => t.Is("INTO")) is var i && PgScopeBuilder.IsTargetInto(b, i, out var insert) && !insert ? i : -1;
        else intro = update;
        if (intro < 0) return null;
        // UPDATE IGNORE t / UPDATE OR REPLACE t: the target follows the modifiers.
        var j = intro == update ? PgScopeBuilder.AfterUpdateModifiers(b, update) : intro + 1;
        while (j < b.Count && b[j].Is("ONLY")) j++;
        if (j >= b.Count || !b[j].IsIdent) return null;
        var chain = PgScopeBuilder.Chain(b, j);
        var target = scope.Sources.FirstOrDefault(s => s.Kind == SourceKind.Relation && s.Offset == b[chain[^1]].Offset);
        if (target == null) return null;
        if (intro == update && PgCompletionRules.Current.MultiTableUpdate)
        {
            // UPDATE a JOIN b … / UPDATE a, b …: more than one table before SET.
            var depth = 0;
            for (var k = chain[^1] + 1; k < set; k++)
            {
                if (b[k].IsP("(")) depth++;
                else if (b[k].IsP(")")) depth--;
                else if (depth == 0 && (b[k].Is("JOIN") || b[k].Is("STRAIGHT_JOIN") || b[k].IsP(","))) return null;
            }
        }
        return target;
    }

    static readonly HashSet<string> ObjectWords = new(["FROM", "JOIN", "STRAIGHT_JOIN", "INTO", "UPDATE", "TABLE", "ONLY", "LATERAL", "USING"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> FromEnd = new(["WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "ON", "SET", "RETURNING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "SELECT", "VALUES"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The cursor names a table: after FROM / JOIN / INTO / UPDATE …, or after a comma in a FROM list.</summary>
    static bool ObjectPosition(List<Tok> b)
    {
        if (b.Count == 0) return false;
        var prev = b[^1];
        if (prev.Kind == PgTokenKind.Word && ObjectWords.Contains(prev.Text))
        {
            if (prev.Is("INTO")) return PgScopeBuilder.IsTargetInto(b, b.Count - 1, out _);
            if (prev.Is("UPDATE")) return StatementUpdate(b, b.Count - 1);
            if (prev.Is("FROM")) return !PgScopeBuilder.IsDistinctFrom(b, b.Count - 1);
            return true;
        }
        // UPDATE IGNORE | / UPDATE LOW_PRIORITY | (MySQL), UPDATE OR REPLACE | (SQLite, DuckDB): the dialect's modifiers
        // stand between the verb and the target (code audit 2026-11-25, M9).
        if (prev.Kind == PgTokenKind.Word && PgScopeBuilder.VerbBefore(b, b.Count - 1) is var verb && verb >= 0 && verb < b.Count - 1 && b[verb].Is("UPDATE"))
            return StatementUpdate(b, verb);
        if (!prev.IsP(",")) return false;
        var depth = 0;
        for (var k = b.Count - 2; k >= 0; k--)
        {
            if (b[k].IsP(")")) depth++;
            else if (b[k].IsP("(")) { if (depth-- == 0) return false; }
            else if (depth == 0 && b[k].Is("FROM")) return !PgScopeBuilder.IsDistinctFrom(b, k);
            // UPDATE a, | (MySQL / MariaDB multi-table UPDATE, code audit 2026-11-25, M8).
            else if (depth == 0 && b[k].Is("UPDATE")) return PgCompletionRules.Current.MultiTableUpdate && StatementUpdate(b, k);
            else if (depth == 0 && b[k].Kind == PgTokenKind.Word && FromEnd.Contains(b[k].Text)) return false;
        }
        return false;
    }

    /// <summary>UPDATE at <paramref name="k"/> starts an UPDATE statement, not FOR UPDATE / ON DUPLICATE KEY UPDATE / DO UPDATE / THEN UPDATE.</summary>
    static bool StatementUpdate(List<Tok> b, int k) => k == 0 || !(b[k - 1].Is("FOR") || b[k - 1].Is("KEY") || b[k - 1].Is("DO") || b[k - 1].Is("THEN"));

    // ---------------------------------------------------------------------------------------------- signature help

    static SignatureHelp Sig(SignatureParams p, ISchemaSource source, CancellationToken ct)
    {
        var none = new SignatureHelp { Signatures = [] };
        if (string.IsNullOrEmpty(p.Text)) return none;
        ct.ThrowIfCancellationRequested();
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
                    Label = builtin ? $"{o.Name}({string.Join(", ", labels)})"
                        : $"{PgCompletionRules.Current.RoutineLabel?.Invoke(o) ?? $"{PgIdent.Quote(o.Schema)}.{PgIdent.Quote(o.Name)}"}({string.Join(", ", labels)})",
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
