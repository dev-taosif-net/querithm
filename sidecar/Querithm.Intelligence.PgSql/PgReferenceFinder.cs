using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.PgSql;

/// <summary>
/// References to a schema object or column on <see cref="PgTokenizer"/> tokens, function bodies
/// included: table references after FROM / JOIN / UPDATE / INSERT INTO with their aliases per statement, qualified and
/// unqualified columns, calls, casts and nextval('…'). Strings and comments are never matched. Without a PL/pgSQL
/// analyzer an unqualified name inside a PL/pgSQL body may be a variable, so such hits are only "possible".
/// </summary>
public sealed class PgReferenceFinder : IReferenceFinder
{
    readonly record struct Tok(PgTokenKind Kind, int Offset, int Length, string Text, bool Body, bool Plpgsql)
    {
        public bool IsIdent => Kind is PgTokenKind.Word or PgTokenKind.QuotedIdent;
        public string Name => Kind == PgTokenKind.QuotedIdent && Text.Length >= 2 ? Text[1..^1].Replace("\"\"", "\"") : Text.ToLowerInvariant();
        public bool Is(string word) => Kind == PgTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Non-trivia tokens with absolute offsets; the dollar-quoted body after AS or DO is tokenized in place.</summary>
    static List<Tok> Flatten(string text)
    {
        var outer = PgTokenizer.Tokenize(text).Where(t => !t.IsTrivia).ToList();
        var plpgsql = outer.Select((t, i) => (t, i)).Any(x => x.t.Is("LANGUAGE") && x.i + 1 < outer.Count && outer[x.i + 1].Text.Trim('\'').Equals("plpgsql", StringComparison.OrdinalIgnoreCase))
                      || (outer.Count > 0 && outer[0].Is("DO"));
        var list = new List<Tok>(outer.Count);
        for (var i = 0; i < outer.Count; i++)
        {
            var t = outer[i];
            if (t.Kind == PgTokenKind.DollarString && i > 0 && (outer[i - 1].Is("AS") || outer[i - 1].Is("DO")))
            {
                var close = t.Text.IndexOf('$', 1);
                if (close > 0 && t.Text.Length >= 2 * (close + 1))
                {
                    var tag = close + 1;
                    var inner = t.Text[tag..^tag];
                    foreach (var b in PgTokenizer.Tokenize(inner))
                        if (!b.IsTrivia) list.Add(new Tok(b.Kind, t.Offset + tag + b.Offset, b.Length, b.Text, true, plpgsql));
                    list.Add(new Tok(PgTokenKind.Punct, t.Offset + t.Length - 1, 0, ";", false, false)); // the body ends its statements
                    continue;
                }
            }
            list.Add(new Tok(t.Kind, t.Offset, t.Length, t.Text, false, false));
        }
        return list;
    }

    /// <summary>Statements: token index ranges split at top-level semicolons.</summary>
    static List<(int Start, int End)> Statements(List<Tok> toks)
    {
        var list = new List<(int, int)>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < toks.Count; i++)
        {
            if (toks[i].Text == "(") depth++;
            else if (toks[i].Text == ")") depth = Math.Max(0, depth - 1);
            else if (toks[i].Text == ";" && depth == 0)
            {
                if (i > start) list.Add((start, i));
                start = i + 1;
            }
        }
        if (start < toks.Count) list.Add((start, toks.Count));
        return list;
    }

    static readonly HashSet<string> NotAlias = new([
        "where", "join", "inner", "left", "right", "full", "cross", "on", "using", "group", "order", "having", "limit", "offset", "union", "except",
        "intersect", "set", "values", "returning", "window", "for", "natural", "lateral", "and", "or", "select", "from", "as", "with", "do", "into",
        "default", "overriding", "fetch", "then", "else", "end", "when", "loop", "is", "not", "null", "like", "in", "between", "outer", "tablesample",
    ], StringComparer.Ordinal);

    static readonly HashSet<string> EndsFrom = new(["where", "group", "order", "having", "limit", "offset", "union", "except", "intersect", "returning", "window", "for", "into", "set", "values", "on", "using", "loop", "then"], StringComparer.Ordinal);

    /// <summary>A table reference; an INSERT target only owns its column list, not the columns of the SELECT feeding it.</summary>
    sealed record TableRef(string? Schema, string Name, int NameIndex, string? Alias, int AliasIndex, bool InsertTarget = false);

    /// <summary>A dotted name starting at <paramref name="i"/>: the identifier token indices.</summary>
    static List<int> Chain(List<Tok> toks, int i, int end)
    {
        var parts = new List<int> { i };
        while (parts[^1] + 2 < end && toks[parts[^1] + 1].Text == "." && toks[parts[^1] + 2].IsIdent) parts.Add(parts[^1] + 2);
        return parts;
    }

    /// <summary>The table references of a statement and the token positions that are table names.</summary>
    static List<TableRef> Tables(List<Tok> toks, int start, int end, HashSet<int> tablePositions)
    {
        var list = new List<TableRef>();
        var inFrom = false;
        var depthAtFrom = 0;
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            var t = toks[i];
            if (t.Text == "(") { depth++; continue; }
            if (t.Text == ")") { depth--; if (inFrom && depth < depthAtFrom) inFrom = false; continue; }
            if (t.Kind == PgTokenKind.Word && EndsFrom.Contains(t.Text.ToLowerInvariant()) && depth <= depthAtFrom) inFrom = false;
            var introduces = t.Is("FROM") || t.Is("JOIN") || t.Is("UPDATE") || (t.Is("INTO") && i > start && toks[i - 1].Is("INSERT")) ||
                             (t.Is("TABLE") && i + 1 < end) || (t.Text == "," && inFrom && depth == depthAtFrom);
            if (!introduces) continue;
            if (t.Is("FROM") || t.Is("JOIN")) { inFrom = true; depthAtFrom = depth; }
            var j = i + 1;
            while (j < end && (toks[j].Is("ONLY") || toks[j].Is("LATERAL"))) j++;
            if (t.Is("TABLE")) while (j < end && (toks[j].Is("IF") || toks[j].Is("EXISTS"))) j++;
            if (j >= end || !toks[j].IsIdent || (toks[j].Kind == PgTokenKind.Word && NotAlias.Contains(toks[j].Text.ToLowerInvariant()))) continue;
            var chain = Chain(toks, j, end);
            var after = chain[^1] + 1;
            if (after < end && toks[after].Text == "(" && (t.Is("FROM") || t.Is("JOIN") || t.Text == ",")) continue; // a set-returning function, not a table
            string? alias = null;
            var aliasAt = -1;
            if (after < end && toks[after].Is("AS") && after + 1 < end && toks[after + 1].IsIdent) { alias = toks[after + 1].Name; aliasAt = after + 1; }
            else if (after < end && toks[after].IsIdent && !(toks[after].Kind == PgTokenKind.Word && NotAlias.Contains(toks[after].Text.ToLowerInvariant())))
            { alias = toks[after].Name; aliasAt = after; }
            var name = toks[chain[^1]].Name;
            var schema = chain.Count >= 2 ? toks[chain[^2]].Name : null;
            var insert = t.Is("INTO");
            if (insert) { alias = null; aliasAt = -1; }
            list.Add(new TableRef(schema, name, chain[^1], alias, aliasAt, insert));
            foreach (var c in chain) tablePositions.Add(c);
        }
        return list;
    }

    string Resolve(string? schema, string name, RefContext ctx)
    {
        if (schema != null) return schema;
        var schemas = ctx.SchemasOf(name);
        if (schemas.Contains(ctx.DefaultSchema)) return ctx.DefaultSchema;
        if (schemas.Contains("public")) return "public";
        return schemas.Count == 1 ? schemas.First() : ctx.DefaultSchema;
    }

    // ---------------------------------------------------------------------------------------------- find

    public RefScan Find(string text, RefTarget target, RefContext ctx)
    {
        var toks = Flatten(text);
        var hits = new Dictionary<int, RefHit>();
        var dynamic = false;
        void Hit(Tok t, RefConfidence c, string? reason = null, bool definition = false) =>
            hits.TryAdd(t.Offset, new RefHit(t.Offset, t.Length, c, reason, definition));
        bool IsTable(TableRef r) => Resolve(r.Schema, r.Name, ctx) == target.Schema && r.Name == target.Name;

        foreach (var (start, end) in Statements(toks))
        {
            if (Trigger(toks, start, end) is { } trigger)
            {
                FindInTrigger(toks, trigger, target, ctx, Hit);
                continue;
            }
            var tablePos = new HashSet<int>();
            var tables = Tables(toks, start, end, tablePos);
            var objectWord = start + 2 < end && toks[start].Is("CREATE") ? Header(toks, start, end) : -1;
            for (var i = start; i < end; i++)
            {
                var t = toks[i];
                if (t.Body && t.Plpgsql && t.Is("EXECUTE") && !(i + 1 < end && (toks[i + 1].Is("FUNCTION") || toks[i + 1].Is("PROCEDURE")))) dynamic = true;
                if (target.Kind == RefKind.Sequence && t.Kind == PgTokenKind.String && Sequence(toks, i, target, ctx) is { } seqHit) hits.TryAdd(seqHit.Offset, seqHit);
                if (!t.IsIdent || (i > start && toks[i - 1].Text == ".")) continue;
                var chain = Chain(toks, i, end);
                var last = toks[chain[^1]];
                var call = chain[^1] + 1 < end && toks[chain[^1] + 1].Text == "(";
                var cast = i > start && toks[i - 1].Text == "::";
                switch (target.Kind)
                {
                    case RefKind.Table or RefKind.View:
                        if (tablePos.Contains(chain[^1]) || chain[^1] == objectWord)
                        {
                            var schema = chain.Count >= 2 ? toks[chain[^2]].Name : null;
                            if (last.Name == target.Name && Resolve(schema, last.Name, ctx) == target.Schema)
                                Hit(last, schema == null && ctx.SchemasOf(target.Name).Count > 1 ? RefConfidence.Possible : RefConfidence.Exact,
                                    schema == null && ctx.SchemasOf(target.Name).Count > 1 ? "unqualified: depends on the search_path" : null, chain[^1] == objectWord);
                        }
                        else if (chain.Count >= 2)
                        {
                            // A table name used as a column qualifier: t.col / schema.t.col.
                            var tq = toks[chain[^2]];
                            if (tq.Name != target.Name) break;
                            if (chain.Count >= 3 ? toks[chain[^3]].Name == target.Schema : tables.Any(r => r.Alias == null && IsTable(r)))
                                Hit(tq, RefConfidence.Exact);
                        }
                        break;
                    case RefKind.Function or RefKind.Procedure:
                        if ((call || chain[^1] == objectWord) && last.Name == target.Name)
                        {
                            var schema = chain.Count >= 2 ? toks[chain[^2]].Name : null;
                            if (Resolve(schema, last.Name, ctx) == target.Schema) Hit(last, RefConfidence.Exact, null, chain[^1] == objectWord);
                        }
                        break;
                    case RefKind.Type:
                        if (cast && last.Name == target.Name && Resolve(chain.Count >= 2 ? toks[chain[^2]].Name : null, last.Name, ctx) == target.Schema) Hit(last, RefConfidence.Exact);
                        break;
                    case RefKind.Column:
                        if (last.Name != target.Column || call || tablePos.Contains(chain[^1]) || cast) break;
                        if (chain.Count == 1)
                        {
                            // An output alias (… AS name) or a declared name is not a reference.
                            if (i > start && toks[i - 1].Is("AS")) break;
                            if (InsertColumns(toks, start, i, tables) is { } insertTable)
                            {
                                if (IsTable(insertTable)) Hit(last, RefConfidence.Exact);
                                break;
                            }
                            var owners = tables.Where(r => !r.InsertTarget && (ctx.Columns(Resolve(r.Schema, r.Name, ctx), r.Name)?.Contains(target.Column!) ?? true)).ToList();
                            if (!owners.Any(IsTable)) break;
                            if (t.Plpgsql && t.Body) Hit(last, RefConfidence.Possible, "PL/pgSQL: an unqualified name may also be a variable");
                            else if (owners.Count == 1) Hit(last, RefConfidence.Exact);
                            else Hit(last, RefConfidence.Possible, $"{owners.Count} tables in scope may have a column {target.Column}");
                        }
                        else if (chain.Count == 2)
                        {
                            var q = toks[chain[0]].Name;
                            var owner = tables.FirstOrDefault(r => r.Alias == q) ?? tables.FirstOrDefault(r => r.Alias == null && r.Name == q);
                            if (owner != null && IsTable(owner)) Hit(last, RefConfidence.Exact);
                        }
                        else if (toks[chain[^2]].Name == target.Name && toks[chain[^3]].Name == target.Schema) Hit(last, RefConfidence.Exact);
                        break;
                }
                i = chain[^1];
            }
        }
        return new RefScan(hits.Values.OrderBy(h => h.Offset).ToList(), dynamic);
    }

    /// <summary>The parts of a CREATE TRIGGER statement, as token indices (chains are dotted names).</summary>
    sealed record TriggerParts(List<int> UpdateOf, List<int> Table, (int Start, int End)? When, List<int>? Function);

    /// <summary>
    /// CREATE [OR REPLACE] [CONSTRAINT] TRIGGER name {BEFORE|AFTER|INSTEAD OF} event [OR …] ON table … [WHEN (…)]
    /// EXECUTE {FUNCTION|PROCEDURE} f(…), as pg_get_triggerdef writes it; null when the statement is not one.
    /// </summary>
    static TriggerParts? Trigger(List<Tok> toks, int start, int end)
    {
        var i = start;
        if (i >= end || !toks[i].Is("CREATE")) return null;
        i++;
        if (i + 1 < end && toks[i].Is("OR") && toks[i + 1].Is("REPLACE")) i += 2;
        if (i < end && toks[i].Is("CONSTRAINT")) i++;
        if (i + 1 >= end || !toks[i].Is("TRIGGER")) return null;
        i += 2; // TRIGGER name
        var updateOf = new List<int>();
        for (; i < end && !toks[i].Is("ON"); i++)
        {
            if (!toks[i].Is("UPDATE") || i + 2 >= end || !toks[i + 1].Is("OF")) continue;
            for (i += 2; i < end && toks[i].IsIdent && !toks[i].Is("ON") && !toks[i].Is("OR"); i++)
            {
                updateOf.Add(i);
                if (i + 1 >= end || toks[i + 1].Text != ",") break;
                i++;
            }
        }
        if (i + 1 >= end || !toks[i + 1].IsIdent) return null;
        var table = Chain(toks, i + 1, end);
        (int, int)? when = null;
        List<int>? function = null;
        for (var j = table[^1] + 1; j < end; j++)
        {
            if (toks[j].Is("WHEN") && j + 1 < end && toks[j + 1].Text == "(")
            {
                var depth = 0;
                var k = j + 1;
                for (; k < end; k++)
                {
                    if (toks[k].Text == "(") depth++;
                    else if (toks[k].Text == ")" && --depth == 0) break;
                }
                when = (j + 2, Math.Min(k, end));
                j = k;
            }
            else if (toks[j].Is("EXECUTE") && j + 2 < end && (toks[j + 1].Is("FUNCTION") || toks[j + 1].Is("PROCEDURE")) && toks[j + 2].IsIdent)
            {
                function = Chain(toks, j + 2, end);
                break;
            }
        }
        return new TriggerParts(updateOf, table, when, function);
    }

    /// <summary>
    /// A trigger references its table (ON), its function (EXECUTE FUNCTION), and columns of its table: the UPDATE OF
    /// list and NEW.col / OLD.col in the WHEN condition.
    /// </summary>
    void FindInTrigger(List<Tok> toks, TriggerParts p, RefTarget target, RefContext ctx, Action<Tok, RefConfidence, string?, bool> hit)
    {
        string? SchemaOf(List<int> chain) => chain.Count >= 2 ? toks[chain[^2]].Name : null;
        var tableName = toks[p.Table[^1]].Name;
        var onTarget = tableName == target.Name && Resolve(SchemaOf(p.Table), tableName, ctx) == target.Schema;
        switch (target.Kind)
        {
            case RefKind.Table or RefKind.View:
                if (onTarget) hit(toks[p.Table[^1]], RefConfidence.Exact, null, false);
                break;
            case RefKind.Function or RefKind.Procedure:
                if (p.Function is { } f && toks[f[^1]].Name == target.Name && Resolve(SchemaOf(f), target.Name, ctx) == target.Schema)
                    hit(toks[f[^1]], RefConfidence.Exact, null, false);
                break;
            case RefKind.Column when onTarget:
                foreach (var c in p.UpdateOf)
                    if (toks[c].Name == target.Column) hit(toks[c], RefConfidence.Exact, null, false);
                if (p.When is { } w)
                    for (var k = w.Start; k + 2 < w.End; k++)
                        if ((toks[k].Is("NEW") || toks[k].Is("OLD")) && toks[k + 1].Text == "." && toks[k + 2].IsIdent && toks[k + 2].Name == target.Column)
                            hit(toks[k + 2], RefConfidence.Exact, null, false);
                break;
        }
    }

    /// <summary>The token index of the object name in CREATE [OR REPLACE] [MATERIALIZED] VIEW/FUNCTION/PROCEDURE/TABLE name, or -1.</summary>
    static int Header(List<Tok> toks, int start, int end)
    {
        var i = start + 1;
        if (i + 1 < end && toks[i].Is("OR") && toks[i + 1].Is("REPLACE")) i += 2;
        while (i < end && (toks[i].Is("MATERIALIZED") || toks[i].Is("TEMP") || toks[i].Is("TEMPORARY") || toks[i].Is("UNLOGGED"))) i++;
        if (i >= end || !(toks[i].Is("VIEW") || toks[i].Is("FUNCTION") || toks[i].Is("PROCEDURE") || toks[i].Is("TABLE"))) return -1;
        i++;
        if (i >= end || !toks[i].IsIdent) return -1;
        var chain = Chain(toks, i, end);
        return chain[^1];
    }

    /// <summary>Inside INSERT INTO t (a, b, …): the table whose column list holds token <paramref name="i"/>.</summary>
    static TableRef? InsertColumns(List<Tok> toks, int start, int i, List<TableRef> tables)
    {
        var depth = 0;
        for (var j = i - 1; j >= start; j--)
        {
            if (toks[j].Text == ")") depth++;
            else if (toks[j].Text == "(")
            {
                if (depth > 0) { depth--; continue; }
                var name = j - 1;
                if (name >= start + 2 && toks[name].IsIdent)
                {
                    var c = name;
                    while (c - 2 >= start && toks[c - 1].Text == "." && toks[c - 2].IsIdent) c -= 2;
                    if (c - 2 >= start && toks[c - 1].Is("INTO") && toks[c - 2].Is("INSERT")) return tables.FirstOrDefault(r => r.NameIndex == name);
                }
                return null;
            }
        }
        return null;
    }

    /// <summary>nextval('s.seq') / currval / setval: the sequence named inside the literal.</summary>
    RefHit? Sequence(List<Tok> toks, int i, RefTarget target, RefContext ctx)
    {
        if (i < 2 || toks[i - 1].Text != "(" || !(toks[i - 2].Is("nextval") || toks[i - 2].Is("currval") || toks[i - 2].Is("setval"))) return null;
        var t = toks[i];
        var inner = t.Text.Length >= 2 ? t.Text[1..^1] : "";
        var dot = inner.LastIndexOf('.');
        var schema = dot > 0 ? inner[..dot].Trim('"') : null;
        var name = (dot > 0 ? inner[(dot + 1)..] : inner).Trim('"');
        var nameAt = dot > 0 ? dot + 1 : 0;
        if (!string.Equals(name, target.Name, StringComparison.Ordinal) || Resolve(schema?.ToLowerInvariant(), name, ctx) != target.Schema) return null;
        return new RefHit(t.Offset + 1 + nameAt, (dot > 0 ? inner[(dot + 1)..] : inner).Length, RefConfidence.Exact);
    }

    // ---------------------------------------------------------------------------------------------- target at cursor

    public RefTarget? TargetAt(string text, int offset, RefContext ctx)
    {
        var toks = Flatten(text);
        var at = toks.FindIndex(t => t.IsIdent && t.Offset <= offset && offset <= t.Offset + t.Length);
        if (at < 0) return null;
        var (start, end) = Statements(toks).First(s => s.Start <= at && at < s.End);
        var tablePos = new HashSet<int>();
        var tables = Tables(toks, start, end, tablePos);
        var first = at;
        while (first - 2 >= start && toks[first - 1].Text == "." && toks[first - 2].IsIdent) first -= 2;
        var chain = Chain(toks, first, end);
        var pos = chain.IndexOf(at);
        var call = chain[^1] + 1 < end && toks[chain[^1] + 1].Text == "(";
        var header = toks[start].Is("CREATE") ? Header(toks, start, end) : -1;
        if (chain[^1] == header && pos == chain.Count - 1)
        {
            var kind = Enumerable.Range(start, header - start).Any(k => toks[k].Is("VIEW")) ? RefKind.View
                : Enumerable.Range(start, header - start).Any(k => toks[k].Is("PROCEDURE")) ? RefKind.Procedure
                : Enumerable.Range(start, header - start).Any(k => toks[k].Is("FUNCTION")) ? RefKind.Function : RefKind.Table;
            return new RefTarget(kind, Resolve(chain.Count >= 2 ? toks[chain[^2]].Name : null, toks[at].Name, ctx), toks[at].Name);
        }
        if (tablePos.Contains(chain[^1]))
            return pos == chain.Count - 1 ? new RefTarget(RefKind.Table, Resolve(chain.Count >= 2 ? toks[chain[^2]].Name : null, toks[at].Name, ctx), toks[at].Name) : null;
        if (call && pos == chain.Count - 1)
            return new RefTarget(RefKind.Function, Resolve(chain.Count >= 2 ? toks[chain[^2]].Name : null, toks[at].Name, ctx), toks[at].Name);
        if (pos == chain.Count - 1)
        {
            // A column: through its qualifier, or the only table in scope that has it.
            TableRef? owner = null;
            if (chain.Count == 1)
            {
                var owners = tables.Where(r => ctx.Columns(Resolve(r.Schema, r.Name, ctx), r.Name)?.Contains(toks[at].Name) ?? false).ToList();
                owner = owners.Count == 1 ? owners[0] : null;
            }
            else if (chain.Count == 2)
            {
                var q = toks[chain[0]].Name;
                owner = tables.FirstOrDefault(r => r.Alias == q) ?? tables.FirstOrDefault(r => r.Alias == null && r.Name == q);
            }
            else owner = new TableRef(toks[chain[^3]].Name, toks[chain[^2]].Name, -1, null, -1);
            return owner == null ? null : new RefTarget(RefKind.Column, Resolve(owner.Schema, owner.Name, ctx), owner.Name, toks[at].Name);
        }
        if (pos == chain.Count - 2 && chain.Count >= 2)
        {
            var q = toks[at].Name;
            if (chain.Count >= 3) return new RefTarget(RefKind.Table, toks[chain[^3]].Name, q);
            var owner = tables.FirstOrDefault(r => r.Alias == null && r.Name == q);
            return owner == null ? null : new RefTarget(RefKind.Table, Resolve(owner.Schema, owner.Name, ctx), owner.Name);
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------- local symbols

    public IReadOnlyList<(int Offset, int Length)>? LocalOccurrences(string text, int offset)
    {
        var toks = Flatten(text);
        var at = toks.FindIndex(t => t.IsIdent && t.Offset <= offset && offset <= t.Offset + t.Length);
        if (at < 0) return null;
        var name = toks[at].Name;
        var statements = Statements(toks);
        var (start, end) = statements.First(s => s.Start <= at && at < s.End);
        var tablePos = new HashSet<int>();
        var tables = Tables(toks, start, end, tablePos);
        List<(int, int)> Spans(IEnumerable<int> idx) => idx.Distinct().OrderBy(i => i).Select(i => (toks[i].Offset, toks[i].Length)).ToList();

        // Alias: its definition and its uses as a qualifier in the statement.
        var alias = tables.FirstOrDefault(r => r.Alias == name);
        if (alias != null && (alias.AliasIndex == at || (at + 1 < end && toks[at + 1].Text == ".")))
            return Spans(new[] { alias.AliasIndex }.Concat(Enumerable.Range(start, end - start).Where(i => toks[i].IsIdent && toks[i].Name == name && i + 1 < end && toks[i + 1].Text == "." && (i == start || toks[i - 1].Text != "."))));

        // CTE: WITH name AS ( / , name AS (, and its uses as a table in the statement.
        var ctes = Enumerable.Range(start, end - start)
            .Where(i => toks[i].IsIdent && i + 2 < end && toks[i + 1].Is("AS") && toks[i + 2].Text == "(" && i > start && (toks[i - 1].Is("WITH") || toks[i - 1].Is("RECURSIVE") || toks[i - 1].Text == ","))
            .ToList();
        if (ctes.FirstOrDefault(i => toks[i].Name == name) is var def and > 0 && toks[def].Name == name && (def == at || tablePos.Contains(at) || (at + 1 < end && toks[at + 1].Text == ".")))
            return Spans(new[] { def }.Concat(Enumerable.Range(start, end - start).Where(i => toks[i].IsIdent && toks[i].Name == name && (tablePos.Contains(i) || (i + 1 < end && toks[i + 1].Text == "." && (i == start || toks[i - 1].Text != "."))))));

        // PL/pgSQL variable or a parameter: declared in DECLARE … BEGIN or the header's argument list; every bare use in the text.
        if (toks[at].Body || IsParameter(toks, name))
        {
            var declared = Declared(toks).Contains(name) || IsParameter(toks, name);
            if (declared)
                return Spans(Enumerable.Range(0, toks.Count).Where(i => toks[i].IsIdent && toks[i].Name == name && (i == 0 || toks[i - 1].Text != ".") && !(i + 1 < toks.Count && toks[i + 1].Text == "(")));
        }
        return null;
    }

    static HashSet<string> Declared(List<Tok> toks)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < toks.Count; i++)
        {
            if (!toks[i].Body || !toks[i].Is("DECLARE")) continue;
            var expectName = true;
            for (var j = i + 1; j < toks.Count && !toks[j].Is("BEGIN"); j++)
            {
                if (expectName && toks[j].IsIdent) { set.Add(toks[j].Name); expectName = false; }
                if (toks[j].Text == ";") expectName = true;
            }
        }
        return set;
    }

    /// <summary>The name is an argument of the CREATE FUNCTION / PROCEDURE header.</summary>
    static bool IsParameter(List<Tok> toks, string name)
    {
        if (toks.Count < 3 || !toks[0].Is("CREATE")) return false;
        var header = Header(toks, 0, toks.Count);
        if (header < 0 || header + 1 >= toks.Count || toks[header + 1].Text != "(") return false;
        var depth = 0;
        var expectName = true;
        for (var j = header + 1; j < toks.Count; j++)
        {
            if (toks[j].Text == "(") { depth++; if (depth == 1) continue; }
            if (toks[j].Text == ")") { depth--; if (depth == 0) break; }
            if (depth == 1 && toks[j].Text == ",") { expectName = true; continue; }
            if (depth == 1 && expectName && toks[j].IsIdent)
            {
                if (toks[j].Is("IN") || toks[j].Is("OUT") || toks[j].Is("INOUT") || toks[j].Is("VARIADIC")) continue;
                if (toks[j].Name == name) return true;
                expectName = false;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------- renames

    static bool PlainName(string s) =>
        s.Length > 0 && (char.IsAsciiLetterLower(s[0]) || s[0] == '_') && s.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_');

    /// <summary>
    /// Keywords that PostgreSQL accepts as a column or table name but not as a function or type name (col_name_keyword):
    /// a rename quotes them as well, since one name may stand in every position.
    /// </summary>
    static readonly HashSet<string> ColumnNameKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "between", "bigint", "bit", "boolean", "char", "character", "coalesce", "dec", "decimal", "exists", "extract", "float",
        "greatest", "grouping", "inout", "int", "integer", "interval", "json", "json_array", "json_arrayagg", "json_exists",
        "json_object", "json_objectagg", "json_query", "json_scalar", "json_serialize", "json_table", "json_value", "least",
        "merge_action", "national", "nchar", "none", "normalize", "nullif", "numeric", "out", "overlay", "position", "precision",
        "real", "row", "setof", "smallint", "substring", "time", "timestamp", "treat", "trim", "values", "varchar",
        "xmlattributes", "xmlconcat", "xmlelement", "xmlexists", "xmlforest", "xmlnamespaces", "xmlparse", "xmlpi", "xmlroot",
        "xmlserialize", "xmltable",
    };

    /// <summary>A lower-case regular name that is no reserved word (USER, ORDER, SELECT …, L9) and no column-name keyword.</summary>
    static bool Bare(string s) => PlainName(s) && !PgSplitter.Reserved.Contains(s) && !ColumnNameKeywords.Contains(s);

    public string Rename(string original, string newName) =>
        !original.StartsWith('"') && Bare(newName) ? newName : "\"" + newName.Replace("\"", "\"\"") + "\"";

    public bool ServerTracked(string definition, ObjectKind kind)
    {
        // A trigger stores its table, UPDATE OF columns (attnums), WHEN tree and function by oid.
        if (kind is ObjectKind.View or ObjectKind.Trigger) return true;
        var toks = PgTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList();
        for (var i = 0; i + 1 < toks.Count; i++)
            if (toks[i].Is("BEGIN") && toks[i + 1].Is("ATOMIC")) return true;
        return false;
    }
}
