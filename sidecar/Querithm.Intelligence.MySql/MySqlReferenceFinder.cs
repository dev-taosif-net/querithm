using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.MySql;

/// <summary>
/// References to a table, view, routine or column on <see cref="MySqlTokenizer"/> tokens (Phase D2), stored-program bodies
/// included: table references after FROM / JOIN / UPDATE / INSERT [INTO] / REPLACE [INTO] / TABLE with their aliases per
/// statement, qualified and unqualified columns, calls (CALL p(), f(…)), and a trigger's table with NEW.col / OLD.col.
/// Strings and comments are never matched. A bare table name resolves to the definition's own database (the context's
/// default); table names compare exactly, column names case-insensitively, as the server does. Inside a stored program an
/// unqualified name may be a local variable or parameter, so such column hits are only "possible"; PREPARE … FROM marks
/// dynamic SQL.
/// </summary>
public sealed class MySqlReferenceFinder : IReferenceFinder
{
    static readonly StringComparer Cols = StringComparer.OrdinalIgnoreCase;

    readonly record struct Tok(MySqlTokenKind Kind, int Offset, int Length, string Text, bool Body)
    {
        public bool IsIdent => Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent;
        public string Name => Kind == MySqlTokenKind.QuotedIdent && Text.Length >= 2 ? Text[1..^1].Replace("``", "`") : Text;
        public bool Is(string word) => Kind == MySqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Non-trivia tokens; those after the first BEGIN of a CREATE PROCEDURE / FUNCTION / TRIGGER are a stored-program body.</summary>
    static List<Tok> Flatten(string text)
    {
        var raw = MySqlTokenizer.Tokenize(text).Where(t => !t.IsTrivia).ToList();
        var program = raw.Count > 1 && raw[0].Is("CREATE") && raw.Take(12).Any(t => t.Is("PROCEDURE") || t.Is("FUNCTION") || t.Is("TRIGGER"));
        var body = false;
        var list = new List<Tok>(raw.Count);
        foreach (var t in raw)
        {
            if (program && !body && (t.Is("BEGIN") || t.Is("RETURN"))) body = true;
            list.Add(new Tok(t.Kind, t.Offset, t.Length, t.Text, body));
        }
        return list;
    }

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
        "where", "join", "inner", "left", "right", "cross", "straight_join", "natural", "on", "using", "group", "order", "having", "limit", "union",
        "except", "intersect", "set", "values", "value", "window", "for", "lock", "and", "or", "select", "from", "as", "with", "into", "partition",
        "use", "ignore", "force", "key", "index", "then", "else", "end", "when", "is", "not", "null", "like", "in", "between", "outer", "lateral",
        "do", "default", "returning", "duplicate", "low_priority", "high_priority", "delayed", "quick",
    ], StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> EndsFrom = new(["where", "group", "order", "having", "limit", "union", "except", "intersect", "window", "for", "into", "set", "values", "on", "using", "lock", "returning"],
        StringComparer.OrdinalIgnoreCase);

    sealed record TableRef(string? Schema, string Name, int NameIndex, string? Alias, int AliasIndex, bool InsertTarget = false);

    static List<int> Chain(List<Tok> toks, int i, int end)
    {
        var parts = new List<int> { i };
        while (parts[^1] + 2 < end && toks[parts[^1] + 1].Text == "." && toks[parts[^1] + 2].IsIdent) parts.Add(parts[^1] + 2);
        return parts;
    }

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
            if (t.Kind == MySqlTokenKind.Word && EndsFrom.Contains(t.Text) && depth <= depthAtFrom) inFrom = false;
            var insert = t.Is("INSERT") || t.Is("REPLACE");
            var introduces = t.Is("FROM") || t.Is("JOIN") || t.Is("STRAIGHT_JOIN") || t.Is("UPDATE") || (t.Is("INTO") && i > start && (toks[i - 1].Is("INSERT") || toks[i - 1].Is("REPLACE") || toks[i - 1].Is("IGNORE")))
                             || (insert && i + 1 < end && !toks[i + 1].Is("INTO") && i == start) || (t.Is("TABLE") && i + 1 < end) || (t.Text == "," && inFrom && depth == depthAtFrom);
            if (!introduces) continue;
            if (t.Is("FROM") || t.Is("JOIN") || t.Is("STRAIGHT_JOIN") || t.Is("UPDATE")) { inFrom = true; depthAtFrom = depth; }
            var j = i + 1;
            while (j < end && (toks[j].Is("LATERAL") || toks[j].Is("LOW_PRIORITY") || toks[j].Is("IGNORE") || toks[j].Is("DELAYED") || toks[j].Is("HIGH_PRIORITY"))) j++;
            if (t.Is("TABLE")) while (j < end && (toks[j].Is("IF") || toks[j].Is("EXISTS") || toks[j].Is("NOT"))) j++;
            if (j >= end || !toks[j].IsIdent || (toks[j].Kind == MySqlTokenKind.Word && NotAlias.Contains(toks[j].Text))) continue;
            var chain = Chain(toks, j, end);
            var after = chain[^1] + 1;
            if (after < end && toks[after].Text == "(" && (t.Is("FROM") || t.Is("JOIN") || t.Text == ",")) continue; // a table function, not a table
            string? alias = null;
            var aliasAt = -1;
            if (after < end && toks[after].Is("AS") && after + 1 < end && toks[after + 1].IsIdent) { alias = toks[after + 1].Name; aliasAt = after + 1; }
            else if (after < end && toks[after].IsIdent && !(toks[after].Kind == MySqlTokenKind.Word && NotAlias.Contains(toks[after].Text))) { alias = toks[after].Name; aliasAt = after; }
            var isInsert = t.Is("INTO") || insert;
            if (isInsert) { alias = null; aliasAt = -1; }
            list.Add(new TableRef(chain.Count >= 2 ? toks[chain[^2]].Name : null, toks[chain[^1]].Name, chain[^1], alias, aliasAt, isInsert));
            foreach (var c in chain) tablePositions.Add(c);
        }
        return list;
    }

    static string Resolve(string? schema, RefContext ctx) => schema ?? ctx.DefaultSchema;

    // ---------------------------------------------------------------------------------------------- find

    public RefScan Find(string text, RefTarget target, RefContext ctx)
    {
        var toks = Flatten(text);
        var hits = new Dictionary<int, RefHit>();
        void Hit(Tok t, RefConfidence c, string? reason = null, bool definition = false) => hits.TryAdd(t.Offset, new RefHit(t.Offset, t.Length, c, reason, definition));
        bool IsTable(TableRef r) => Resolve(r.Schema, ctx) == target.Schema && r.Name == target.Name;
        var dynamic = toks.Select((t, i) => (t, i)).Any(x => x.t.Is("PREPARE") && x.i + 2 < toks.Count && toks.Skip(x.i).Take(4).Any(y => y.Is("FROM")));

        if (Trigger(toks) is { } trig) FindInTrigger(toks, trig, target, ctx, Hit);
        foreach (var (start, end) in Statements(toks))
        {
            var tablePos = new HashSet<int>();
            var tables = Tables(toks, start, end, tablePos);
            var objectWord = toks[start].Is("CREATE") ? Header(toks, start, end) : -1;
            for (var i = start; i < end; i++)
            {
                var t = toks[i];
                if (!t.IsIdent || (i > start && toks[i - 1].Text == ".")) continue;
                var chain = Chain(toks, i, end);
                var last = toks[chain[^1]];
                var call = chain[^1] + 1 < end && toks[chain[^1] + 1].Text == "(";
                switch (target.Kind)
                {
                    case RefKind.Table or RefKind.View:
                        if (tablePos.Contains(chain[^1]) || chain[^1] == objectWord)
                        {
                            var schema = chain.Count >= 2 ? toks[chain[^2]].Name : null;
                            if (last.Name == target.Name && Resolve(schema, ctx) == target.Schema) Hit(last, RefConfidence.Exact, null, chain[^1] == objectWord);
                        }
                        else if (chain.Count >= 2)
                        {
                            // A table name used as a column qualifier: t.col / db.t.col.
                            var tq = toks[chain[^2]];
                            if (tq.Name != target.Name) break;
                            if (chain.Count >= 3 ? toks[chain[^3]].Name == target.Schema : tables.Any(r => r.Alias == null && IsTable(r))) Hit(tq, RefConfidence.Exact);
                        }
                        break;
                    case RefKind.Function or RefKind.Procedure:
                        if ((call || chain[^1] == objectWord) && Cols.Equals(last.Name, target.Name)) // routine names are case-insensitive
                        {
                            var schema = chain.Count >= 2 ? toks[chain[^2]].Name : null;
                            if (Resolve(schema, ctx) == target.Schema) Hit(last, RefConfidence.Exact, null, chain[^1] == objectWord);
                        }
                        break;
                    case RefKind.Column:
                        if (!Cols.Equals(last.Name, target.Column) || call || tablePos.Contains(chain[^1])) break;
                        if (chain.Count == 1)
                        {
                            if (i > start && toks[i - 1].Is("AS")) break; // an output alias
                            if (InsertColumns(toks, start, i, tables) is { } insertTable)
                            {
                                if (IsTable(insertTable)) Hit(last, RefConfidence.Exact);
                                break;
                            }
                            var owners = tables.Where(r => !r.InsertTarget && (ctx.Columns(Resolve(r.Schema, ctx), r.Name)?.Contains(target.Column!, Cols) ?? true)).ToList();
                            // UPDATE t SET col = … : the column belongs to the updated table.
                            if (owners.Count == 0 && tables.Count == 1 && tables[0].InsertTarget == false) owners = tables;
                            if (!owners.Any(IsTable)) break;
                            if (t.Body) Hit(last, RefConfidence.Possible, "stored program: an unqualified name may also be a local variable or parameter");
                            else if (owners.Count == 1) Hit(last, RefConfidence.Exact);
                            else Hit(last, RefConfidence.Possible, $"{owners.Count} tables in scope may have a column {target.Column}");
                        }
                        else if (chain.Count == 2)
                        {
                            var q = toks[chain[0]].Name;
                            if (toks[chain[0]].Is("NEW") || toks[chain[0]].Is("OLD")) break; // the trigger's own rows (FindInTrigger)
                            var owner = tables.FirstOrDefault(r => r.Alias != null && Cols.Equals(r.Alias, q)) ?? tables.FirstOrDefault(r => r.Alias == null && r.Name == q);
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

    /// <summary>CREATE [DEFINER = …] TRIGGER [IF NOT EXISTS] name {BEFORE|AFTER} event ON table FOR EACH ROW …: the table's chain.</summary>
    static List<int>? Trigger(List<Tok> toks)
    {
        if (toks.Count < 6 || !toks[0].Is("CREATE")) return null;
        var t = toks.FindIndex(x => x.Is("TRIGGER"));
        if (t < 0 || t > 12) return null;
        var on = toks.FindIndex(t, x => x.Is("ON"));
        if (on < 0 || on + 1 >= toks.Count || !toks[on + 1].IsIdent) return null;
        return Chain(toks, on + 1, toks.Count);
    }

    static void FindInTrigger(List<Tok> toks, List<int> table, RefTarget target, RefContext ctx, Action<Tok, RefConfidence, string?, bool> hit)
    {
        var name = toks[table[^1]].Name;
        var onTarget = name == target.Name && Resolve(table.Count >= 2 ? toks[table[^2]].Name : null, ctx) == target.Schema;
        if (!onTarget) return;
        if (target.Kind is RefKind.Table or RefKind.View) hit(toks[table[^1]], RefConfidence.Exact, null, false);
        if (target.Kind != RefKind.Column) return;
        for (var k = 0; k + 2 < toks.Count; k++)
            if ((toks[k].Is("NEW") || toks[k].Is("OLD")) && toks[k + 1].Text == "." && toks[k + 2].IsIdent && Cols.Equals(toks[k + 2].Name, target.Column))
                hit(toks[k + 2], RefConfidence.Exact, null, false);
    }

    /// <summary>The object name in CREATE [OR REPLACE] [ALGORITHM=…] [DEFINER=…] [SQL SECURITY …] VIEW / FUNCTION / PROCEDURE / TABLE name, or -1.</summary>
    static int Header(List<Tok> toks, int start, int end)
    {
        for (var i = start + 1; i < Math.Min(end, start + 16); i++)
        {
            if (!(toks[i].Is("VIEW") || toks[i].Is("FUNCTION") || toks[i].Is("PROCEDURE") || toks[i].Is("TABLE"))) continue;
            var j = i + 1;
            while (j < end && (toks[j].Is("IF") || toks[j].Is("NOT") || toks[j].Is("EXISTS"))) j++;
            return j < end && toks[j].IsIdent ? Chain(toks, j, end)[^1] : -1;
        }
        return -1;
    }

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
                if (name >= start + 1 && toks[name].IsIdent) return tables.FirstOrDefault(r => r.NameIndex == name && r.InsertTarget);
                return null;
            }
        }
        return null;
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
        string Schema() => Resolve(chain.Count >= 2 ? toks[chain[^2]].Name : null, ctx);
        var header = toks[start].Is("CREATE") ? Header(toks, start, end) : -1;
        if (chain[^1] == header && pos == chain.Count - 1)
        {
            var words = Enumerable.Range(start, header - start).Select(k => toks[k]).ToList();
            var kind = words.Any(w => w.Is("VIEW")) ? RefKind.View : words.Any(w => w.Is("PROCEDURE")) ? RefKind.Procedure : words.Any(w => w.Is("FUNCTION")) ? RefKind.Function : RefKind.Table;
            return new RefTarget(kind, Schema(), toks[at].Name);
        }
        if (tablePos.Contains(chain[^1])) return pos == chain.Count - 1 ? new RefTarget(RefKind.Table, Schema(), toks[at].Name) : null;
        if (call && pos == chain.Count - 1)
            return new RefTarget(start + 1 < end && toks[start].Is("CALL") && at <= start + 3 ? RefKind.Procedure : RefKind.Function, Schema(), toks[at].Name);
        if (pos == chain.Count - 1)
        {
            TableRef? owner = null;
            if (chain.Count == 1)
            {
                var owners = tables.Where(r => ctx.Columns(Resolve(r.Schema, ctx), r.Name)?.Contains(toks[at].Name, Cols) ?? false).ToList();
                owner = owners.Count == 1 ? owners[0] : null;
            }
            else if (chain.Count == 2)
            {
                var q = toks[chain[0]].Name;
                owner = tables.FirstOrDefault(r => r.Alias != null && Cols.Equals(r.Alias, q)) ?? tables.FirstOrDefault(r => r.Alias == null && r.Name == q);
            }
            else owner = new TableRef(toks[chain[^3]].Name, toks[chain[^2]].Name, -1, null, -1);
            if (owner == null) return null;
            var cols = ctx.Columns(Resolve(owner.Schema, ctx), owner.Name);
            var column = cols?.FirstOrDefault(c => Cols.Equals(c, toks[at].Name)) ?? toks[at].Name; // the declared spelling
            return new RefTarget(RefKind.Column, Resolve(owner.Schema, ctx), owner.Name, column);
        }
        if (pos == chain.Count - 2 && chain.Count >= 2)
        {
            var q = toks[at].Name;
            if (chain.Count >= 3) return new RefTarget(RefKind.Table, toks[chain[^3]].Name, q);
            var owner = tables.FirstOrDefault(r => r.Alias == null && r.Name == q);
            return owner == null ? null : new RefTarget(RefKind.Table, Resolve(owner.Schema, ctx), owner.Name);
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
        var (start, end) = Statements(toks).First(s => s.Start <= at && at < s.End);
        var tablePos = new HashSet<int>();
        var tables = Tables(toks, start, end, tablePos);
        List<(int, int)> Spans(IEnumerable<int> idx) => idx.Distinct().OrderBy(i => i).Select(i => (toks[i].Offset, toks[i].Length)).ToList();
        bool Same(string a) => Cols.Equals(a, name); // aliases and variables are case-insensitive

        var alias = tables.FirstOrDefault(r => r.Alias != null && Same(r.Alias));
        if (alias != null && (alias.AliasIndex == at || (at + 1 < end && toks[at + 1].Text == ".")))
            return Spans(new[] { alias.AliasIndex }.Concat(Enumerable.Range(start, end - start).Where(i => toks[i].IsIdent && Same(toks[i].Name) && i + 1 < end && toks[i + 1].Text == "." && (i == start || toks[i - 1].Text != "."))));

        var ctes = Enumerable.Range(start, end - start)
            .Where(i => toks[i].IsIdent && i + 2 < end && toks[i + 1].Is("AS") && toks[i + 2].Text == "(" && i > start && (toks[i - 1].Is("WITH") || toks[i - 1].Is("RECURSIVE") || toks[i - 1].Text == ","))
            .ToList();
        if (ctes.FirstOrDefault(i => toks[i].Name == name) is var def and > 0 && toks[def].Name == name && (def == at || tablePos.Contains(at) || (at + 1 < end && toks[at + 1].Text == ".")))
            return Spans(new[] { def }.Concat(Enumerable.Range(start, end - start).Where(i => toks[i].IsIdent && toks[i].Name == name && (tablePos.Contains(i) || (i + 1 < end && toks[i + 1].Text == "." && (i == start || toks[i - 1].Text != "."))))));

        // A stored program's DECLAREd variable or its parameter: every bare use in the text.
        if (toks[at].Body || IsParameter(toks, name))
        {
            if (Declared(toks).Contains(name) || IsParameter(toks, name))
                return Spans(Enumerable.Range(0, toks.Count).Where(i => toks[i].IsIdent && Same(toks[i].Name) && (i == 0 || toks[i - 1].Text != ".") && !(i + 1 < toks.Count && toks[i + 1].Text == "(")));
        }
        return null;
    }

    static HashSet<string> Declared(List<Tok> toks)
    {
        var set = new HashSet<string>(Cols);
        for (var i = 0; i + 1 < toks.Count; i++)
        {
            if (!toks[i].Body || !toks[i].Is("DECLARE")) continue;
            // DECLARE a, b INT; — not DECLARE c CURSOR / CONTINUE HANDLER (still names, harmless) …
            for (var j = i + 1; j < toks.Count && toks[j].IsIdent; j += 2)
            {
                set.Add(toks[j].Name);
                if (j + 1 >= toks.Count || toks[j + 1].Text != ",") break;
            }
        }
        return set;
    }

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
                if (toks[j].Is("IN") || toks[j].Is("OUT") || toks[j].Is("INOUT")) continue;
                if (Cols.Equals(toks[j].Name, name)) return true;
                expectName = false;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------- renames

    static bool PlainName(string s) => s.Length > 0 && !char.IsAsciiDigit(s[0]) && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '$');

    public string Rename(string original, string newName) =>
        !original.StartsWith('`') && PlainName(newName) && !MySqlKeywords.IsReserved(newName) ? newName : MySqlTokenizer.Quote(newName);

    /// <summary>MySQL / MariaDB store definitions as text: no view, routine or trigger follows a rename by itself.</summary>
    public bool ServerTracked(string definition, ObjectKind kind) => false;
}
