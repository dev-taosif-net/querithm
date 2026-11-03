using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql.Completion;

/// <summary>
/// Signature help (§9): the innermost call parenthesis around the cursor (built-in, scalar function or TVF), else the
/// EXEC argument list. Token based on the cached <see cref="DocModel"/>; never parses the document.
/// </summary>
public static class TSqlSignature
{
    static readonly SignatureHelp None = new() { Signatures = [] };

    public static SignatureHelp Help(SignatureParams p, ISchemaSource schema)
    {
        try { return Run(p, schema) ?? None; }
        catch { return None; }
    }

    static SignatureHelp? Run(SignatureParams p, ISchemaSource schemaSource)
    {
        if (string.IsNullOrEmpty(p.Text)) return null;
        var doc = DocModel.Get(p.Text, p.DocId);
        var offset = Math.Clamp(p.Offset, 0, p.Text.Length);
        if (doc.InOpaque(offset)) return null;
        var scope = new SchemaScope(schemaSource, p.ProfileId, p.Database);
        var cur = CursorInfo.Compute(doc, offset);
        var prev = doc.LastTokBefore(offset);
        var batch = cur.Batch;
        if (prev < batch.FirstTok) return null;
        // Statement start: the segment holding prev (or the batch start).
        var first = cur.Seg?.First ?? batch.FirstTok;
        var seg = cur.Seg;
        if (seg == null)
        {
            for (var s = batch.FirstSeg; s < batch.EndSeg; s++)
                if (doc.Segs[s].First <= prev && prev <= doc.Segs[s].Last) { seg = doc.Segs[s]; first = seg.First; break; }
        }
        if (Call(doc, scope, first, prev) is { } call) return call;
        if (seg != null && seg.Kind == SegKind.Statement) return Exec(doc, scope, seg, prev, offset);
        return null;
    }

    static SignatureHelp? Call(DocModel doc, SchemaScope scope, int first, int prev)
    {
        var t = doc.Toks;
        var depth = 0;
        var commas = 0;
        for (var i = prev; i >= first; i--)
        {
            if (doc.IsP(i, ')')) { depth++; continue; }
            if (doc.IsP(i, '('))
            {
                if (depth > 0) { depth--; continue; }
                // Unclosed '(' at the cursor's level: is it a call?
                var nameEnd = i - 1;
                if (nameEnd >= first && t[nameEnd].IsName())
                {
                    var k = nameEnd;
                    while (k - 2 >= first && doc.IsP(k - 1, '.') && t[k - 2].IsName()) k -= 2;
                    var parts = TSqlCompletion.NameParts(doc, k, nameEnd);
                    if (Resolve(scope, parts, commas) is { } help) return help;
                }
                commas = 0;
                continue;
            }
            if (depth == 0 && doc.IsP(i, ',')) commas++;
        }
        return null;
    }

    static SignatureHelp? Resolve(SchemaScope scope, List<string> parts, int commas)
    {
        if (parts.Count == 1 && Builtins.Functions.TryGetValue(parts[0], out var fn))
        {
            var ps = fn.Params.Select(x => new ParameterInfo { Label = x }).ToArray();
            var active = ps.Length == 0 ? 0 : Math.Min(commas, ps.Length - 1);
            // Variadic: "..." stands for the rest.
            if (ps.Length > 0 && fn.Params[^1] == "..." && commas >= ps.Length - 1) active = ps.Length - 2 >= 0 ? ps.Length - 2 : 0;
            return new SignatureHelp
            {
                Signatures = [new SignatureInfo { Label = fn.Label, Documentation = fn.Doc, Parameters = ps }],
                ActiveSignature = 0, ActiveParameter = active,
            };
        }
        var name = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : null;
        var db = parts.Count >= 3 ? parts[^3] : null;
        if (parts.Count == 1 && SqlLexer.Keywords.Contains(name)) return null;
        var obj = scope.Find(db, schema, name, o => o.Kind is ObjectKind.Func or ObjectKind.Tvf);
        if (obj == null) return null;
        var plist = (obj.Params ?? []).Where(x => x.Ordinal > 0).OrderBy(x => x.Ordinal)
            .Select(x => new ModuleParam(x.Name.StartsWith('@') ? x.Name : "@" + x.Name, x.Type, x.Output, x.HasDefault, null)).ToList();
        var labels = plist.Select(TSqlCompletion.ParamLabel).ToArray();
        var ret = obj.ReturnType ?? (obj.Kind == ObjectKind.Tvf ? "TABLE" : null);
        return new SignatureHelp
        {
            Signatures =
            [
                new SignatureInfo
                {
                    Label = $"{obj.Schema}.{obj.Name}({string.Join(", ", labels)})" + (ret != null ? " → " + ret : ""),
                    Documentation = obj.Kind == ObjectKind.Tvf ? "table-valued function" : "scalar function",
                    Parameters = labels.Select(l => new ParameterInfo { Label = l }).ToArray(),
                },
            ],
            ActiveSignature = 0, ActiveParameter = labels.Length == 0 ? 0 : Math.Min(commas, labels.Length - 1),
        };
    }

    static SignatureHelp? Exec(DocModel doc, SchemaScope scope, Segment seg, int prev, int offset)
    {
        var exec = TSqlCompletion.FindExec(doc, seg, prev);
        if (exec < 0) return null;
        var t = doc.Toks;
        var j = exec + 1;
        if (j + 1 < t.Count && t[j].Kind == TK.Variable && doc.IsP(j + 1, '=')) j += 2;
        var nameEnd = TSqlCompletion.NameEnd(doc, j, seg.Last);
        if (nameEnd < 0 || prev < nameEnd) return null;
        if (prev == nameEnd && t[nameEnd].End >= offset) return null; // still typing the name
        var proc = TSqlCompletion.ResolveProc(doc, scope, TSqlCompletion.NameParts(doc, j, nameEnd));
        if (proc is not { } pr) return null;
        var ps = pr.Params;
        // Split the argument list at top-level commas up to the cursor.
        var args = new List<(int Start, int End)>();
        var depth = 0;
        var start = nameEnd + 1;
        for (var k = nameEnd + 1; k <= prev; k++)
        {
            if (doc.IsP(k, '(')) depth++;
            else if (doc.IsP(k, ')')) depth--;
            else if (depth == 0 && doc.IsP(k, ',')) { args.Add((start, k - 1)); start = k + 1; }
        }
        var current = (Start: start, End: prev);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawNamed = false;
        foreach (var (s, _) in args)
            if (s < t.Count && t[s].Kind == TK.Variable && doc.IsP(s + 1, '=')) { used.Add(doc.TokText(s)); sawNamed = true; }
        int active;
        if (current.Start <= current.End && t[current.Start].Kind == TK.Variable && (doc.IsP(current.Start + 1, '=') || current.Start == current.End))
        {
            var pname = doc.TokText(current.Start);
            active = ps.FindIndex(x => string.Equals(x.Name, pname, StringComparison.OrdinalIgnoreCase));
            if (active < 0) active = ps.FindIndex(x => x.Name.StartsWith(pname, StringComparison.OrdinalIgnoreCase));
        }
        else if (sawNamed) active = ps.FindIndex(x => !used.Contains(x.Name));
        else active = args.Count;
        if (active < 0 || active >= ps.Count) active = Math.Max(0, Math.Min(args.Count, ps.Count - 1));
        var labels = ps.Select(TSqlCompletion.ParamLabel).ToArray();
        return new SignatureHelp
        {
            Signatures =
            [
                new SignatureInfo
                {
                    Label = $"{pr.Label}({string.Join(", ", labels)})", Documentation = "stored procedure",
                    Parameters = labels.Select((l, i) => new ParameterInfo { Label = l, Documentation = ps[i].HasDefault ? "optional" : ps[i].Output ? "output" : "required" }).ToArray(),
                },
            ],
            ActiveSignature = 0, ActiveParameter = active,
        };
    }
}
