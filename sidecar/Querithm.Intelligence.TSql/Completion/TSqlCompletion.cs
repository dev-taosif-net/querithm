using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql.Completion;

enum Ctx { None, Expression, Qualified, Object, ObjectQualified, ExecTarget, ExecArgs, ExecValue, Types, Cursors, Databases, InsertColumns, Variables }

/// <summary>
/// Tier-2 completion (§9). Per request: lex the document (cached per docId+text), split it into batches and statements,
/// parse only the statement around the cursor (with a placeholder identifier at the cursor and progressively stronger
/// repairs) plus the declaration statements above it (each cached by text), then resolve the cursor context against
/// the AST, the declarations and the schema snapshot. The whole document is never parsed here, so cost does not grow
/// with the size of a 12k-line procedure beyond the lexer pass.
/// </summary>
public static class TSqlCompletion
{
    public static CompletionItem[] Complete(CompleteParams p, ISchemaSource schema)
    {
        try { return Run(p, schema); }
        catch { return []; }
    }

    static CompletionItem[] Run(CompleteParams p, ISchemaSource schemaSource)
    {
        if (string.IsNullOrEmpty(p.Text)) return [];
        var doc = DocModel.Get(p.Text, p.DocId);
        var offset = Math.Clamp(p.Offset, 0, p.Text.Length);
        if (doc.InOpaque(offset)) return [];
        var cur = CursorInfo.Compute(doc, offset);
        var scope = new SchemaScope(schemaSource, p.ProfileId, p.Database);
        var env = Declarations.Build(doc, cur.Batch, cur.StmtStart, scope);
        var resolver = new Resolver(env);
        var ast = StatementAnalysis.Parse(doc, cur);
        var ctx = Classify(doc, cur, out var extra);
        var b = new ItemBuilder(doc, cur, env, resolver, ast, extra);
        return b.Build(ctx);
    }

    /// <summary>Token-level classification of the cursor position (the AST supplies scope, not position).</summary>
    internal static Ctx Classify(DocModel doc, CursorInfo cur, out int extra)
    {
        extra = -1;
        var seg = cur.Seg;
        if (seg == null) return Ctx.None;
        var t = doc.Toks;
        var prev = cur.Prev;
        var pk = doc.Kw(prev);
        var prevIsVar = prev >= 0 && t[prev].Kind == TK.Variable;
        if (seg.Kind == SegKind.Header)
        {
            if (prevIsVar && (doc.IsP(prev - 1, ',') || doc.IsP(prev - 1, '(') || t[prev - 1].IsName())) return Ctx.Types;
            if (pk == "AS" && prev > 0 && t[prev - 1].Kind == TK.Variable) return Ctx.Types;
            if (pk == "RETURNS") return Ctx.Types;
            return Ctx.None;
        }
        if (pk == "USE") return Ctx.Databases;
        if (pk is "OPEN" or "CLOSE" or "DEALLOCATE") return Ctx.Cursors;
        if (seg.Kw == "FETCH")
        {
            if (pk is "FROM" or "GLOBAL") return Ctx.Cursors;
            if (pk == "INTO" || doc.IsP(prev, ',')) return Ctx.Variables;
            return Ctx.None;
        }
        var open = InnermostOpen(doc, seg.First, prev, out var commaAtLevel);
        if (seg.Kw == "DECLARE" && open < 0)
        {
            if (prevIsVar && (doc.Kw(prev - 1) == "DECLARE" || doc.IsP(prev - 1, ','))) return Ctx.Types;
            if (pk == "AS" && prev > 0 && t[prev - 1].Kind == TK.Variable) return Ctx.Types;
        }
        if (open >= 0)
        {
            var kb = doc.Kw(open - 1);
            if (kb is "CAST" or "TRY_CAST" && pk == "AS") return Ctx.Types;
            if (kb is "CONVERT" or "TRY_CONVERT" && !commaAtLevel) return Ctx.Types;
            // Column definitions: CREATE TABLE #t ( / DECLARE @t TABLE ( / RETURNS @t TABLE (
            var isTableDef = kb == "TABLE" || (open >= 2 && t[open - 1].IsName() && doc.Kw(open - 2) == "TABLE" && seg.Kw == "CREATE");
            if (isTableDef)
            {
                if (prev >= 0 && t[prev].IsName() && (prev - 1 == open || doc.IsP(prev - 1, ','))) return Ctx.Types;
                return Ctx.None;
            }
            // INSERT [INTO] target ( | ...  / MERGE ... THEN INSERT ( |
            if (doc.Kw(open - 1) == "INSERT" && doc.Kw(open - 2) == "THEN") { extra = open; return Ctx.InsertColumns; }
            var k = open - 1;
            if (k >= 0 && (t[k].IsName() || t[k].Kind == TK.Variable))
            {
                while (k - 2 >= seg.First && doc.IsP(k - 1, '.') && t[k - 2].IsName()) k -= 2;
                if (doc.Kw(k - 1) is "INTO" or "INSERT" && (doc.Kw(k - 1) == "INSERT" || doc.Kw(k - 2) == "INSERT"))
                {
                    if (prev == open || doc.IsP(prev, ',')) { extra = open; return Ctx.InsertColumns; }
                    return Ctx.None;
                }
            }
        }
        var exec = FindExec(doc, seg, prev);
        if (exec >= 0)
        {
            var j = exec + 1;
            if (j + 1 < t.Count && t[j].Kind == TK.Variable && doc.IsP(j + 1, '=')) j += 2;
            if (prev == j - 1) return cur.Qualifier.Count > 0 ? Ctx.ExecTarget : Ctx.ExecTarget;
            var nameEnd = NameEnd(doc, j, seg.Last);
            if (nameEnd >= 0 && prev >= nameEnd)
            {
                extra = exec;
                if (doc.IsP(prev, '=') && prev > 0 && t[prev - 1].Kind == TK.Variable) return Ctx.ExecValue;
                if (prev == nameEnd || doc.IsP(prev, ',') || pk is "OUTPUT" or "OUT") return Ctx.ExecArgs;
                return Ctx.None;
            }
        }
        if (cur.Qualifier.Count > 0) return ObjectPosition(doc, seg, prev) ? Ctx.ObjectQualified : Ctx.Qualified;
        if (ObjectPosition(doc, seg, prev)) return Ctx.Object;
        if (pk == "INTO") return Ctx.None; // SELECT ... INTO new_table
        if (pk == "AS") return Ctx.None;   // naming an alias
        var clause = ClauseAt(doc, seg.First, prev);
        if (clause is "FROM" or "JOIN" or "APPLY" or "USING" or "INTO" or "TABLE" || (clause is "UPDATE" or "DELETE" or "MERGE" && pk != "ON"))
            return prev >= 0 && (t[prev].IsName() || doc.IsP(prev, ')')) ? Ctx.None : Ctx.Expression;
        return Ctx.Expression;
    }

    /// <summary>The '(' left open at the cursor's nesting level within the statement, or -1.</summary>
    static int InnermostOpen(DocModel doc, int first, int prev, out bool commaAtLevel)
    {
        commaAtLevel = false;
        var depth = 0;
        for (var i = prev; i >= first; i--)
        {
            if (doc.IsP(i, ')')) depth++;
            else if (doc.IsP(i, '('))
            {
                if (depth == 0) return i;
                depth--;
            }
            else if (depth == 0 && doc.IsP(i, ',')) commaAtLevel = true;
        }
        return -1;
    }

    static readonly HashSet<string> ClauseKw =
    [
        "SELECT", "FROM", "JOIN", "APPLY", "ON", "WHERE", "GROUP", "HAVING", "ORDER", "SET", "INTO", "VALUES", "OUTPUT", "USING",
        "MERGE", "UPDATE", "DELETE", "INSERT", "WHEN", "THEN", "ELSE", "RETURN", "PRINT", "IF", "WHILE", "DECLARE", "EXEC",
        "EXECUTE", "BY", "AND", "OR", "CASE", "TABLE", "PIVOT", "UNPIVOT",
    ];

    /// <summary>Nearest clause keyword before the cursor at the cursor's nesting level.</summary>
    internal static string? ClauseAt(DocModel doc, int first, int prev)
    {
        var depth = 0;
        for (var i = prev; i >= first; i--)
        {
            if (doc.IsP(i, ')')) { depth++; continue; }
            if (doc.IsP(i, '(')) { if (depth == 0) return "("; depth--; continue; }
            if (depth > 0) continue;
            if (doc.Kw(i) is { } kw && ClauseKw.Contains(kw))
            {
                if (kw is "LEFT" or "RIGHT") continue;
                return kw;
            }
        }
        return null;
    }

    static bool ObjectPosition(DocModel doc, Segment seg, int prev)
    {
        var pk = doc.Kw(prev);
        var before = doc.Kw(prev - 1);
        switch (pk)
        {
            case "FROM" or "JOIN" or "APPLY" or "USING": return true;
            case "TABLE": return before == "TRUNCATE";
            case "MERGE": return true;
            case "INTO":
                if (before is "INSERT" or "MERGE") return true;
                return ClauseAt(doc, seg.First, prev - 1) == "OUTPUT";
            case "UPDATE" or "DELETE" or "INSERT": return before != "THEN" && before != "ON";
        }
        if (doc.IsP(prev, ',')) return ClauseAt(doc, seg.First, prev) == "FROM";
        return false;
    }

    /// <summary>Index of EXEC/EXECUTE at depth 0 between the statement start and the cursor, or -1.</summary>
    internal static int FindExec(DocModel doc, Segment seg, int prev)
    {
        var depth = 0;
        for (var i = seg.First; i <= prev && i <= seg.Last; i++)
        {
            if (doc.IsP(i, '(')) depth++;
            else if (doc.IsP(i, ')')) depth = Math.Max(0, depth - 1);
            else if (depth == 0 && doc.Kw(i) is "EXEC" or "EXECUTE" && doc.Kw(i - 1) != "WITH" && !doc.IsP(i + 1, '(')) return i;
        }
        return -1;
    }

    /// <summary>Last token index of a dotted name starting at j, or -1.</summary>
    internal static int NameEnd(DocModel doc, int j, int last)
    {
        if (j > last || j >= doc.Toks.Count || !doc.Toks[j].IsName()) return -1;
        while (j + 2 <= last && doc.IsP(j + 1, '.') && doc.Toks[j + 2].IsName()) j += 2;
        return j;
    }

    internal static List<string> NameParts(DocModel doc, int from, int to)
    {
        var parts = new List<string>();
        for (var i = from; i <= to; i++) if (doc.Toks[i].IsName()) parts.Add(SqlLexer.Ident(doc.Text, doc.Toks[i]));
        return parts;
    }

    /// <summary>The procedure an EXEC targets with its parameters (snapshot, with defaults filled from a CREATE PROC in the document).</summary>
    internal static (string Label, List<ModuleParam> Params)? ResolveProc(DocModel doc, SchemaScope scope, List<string> parts)
    {
        if (parts.Count == 0) return null;
        var name = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : null;
        var db = parts.Count >= 3 ? parts[^3] : null;
        ModuleInfo? docMod = null;
        foreach (var seg in doc.Segs)
        {
            if (seg.Kind != SegKind.Header || seg.ModuleKind != "PROCEDURE") continue;
            var m = Declarations.ParseModule(doc, seg)?.Info;
            if (m != null && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                && (schema == null || m.Schema == null || string.Equals(m.Schema, schema, StringComparison.OrdinalIgnoreCase)))
            { docMod = m; break; }
        }
        var obj = scope.Find(db, schema, name, o => o.Kind == ObjectKind.Proc);
        if (obj != null)
        {
            var ps = (obj.Params ?? []).Where(x => x.Ordinal > 0 || !string.IsNullOrEmpty(x.Name)).OrderBy(x => x.Ordinal).Select(x =>
            {
                var pname = x.Name.StartsWith('@') ? x.Name : "@" + x.Name;
                var dp = docMod?.Params.FirstOrDefault(d => string.Equals(d.Name, pname, StringComparison.OrdinalIgnoreCase));
                return new ModuleParam(pname, x.Type, x.Output, x.HasDefault || (dp?.HasDefault ?? false), dp?.Default);
            }).ToList();
            return ($"{obj.Schema}.{obj.Name}", ps);
        }
        if (docMod != null) return ($"{docMod.Schema ?? "dbo"}.{docMod.Name}", docMod.Params);
        return null;
    }

    internal static string ParamLabel(ModuleParam p) =>
        $"{p.Name} {p.Type}" + (p.HasDefault ? " = " + (p.Default is { Length: <= 20 } d ? d : "…") : "") + (p.Output ? " OUTPUT" : "");
}

/// <summary>Builds the item list for a classified context.</summary>
sealed class ItemBuilder(DocModel doc, CursorInfo cur, Env env, Resolver resolver, StmtAst? ast, int extra)
{
    readonly List<CompletionItem> items = [];
    readonly HashSet<string> labels = new(StringComparer.OrdinalIgnoreCase);
    SchemaScope Schema => env.Schema;

    void Add(string label, CompletionKind kind, string? detail, string sortGroup, string? insert = null, bool snippet = false, string? doc = null, string? sortKey = null)
    {
        if (!labels.Add(label)) return;
        items.Add(new CompletionItem
        {
            Label = label, Kind = kind, Detail = detail, InsertText = insert, IsSnippet = snippet, Documentation = doc,
            SortText = sortGroup + (sortKey ?? label.ToLowerInvariant()),
        });
    }

    public CompletionItem[] Build(Ctx ctx)
    {
        switch (ctx)
        {
            case Ctx.Expression: Expression(); break;
            case Ctx.Qualified: Qualified(); break;
            case Ctx.Object: Objects(); break;
            case Ctx.ObjectQualified: ObjectsQualified(); break;
            case Ctx.ExecTarget: Procs(); break;
            case Ctx.ExecArgs: ExecArgs(); break;
            case Ctx.ExecValue: Variables("1"); break;
            case Ctx.Variables: Variables("1"); break;
            case Ctx.Types: Types(); break;
            case Ctx.Cursors: Cursors(); break;
            case Ctx.Databases:
                foreach (var d in Schema.Databases()) Add(Quote(d), CompletionKind.Database, "database", "1");
                break;
            case Ctx.InsertColumns: InsertColumns(); break;
        }
        return items.ToArray();
    }

    // ------------------------------------------------------------------ scope from the AST

    sealed class Scopes
    {
        public List<Source> Inner = [];
        public List<Source> Outer = [];
        public CteScope Ctes = CteScope.Empty;
        public bool InOutput;
        public bool IsMerge;
        public QuerySpecification? Spec;
        public bool InOrderBy;
    }

    Scopes? scopes;

    Scopes GetScopes()
    {
        if (scopes != null) return scopes;
        var sc = new Scopes();
        scopes = sc;
        if (ast == null)
        {
            TokenScopes(sc);
            return sc;
        }
        var chain = ast.Chain;
        var root = ast.Stmt;
        if (root is StatementWithCtesAndXmlNamespaces w && w.WithCtesAndXmlNamespaces?.CommonTableExpressions is { Count: > 0 } ctes)
        {
            var limit = ctes.Count;
            foreach (var n in chain)
                if (n is CommonTableExpression c) { var i = ctes.IndexOf(c); if (i >= 0) limit = i + 1; }
            sc.Ctes = new CteScope(ctes, limit);
        }
        var inOutput = false;
        var first = true;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            List<Source>? found = null;
            switch (node)
            {
                case OutputClause or OutputIntoClause:
                    inOutput = true;
                    break;
                case OrderByClause when first:
                    sc.InOrderBy = true;
                    break;
                case QuerySpecification qs:
                    found = resolver.Flatten(qs.FromClause, sc.Ctes);
                    if (first) sc.Spec = qs;
                    break;
                case UpdateDeleteSpecificationBase ud:
                {
                    found = resolver.Flatten(ud.FromClause, sc.Ctes);
                    var target = Target(ud.Target, found);
                    if (target != null && !found.Contains(target)) found.Insert(0, target);
                    if (inOutput && target != null)
                    {
                        if (ud is UpdateSpecification) found.Add(Pseudo("inserted", target));
                        found.Add(Pseudo("deleted", target));
                    }
                    break;
                }
                case InsertSpecification ins when first:
                {
                    var target = Target(ins.Target, []);
                    found = target != null ? [target] : [];
                    if (inOutput && target != null) { found.Clear(); found.Add(Pseudo("inserted", target)); }
                    break;
                }
                case MergeSpecification ms:
                {
                    found = [];
                    var target = Target(ms.Target, [], ms.TableAlias?.Value);
                    if (target != null) found.Add(target);
                    found.AddRange(resolver.Flatten(ms.TableReference, sc.Ctes));
                    if (inOutput && target != null)
                    {
                        found.Add(Pseudo("inserted", target));
                        found.Add(Pseudo("deleted", target));
                    }
                    sc.IsMerge = true;
                    break;
                }
            }
            if (found != null)
            {
                if (first) { sc.Inner = found; sc.InOutput = inOutput; first = false; }
                else sc.Outer.AddRange(found.Where(f => Resolver.FindSource(sc.Inner, f.Exposed) == null && Resolver.FindSource(sc.Outer, f.Exposed) == null));
            }
            if (node is CommonTableExpression && !first) break;
        }
        return sc;
    }

    Source? Target(TableReference? t, List<Source> from, string? alias = null)
    {
        if (t is NamedTableReference n && n.SchemaObject.SchemaIdentifier == null && n.SchemaObject.DatabaseIdentifier == null)
        {
            var hit = Resolver.FindSource(from, n.SchemaObject.BaseIdentifier.Value);
            if (hit != null) return hit;
        }
        if (t is NamedTableReference nn)
        {
            var so = nn.SchemaObject;
            return resolver.Named(so.DatabaseIdentifier?.Value, so.SchemaIdentifier?.Value, so.BaseIdentifier.Value, alias ?? nn.Alias?.Value, scopes?.Ctes ?? CteScope.Empty);
        }
        return t is TableReferenceWithAlias a ? resolver.FromRef(a, scopes?.Ctes ?? CteScope.Empty) : null;
    }

    static Source Pseudo(string name, Source target) => new(name, SourceKind.Pseudo, $"{name} ({target.Display})", () => target.Columns);

    /// <summary>Statement did not parse even after repair: FROM/JOIN/UPDATE/INTO/USING name [AS] alias from tokens.</summary>
    void TokenScopes(Scopes sc)
    {
        var seg = cur.Seg;
        if (seg == null) return;
        var t = doc.Toks;
        for (var i = seg.First; i <= seg.Last; i++)
        {
            var kw = doc.Kw(i);
            var isList = doc.IsP(i, ',') && TSqlCompletion.ClauseAt(doc, seg.First, i) == "FROM";
            if (!isList && kw is not ("FROM" or "JOIN" or "APPLY" or "UPDATE" or "INTO" or "USING" or "MERGE")) continue;
            var j = i + 1;
            if (kw == "MERGE" && doc.Kw(j) == "INTO") j++;
            if (j > seg.Last) break;
            if (t[j].Kind == TK.Variable || t[j].IsName())
            {
                var end = t[j].Kind == TK.Variable ? j : TSqlCompletion.NameEnd(doc, j, seg.Last);
                if (end < 0 || (t[j].Kw is { } k2 && k2 is "SELECT" or "WHERE" or "SET")) continue;
                var parts = TSqlCompletion.NameParts(doc, j, end);
                if (t[j].Kind == TK.Variable) parts = [doc.TokText(j)];
                var a = end + 1;
                if (doc.Kw(a) == "AS") a++;
                string? alias = a <= seg.Last && (t[a].Kind == TK.Quoted || (t[a].Kind == TK.Word && t[a].Kw == null)) && a < cur.ChainStart + 1000000 ? SqlLexer.Ident(doc.Text, t[a]) : null;
                if (alias != null && t[a].Start >= cur.PrefixStart && t[a].Start < cur.PrefixEnd) alias = null;
                var name = parts[^1];
                var schema = parts.Count >= 2 ? parts[^2] : null;
                var db = parts.Count >= 3 ? parts[^3] : null;
                var src = resolver.Named(db, schema, name, alias, CteScope.Empty);
                if (Resolver.FindSource(sc.Inner, src.Exposed) == null) sc.Inner.Add(src);
            }
        }
    }

    // ------------------------------------------------------------------ contexts

    void Expression()
    {
        var sc = GetScopes();
        var multi = sc.Inner.Count > 1;
        var order = 0;
        foreach (var s in sc.Inner)
            foreach (var c in s.Columns)
            {
                var label = multi ? $"{Quote(s.Exposed)}.{Quote(c.Name)}" : Quote(c.Name);
                Add(label, CompletionKind.Column, c.Type, "1", doc: ColDoc(c, s), sortKey: (order++).ToString("D5"));
            }
        if (sc.InOrderBy && sc.Spec != null)
            foreach (var c in resolver.Projection(sc.Spec, sc.Ctes))
                Add(Quote(c.Name), CompletionKind.Column, c.Type, "0", doc: "select-list column");
        if (sc.IsMerge && sc.InOutput) Add("$action", CompletionKind.Keyword, "nvarchar(10)", "2", doc: "INSERT / UPDATE / DELETE");
        Variables("2");
        foreach (var s in sc.Outer)
            foreach (var c in s.Columns)
                Add($"{Quote(s.Exposed)}.{Quote(c.Name)}", CompletionKind.Column, c.Type, "3", doc: ColDoc(c, s) + " (outer query)", sortKey: (order++).ToString("D5"));
        foreach (var s in sc.Inner.Concat(sc.Outer))
            if (s.HasAlias || s.Kind is SourceKind.Cte or SourceKind.TempTable) Add(s.Exposed, CompletionKind.Alias, s.Display, "4");
        if (Schema.Current is { } idx)
            foreach (var o in idx.Snapshot.Objects)
                if (o.Kind == ObjectKind.Func)
                    Add($"{Quote(o.Schema)}.{Quote(o.Name)}", CompletionKind.Func, o.ReturnType is { } rt ? "returns " + rt : "scalar function", "5",
                        insert: $"{Quote(o.Schema)}.{Quote(o.Name)}(${{1}})", snippet: true, doc: FuncSignature(o));
        foreach (var f in Builtins.Functions.Values)
            Add(f.Name, CompletionKind.Func, f.Label, "6", insert: f.Params.Length == 0 ? f.Name + "()" : f.Name + "(${1})", snippet: f.Params.Length > 0, doc: f.Doc);
        foreach (var (n, ty) in Builtins.GlobalVariables) Add(n, CompletionKind.Variable, ty, "7");
    }

    static string ColDoc(Col c, Source s) => (c.Note != null ? c.Note + " — " : "") + (s.Exposed != s.Display ? $"{s.Exposed}: {s.Display}" : s.Display);

    void Variables(string group)
    {
        foreach (var v in env.Vars.Values)
            Add(v.Name, v.IsParam ? CompletionKind.Parameter : CompletionKind.Variable, (v.Type ?? "") + (v.Output ? " OUTPUT" : ""), group,
                doc: v.IsParam ? "parameter" + (v.HasDefault ? " (has default)" : "") : "local variable");
    }

    void Qualified()
    {
        var q = cur.Qualifier;
        var sc = GetScopes();
        var all = sc.Inner.Concat(sc.Outer).ToList();
        Source? src = null;
        if (q.Count == 1)
        {
            src = Resolver.FindSource(all, q[0]);
            if (src == null && (q[0].StartsWith('#') || q[0].StartsWith('@')) && env.Tables.ContainsKey(q[0]))
                src = resolver.Named(null, null, q[0], null, sc.Ctes);
            if (src == null && sc.Ctes.Find(q[0]) != null) src = resolver.Named(null, null, q[0], null, sc.Ctes);
            if (src == null && env.Module?.TriggerTarget != null && q[0] is var n && (n.Equals("inserted", StringComparison.OrdinalIgnoreCase) || n.Equals("deleted", StringComparison.OrdinalIgnoreCase)))
                src = resolver.Named(null, null, q[0], null, sc.Ctes);
        }
        else if (q.Count >= 2)
        {
            src = Resolver.FindSource(all, q[^1]);
            if (src == null)
            {
                var obj = Schema.Find(q.Count >= 3 ? q[^3] : null, q[^2], q[^1], SchemaIndex.IsRowset);
                if (obj != null) src = resolver.Named(q.Count >= 3 ? q[^3] : null, q[^2], q[^1], null, sc.Ctes);
            }
        }
        if (src != null)
        {
            var i = 0;
            foreach (var c in src.Columns) Add(Quote(c.Name), CompletionKind.Column, c.Type, "0", doc: ColDoc(c, src), sortKey: (i++).ToString("D5"));
            return;
        }
        // schema. in an expression → scalar functions of that schema; database. → its schemas
        var (dbName, schemaName) = q.Count >= 2 ? (q[^2], q[^1]) : ((string?)null, q[0]);
        var idx = Schema.ForDatabase(dbName);
        if (idx != null && idx.HasSchema(schemaName))
        {
            foreach (var o in idx.Snapshot.Objects)
                if (o.Kind == ObjectKind.Func && string.Equals(o.Schema, schemaName, StringComparison.OrdinalIgnoreCase))
                    Add(Quote(o.Name), CompletionKind.Func, o.ReturnType is { } rt ? "returns " + rt : "scalar function", "1", insert: Quote(o.Name) + "(${1})", snippet: true, doc: FuncSignature(o));
            return;
        }
        if (q.Count == 1 && Schema.IsDatabase(q[0]) && Schema.ForDatabase(q[0]) is { } other)
            foreach (var s in other.Schemas) Add(Quote(s), CompletionKind.Schema, "schema in " + q[0], "1");
    }

    bool DmlTarget()
    {
        var pk = doc.Kw(cur.Prev);
        return pk is "INTO" or "UPDATE" or "DELETE" or "MERGE" or "INSERT" or "TABLE" || (pk == "FROM" && doc.Kw(cur.Prev - 1) == "DELETE");
    }

    void Objects()
    {
        var dml = DmlTarget();
        var sc = GetScopes();
        var cteList = sc.Ctes.Limit > 0 ? Enumerable.Range(0, sc.Ctes.Limit).Select(i => sc.Ctes.List[i]).ToList() : [];
        foreach (var c in cteList)
        {
            var cols = resolver.CteColumns(c, sc.Ctes.Upto(sc.Ctes.List.IndexOf(c)));
            Add(Quote(c.ExpressionName.Value), CompletionKind.Cte, "CTE (" + Preview(cols) + ")", "0");
        }
        if (cteList.Count == 0) foreach (var n in TokenCteNames()) Add(Quote(n), CompletionKind.Cte, "CTE", "0");
        foreach (var t in env.Tables.Values)
            Add(t.Name, CompletionKind.TempTable, (t.IsVariable ? "table variable (" : "temp table (") + Preview(t.Columns) + ")", "1");
        if (Schema.Current is { } idx)
        {
            foreach (var o in idx.Snapshot.Objects)
            {
                if (!SchemaIndex.IsRowset(o) || (dml && o.Kind == ObjectKind.Tvf)) continue;
                var label = string.Equals(o.Schema, "dbo", StringComparison.OrdinalIgnoreCase) ? Quote(o.Name) : $"{Quote(o.Schema)}.{Quote(o.Name)}";
                AddObject(o, label);
            }
            foreach (var s in idx.Schemas) Add(Quote(s), CompletionKind.Schema, "schema", "5");
        }
    }

    void AddObject(SnapObject o, string label)
    {
        var (kind, word, group) = o.Kind switch
        {
            ObjectKind.View => (CompletionKind.View, "view", "3"),
            ObjectKind.Tvf => (CompletionKind.Func, "table-valued function", "4"),
            ObjectKind.Synonym => (CompletionKind.Table, "synonym", "4"),
            _ => (CompletionKind.Table, "table", "2"),
        };
        var detail = $"{word} {o.Schema}.{o.Name}";
        if (o.Kind == ObjectKind.Tvf) Add(label, kind, detail, group, insert: label + "(${1})", snippet: true, doc: FuncSignature(o));
        else Add(label, kind, detail, group, doc: o.Columns is { Length: > 0 } c ? Preview(Resolver.SnapCols(o)) : null);
    }

    void ObjectsQualified()
    {
        var q = cur.Qualifier;
        var dml = DmlTarget();
        if (q.Count == 1)
        {
            if (Schema.Current is { } idx && idx.HasSchema(q[0]))
            {
                foreach (var o in idx.Snapshot.Objects)
                    if (SchemaIndex.IsRowset(o) && !(dml && o.Kind == ObjectKind.Tvf) && string.Equals(o.Schema, q[0], StringComparison.OrdinalIgnoreCase)) AddObject(o, Quote(o.Name));
                return;
            }
            if (Schema.IsDatabase(q[0]) && Schema.ForDatabase(q[0]) is { } other)
                foreach (var s in other.Schemas) Add(Quote(s), CompletionKind.Schema, "schema in " + q[0], "1");
            return;
        }
        var dbIdx = Schema.ForDatabase(q[^2]);
        if (dbIdx == null) return;
        var schema = q[^1].Length == 0 ? "dbo" : q[^1];
        foreach (var o in dbIdx.Snapshot.Objects)
            if (SchemaIndex.IsRowset(o) && string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)) AddObject(o, Quote(o.Name));
    }

    IEnumerable<string> TokenCteNames()
    {
        var seg = cur.Seg;
        if (seg == null || seg.Kw != "WITH") yield break;
        var depth = 0;
        for (var i = seg.First; i <= seg.Last; i++)
        {
            if (doc.IsP(i, '(')) { depth++; continue; }
            if (doc.IsP(i, ')')) { depth--; continue; }
            if (depth != 0 || !(i == seg.First || doc.IsP(i, ','))) continue;
            var n = i + 1;
            if (n > seg.Last || !doc.Toks[n].IsName()) continue;
            var k = n + 1;
            if (doc.IsP(k, '('))
            {
                var d = 0;
                for (; k <= seg.Last; k++)
                {
                    if (doc.IsP(k, '(')) d++;
                    else if (doc.IsP(k, ')') && --d == 0) { k++; break; }
                }
            }
            if (doc.Kw(k) == "AS" && doc.IsP(k + 1, '(')) yield return SqlLexer.Ident(doc.Text, doc.Toks[n]);
        }
    }

    void Procs()
    {
        if (Schema.Current is not { } idx) return;
        var q = cur.Qualifier;
        foreach (var o in idx.Snapshot.Objects)
        {
            if (o.Kind != ObjectKind.Proc) continue;
            if (q.Count > 0 && !string.Equals(o.Schema, q[^1], StringComparison.OrdinalIgnoreCase)) continue;
            var label = q.Count > 0 || string.Equals(o.Schema, "dbo", StringComparison.OrdinalIgnoreCase) ? Quote(o.Name) : $"{Quote(o.Schema)}.{Quote(o.Name)}";
            var ps = (o.Params ?? []).Where(x => x.Ordinal > 0).OrderBy(x => x.Ordinal).Select(x => $"{x.Name} {x.Type}" + (x.Output ? " OUTPUT" : ""));
            Add(label, CompletionKind.Proc, $"procedure {o.Schema}.{o.Name}", "1", doc: "(" + string.Join(", ", ps) + ")");
        }
    }

    void ExecArgs()
    {
        var exec = extra;
        var seg = cur.Seg!;
        var t = doc.Toks;
        var j = exec + 1;
        if (j + 1 < t.Count && t[j].Kind == TK.Variable && doc.IsP(j + 1, '=')) j += 2;
        var nameEnd = TSqlCompletion.NameEnd(doc, j, seg.Last);
        if (nameEnd < 0) return;
        var proc = TSqlCompletion.ResolveProc(doc, Schema, TSqlCompletion.NameParts(doc, j, nameEnd));
        if (proc is not { } pr) { Variables("2"); return; }
        // Arguments already written anywhere in the statement: named (@p =) and positional ones before the first named.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positional = 0;
        var sawNamed = false;
        var depth = 0;
        var argStart = true;
        for (var k = nameEnd + 1; k <= seg.Last; k++)
        {
            if (doc.IsP(k, '(')) { depth++; continue; }
            if (doc.IsP(k, ')')) { depth--; continue; }
            if (depth > 0) continue;
            if (doc.IsP(k, ',')) { argStart = true; continue; }
            if (t[k].Start >= cur.PrefixStart && t[k].Start < cur.PrefixEnd) { argStart = false; continue; }
            if (!argStart) continue;
            argStart = false;
            if (t[k].Kind == TK.Variable && doc.IsP(k + 1, '=')) { used.Add(doc.TokText(k)); sawNamed = true; }
            else if (!sawNamed && doc.Kw(k) != "WITH") positional++;
        }
        var ps = pr.Params;
        for (var i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (i < positional || used.Contains(p.Name)) continue;
            var insert = p.Output ? $"{p.Name} = ${{1}} OUTPUT" : $"{p.Name} = ${{1}}";
            var detail = p.Type + (p.Output ? " OUTPUT" : "") + (p.HasDefault ? " = default" : "");
            Add(p.Name, CompletionKind.Parameter, detail, "0", insert: insert, snippet: true,
                doc: $"{pr.Label} parameter {i + 1}" + (p.HasDefault ? " (optional)" : " (required)"), sortKey: i.ToString("D4"));
        }
        Variables("2");
    }

    void Types()
    {
        foreach (var (label, insert, detail) in Builtins.Types) Add(label, CompletionKind.Type, detail, "1", insert: insert, snippet: insert != null);
        if (cur.Seg?.Kw == "DECLARE" && cur.Seg.Kind == SegKind.Statement)
        {
            Add("TABLE", CompletionKind.Keyword, "table variable", "0", insert: "TABLE (\n\t$0\n)", snippet: true);
            Add("CURSOR", CompletionKind.Keyword, "cursor variable", "2");
        }
        if (Schema.Current is { } idx)
            foreach (var o in idx.Snapshot.Objects)
                if (o.Kind == ObjectKind.Type)
                    Add(string.Equals(o.Schema, "dbo", StringComparison.OrdinalIgnoreCase) ? Quote(o.Name) : $"{Quote(o.Schema)}.{Quote(o.Name)}", CompletionKind.Type, "user type " + o.Schema + "." + o.Name, "3");
    }

    void Cursors()
    {
        foreach (var c in env.Cursors) Add(c, CompletionKind.Cursor, "cursor", "0");
        foreach (var v in env.Vars.Values) if (string.Equals(v.Type, "cursor", StringComparison.OrdinalIgnoreCase)) Add(v.Name, CompletionKind.Cursor, "cursor variable", "1");
    }

    void InsertColumns()
    {
        var open = extra;
        var seg = cur.Seg!;
        var t = doc.Toks;
        Source? target;
        if (doc.Kw(open - 1) == "INSERT" && doc.Kw(open - 2) == "THEN")
        {
            // MERGE [INTO] target ...
            target = null;
            for (var i = seg.First; i < open; i++)
            {
                if (doc.Kw(i) != "MERGE") continue;
                var j = i + 1;
                if (doc.Kw(j) == "INTO") j++;
                var end = TSqlCompletion.NameEnd(doc, j, seg.Last);
                if (end < 0) break;
                var parts = TSqlCompletion.NameParts(doc, j, end);
                target = resolver.Named(parts.Count >= 3 ? parts[^3] : null, parts.Count >= 2 ? parts[^2] : null, parts[^1], null, CteScope.Empty);
                break;
            }
        }
        else
        {
            var k = open - 1;
            var end = k;
            if (t[k].Kind == TK.Variable) target = resolver.Named(null, null, doc.TokText(k), null, CteScope.Empty);
            else
            {
                while (k - 2 >= seg.First && doc.IsP(k - 1, '.') && t[k - 2].IsName()) k -= 2;
                var parts = TSqlCompletion.NameParts(doc, k, end);
                target = resolver.Named(parts.Count >= 3 ? parts[^3] : null, parts.Count >= 2 ? parts[^2] : null, parts[^1], null, GetScopes().Ctes);
            }
        }
        if (target == null) return;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = open + 1; i <= seg.Last && !doc.IsP(i, ')'); i++)
            if (t[i].IsName() && !(t[i].Start >= cur.PrefixStart && t[i].Start < cur.PrefixEnd)) listed.Add(SqlLexer.Ident(doc.Text, t[i]));
        var n = 0;
        foreach (var c in target.Columns)
        {
            if (listed.Contains(c.Name) || (c.Note?.Contains("computed") ?? false)) { n++; continue; }
            Add(Quote(c.Name), CompletionKind.Column, c.Type, "0", doc: ColDoc(c, target), sortKey: (n++).ToString("D5"));
        }
    }

    static string Preview(IReadOnlyList<Col> cols)
    {
        var names = cols.Take(6).Select(c => c.Name);
        return string.Join(", ", names) + (cols.Count > 6 ? ", …" : "");
    }

    static string FuncSignature(SnapObject o) =>
        $"{o.Schema}.{o.Name}(" + string.Join(", ", (o.Params ?? []).Where(p => p.Ordinal > 0).OrderBy(p => p.Ordinal).Select(p => $"{p.Name} {p.Type}")) + ")"
        + (o.ReturnType != null ? " → " + o.ReturnType : o.Kind == ObjectKind.Tvf ? " → TABLE" : "");

    internal static string Quote(string name)
    {
        if (name.Length == 0) return name;
        var ok = (char.IsLetter(name[0]) || name[0] is '_' or '#' or '@') && name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '#' or '@' or '$');
        return ok ? name : "[" + name.Replace("]", "]]") + "]";
    }
}
