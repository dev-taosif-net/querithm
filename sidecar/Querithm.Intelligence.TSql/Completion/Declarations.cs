using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querithm.Intelligence.TSql.Completion;

/// <summary>
/// Builds the <see cref="Env"/> for a cursor: module parameters, DECLAREd variables / table variables / cursors from the
/// current batch above the cursor, and temp tables (CREATE TABLE #t, SELECT ... INTO #t) from anywhere above it — temp
/// tables outlive their batch within a session. Each declaration statement is parsed on its own (cached by text).
/// </summary>
static class Declarations
{
    public static Env Build(DocModel doc, Batch batch, int stmtStart, SchemaScope schema)
    {
        var env = new Env(schema);
        var resolver = new Resolver(env);
        for (var s = 0; s < doc.Segs.Count; s++)
        {
            var seg = doc.Segs[s];
            if (seg.StartOffset >= stmtStart) break;
            var sameBatch = seg.Batch == batch.Index;
            try
            {
                if (seg.Kind == SegKind.Header)
                {
                    if (sameBatch && ParseModule(doc, seg) is { } mod)
                    {
                        env.Module = mod.Info;
                        foreach (var p in mod.Info.Params) env.Vars[p.Name] = new VarDecl(p.Name, p.Type, true, p.Output, p.HasDefault);
                        if (mod.ReturnTable is { } rt) env.Tables[rt.Name] = rt;
                    }
                    continue;
                }
                if (seg.Kind != SegKind.Statement) continue;
                if (seg.Kw == "DECLARE") { if (sameBatch) AddDeclare(doc, seg, env); }
                else if (seg.Kw == "CREATE" && doc.Kw(seg.First + 1) == "TABLE" && seg.First + 2 <= seg.Last && doc.Text[doc.Toks[seg.First + 2].Start] == '#') AddCreateTemp(doc, seg, env);
                else if (seg.IntoTemp && seg.Kw is "SELECT" or "WITH") AddSelectInto(doc, seg, env, resolver);
            }
            catch
            {
                // A declaration we cannot read is skipped; completion must never throw.
            }
        }
        return env;
    }

    public static string SegText(DocModel doc, Segment seg) => doc.Text.Substring(seg.StartOffset, seg.EndOffset - seg.StartOffset);

    public sealed record ModuleParse(ModuleInfo Info, TableDecl? ReturnTable);

    /// <summary>Parses a CREATE/ALTER PROCEDURE|FUNCTION|TRIGGER header by completing it with a trivial body.</summary>
    public static ModuleParse? ParseModule(DocModel doc, Segment seg)
    {
        var text = SegText(doc, seg);
        if (!seg.HeaderClosed) text += "\nAS";
        string[] suffixes = seg.ModuleKind switch
        {
            "FUNCTION" => ["\nBEGIN RETURN NULL END", "\nRETURN SELECT 1 AS x", "\nBEGIN RETURN END"],
            "TRIGGER" => ["\nBEGIN RETURN END", "\nRETURN"],
            "VIEW" => ["\nSELECT 1 AS x"],
            _ => ["\nRETURN"],
        };
        foreach (var suffix in suffixes)
        {
            var full = text + suffix;
            var parsed = StatementParser.Parse(full);
            if (!parsed.Ok) continue;
            switch (parsed.First)
            {
                case ProcedureStatementBody p:
                {
                    var n = p.ProcedureReference.Name;
                    return new(new ModuleInfo { Kind = "PROCEDURE", Schema = n.SchemaIdentifier?.Value, Name = n.BaseIdentifier.Value, Params = Params(p.Parameters, full) }, null);
                }
                case FunctionStatementBody f:
                {
                    TableDecl? rt = null;
                    if (f.ReturnType is TableValuedFunctionReturnType tv && tv.DeclareTableVariableBody is { } body)
                    {
                        var cols = Columns(body.Definition);
                        rt = new TableDecl(body.VariableName.Value, true, () => cols);
                    }
                    return new(new ModuleInfo { Kind = "FUNCTION", Schema = f.Name.SchemaIdentifier?.Value, Name = f.Name.BaseIdentifier.Value, Params = Params(f.Parameters, full) }, rt);
                }
                case TriggerStatementBody t:
                    return new(new ModuleInfo { Kind = "TRIGGER", Schema = t.Name.SchemaIdentifier?.Value, Name = t.Name.BaseIdentifier.Value, Params = [], TriggerTarget = t.TriggerObject?.Name }, null);
                case ViewStatementBody v:
                    return new(new ModuleInfo { Kind = "VIEW", Schema = v.SchemaObjectName.SchemaIdentifier?.Value, Name = v.SchemaObjectName.BaseIdentifier.Value, Params = [] }, null);
            }
        }
        return TokenModule(doc, seg);
    }

    static List<ModuleParam> Params(IList<ProcedureParameter> ps, string text) =>
        ps.Select(p =>
        {
            var type = TypeNames.Of(p.DataType) ?? "?";
            if (p.Modifier == ParameterModifier.ReadOnly) type += " READONLY";
            string? def = null;
            if (p.Value != null && p.Value.StartOffset >= 0 && p.Value.StartOffset + p.Value.FragmentLength <= text.Length)
                def = text.Substring(p.Value.StartOffset, p.Value.FragmentLength);
            return new ModuleParam(p.VariableName.Value, type, p.Modifier == ParameterModifier.Output, p.Value != null, def);
        }).ToList();

    /// <summary>Header that does not parse (being typed): pick parameters out of the tokens.</summary>
    static ModuleParse? TokenModule(DocModel doc, Segment seg)
    {
        var t = doc.Toks;
        var i = seg.First + 1;
        if (doc.Kw(i) == "OR") i += 2;
        i++; // PROCEDURE / FUNCTION / TRIGGER
        if (i > seg.Last || !t[i].IsName()) return null;
        string? schema = null;
        var name = SqlLexer.Ident(doc.Text, t[i]);
        if (i + 2 <= seg.Last && doc.IsP(i + 1, '.') && t[i + 2].IsName()) { schema = name; name = SqlLexer.Ident(doc.Text, t[i + 2]); i += 2; }
        var ps = new List<ModuleParam>();
        var depth = 0;
        for (var j = i + 1; j <= seg.Last; j++)
        {
            if (doc.IsP(j, '(')) { depth++; continue; }
            if (doc.IsP(j, ')')) { depth--; continue; }
            if (depth > 1 || t[j].Kind != TK.Variable) continue;
            if (!(doc.IsP(j - 1, ',') || doc.IsP(j - 1, '(') || j == i + 1)) continue;
            var pname = doc.TokText(j);
            var k = j + 1;
            if (doc.Kw(k) == "AS") k++;
            var typeStart = k;
            var d = 0;
            var output = false;
            var hasDefault = false;
            var typeEnd = k - 1;
            for (; k <= seg.Last; k++)
            {
                if (doc.IsP(k, '(')) { d++; typeEnd = k; continue; }
                if (doc.IsP(k, ')')) { if (d == 0) break; d--; typeEnd = k; continue; }
                if (d > 0) { typeEnd = k; continue; }
                if (doc.IsP(k, ',') || doc.Kw(k) == "AS") break;
                if (doc.IsP(k, '=')) { hasDefault = true; continue; }
                if (doc.Kw(k) is "OUTPUT" or "OUT") { output = true; continue; }
                if (!hasDefault && doc.Kw(k) != "READONLY") typeEnd = k;
            }
            var type = typeEnd >= typeStart ? doc.Text[t[typeStart].Start..t[typeEnd].End] : "?";
            ps.Add(new ModuleParam(pname, type, output, hasDefault, null));
            j = k - 1;
        }
        var kind = seg.ModuleKind ?? "PROCEDURE";
        return new(new ModuleInfo { Kind = kind, Schema = schema, Name = name, Params = ps }, null);
    }

    static List<Col> Columns(TableDefinition? def)
    {
        var list = new List<Col>();
        if (def?.ColumnDefinitions == null) return list;
        var pk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in def.TableConstraints ?? [])
            if (c is UniqueConstraintDefinition { IsPrimaryKey: true } u)
                foreach (var col in u.Columns) pk.Add(col.Column.MultiPartIdentifier.Identifiers[^1].Value);
        foreach (var c in def.ColumnDefinitions)
        {
            var notes = new List<string>();
            var name = c.ColumnIdentifier.Value;
            if (pk.Contains(name) || c.Constraints.Any(x => x is UniqueConstraintDefinition { IsPrimaryKey: true })) notes.Add("PK");
            if (c.IdentityOptions != null) notes.Add("identity");
            if (c.ComputedColumnExpression != null) notes.Add("computed");
            list.Add(new Col(name, TypeNames.Of(c.DataType) ?? (c.ComputedColumnExpression != null ? "computed" : null), notes.Count > 0 ? string.Join(", ", notes) : null));
        }
        return list;
    }

    static void AddDeclare(DocModel doc, Segment seg, Env env)
    {
        var parsed = StatementParser.Parse(SegText(doc, seg));
        if (parsed.Ok)
        {
            switch (parsed.First)
            {
                case DeclareVariableStatement d:
                    foreach (var el in d.Declarations)
                        env.Vars[el.VariableName.Value] = new VarDecl(el.VariableName.Value, TypeNames.Of(el.DataType) ?? "cursor", false);
                    return;
                case DeclareTableVariableStatement tv:
                {
                    var cols = Columns(tv.Body.Definition);
                    env.Tables[tv.Body.VariableName.Value] = new TableDecl(tv.Body.VariableName.Value, true, () => cols);
                    return;
                }
                case DeclareCursorStatement c:
                    if (!env.Cursors.Contains(c.Name.Value, StringComparer.OrdinalIgnoreCase)) env.Cursors.Add(c.Name.Value);
                    return;
            }
        }
        TokenDeclare(doc, seg, env);
    }

    /// <summary>DECLARE that does not parse: @name [AS] type[, ...] / @t TABLE (...) / name CURSOR.</summary>
    static void TokenDeclare(DocModel doc, Segment seg, Env env)
    {
        var t = doc.Toks;
        var i = seg.First + 1;
        if (i <= seg.Last && t[i].Kind == TK.Word && doc.Kw(i + 1) is "CURSOR" or "INSENSITIVE" or "SCROLL")
        {
            var cname = SqlLexer.Ident(doc.Text, t[i]);
            if (!env.Cursors.Contains(cname, StringComparer.OrdinalIgnoreCase)) env.Cursors.Add(cname);
            return;
        }
        while (i <= seg.Last)
        {
            if (t[i].Kind != TK.Variable) { i++; continue; }
            var name = doc.TokText(i);
            var k = i + 1;
            if (doc.Kw(k) == "AS") k++;
            if (doc.Kw(k) == "TABLE")
            {
                if (doc.IsP(k + 1, '(')) env.Tables[name] = new TableDecl(name, true, () => TokenColumnDefs(doc, k + 1, seg.Last));
                return;
            }
            var typeStart = k;
            var typeEnd = k - 1;
            var d = 0;
            for (; k <= seg.Last; k++)
            {
                if (doc.IsP(k, '(')) d++;
                else if (doc.IsP(k, ')')) d--;
                else if (d == 0 && (doc.IsP(k, ',') || doc.IsP(k, '='))) break;
                if (d >= 0) typeEnd = k;
            }
            env.Vars[name] = new VarDecl(name, typeEnd >= typeStart ? doc.Text[t[typeStart].Start..t[typeEnd].End] : null, false);
            // skip a default value up to the next top-level comma
            for (d = 0; k <= seg.Last; k++)
            {
                if (doc.IsP(k, '(')) d++;
                else if (doc.IsP(k, ')')) d--;
                else if (d == 0 && doc.IsP(k, ',')) break;
            }
            i = k + 1;
        }
    }

    /// <summary>Column definitions "(name type, ...)" read from tokens (for definitions that do not parse).</summary>
    internal static List<Col> TokenColumnDefs(DocModel doc, int openParen, int last)
    {
        var cols = new List<Col>();
        var t = doc.Toks;
        var d = 0;
        var elementStart = true;
        for (var k = openParen; k <= last; k++)
        {
            if (doc.IsP(k, '(')) { d++; if (d == 1) elementStart = true; continue; }
            if (doc.IsP(k, ')')) { d--; if (d == 0) break; continue; }
            if (d != 1) continue;
            if (doc.IsP(k, ',')) { elementStart = true; continue; }
            if (!elementStart) continue;
            elementStart = false;
            if (!t[k].IsName() || doc.Kw(k) is "CONSTRAINT" or "PRIMARY" or "UNIQUE" or "INDEX" or "CHECK" or "FOREIGN" or "PERIOD") continue;
            var name = SqlLexer.Ident(doc.Text, t[k]);
            string? type = null;
            if (k + 1 <= last && t[k + 1].IsName() && doc.Kw(k + 1) != "AS")
            {
                var end = k + 1;
                if (doc.IsP(k + 2, '('))
                {
                    var dd = 0;
                    for (var j = k + 2; j <= last; j++)
                    {
                        if (doc.IsP(j, '(')) dd++;
                        else if (doc.IsP(j, ')') && --dd == 0) { end = j; break; }
                    }
                }
                type = doc.Text[t[k + 1].Start..t[end].End].ToLowerInvariant();
            }
            cols.Add(new Col(name, type));
        }
        return cols;
    }

    static void AddCreateTemp(DocModel doc, Segment seg, Env env)
    {
        var name = SqlLexer.Ident(doc.Text, doc.Toks[seg.First + 2]);
        var parsed = StatementParser.Parse(SegText(doc, seg));
        if (parsed.Ok && parsed.First is CreateTableStatement ct)
        {
            var cols = Columns(ct.Definition);
            env.Tables[name] = new TableDecl(name, false, () => cols);
            return;
        }
        var open = seg.First + 3;
        if (open <= seg.Last && doc.IsP(open, '(')) env.Tables[name] = new TableDecl(name, false, () => TokenColumnDefs(doc, open, seg.Last));
    }

    static void AddSelectInto(DocModel doc, Segment seg, Env env, Resolver resolver)
    {
        var parsed = StatementParser.Parse(SegText(doc, seg));
        if (!parsed.Ok || parsed.First is not SelectStatement { Into: { } into } sel) return;
        var name = into.BaseIdentifier.Value;
        var ctes = CteScope.Of(sel);
        env.Tables[name] = new TableDecl(name, false, () => resolver.Projection(sel.QueryExpression, ctes));
    }
}
