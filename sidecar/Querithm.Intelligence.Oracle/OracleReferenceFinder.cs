using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.Oracle;

/// <summary>
/// References to a table, view, sequence, standalone routine, type or column on <see cref="OracleTokenizer"/> tokens
/// (D4 S11), in SQL and in PL/SQL units (procedures, functions, triggers, package specifications and bodies). Strings,
/// <c>q'…'</c> literals, comments and hints are never matched; names compare by their dictionary spelling (unquoted
/// folded to upper case, quoted exact — <see cref="OracleIdentifiers.Identity(OracleToken)"/>).
/// <list type="bullet">
/// <item>Tables after FROM / JOIN / UPDATE / INSERT INTO / MERGE INTO / USING / DELETE [FROM] / TABLE (ALTER, DROP,
/// TRUNCATE, LOCK) / REFERENCES / CREATE INDEX … ON / a trigger's ON, with their aliases; <c>t%ROWTYPE</c> and
/// <c>t.col%TYPE</c>; a remote name (<c>t@link</c>) is never this database's object.</item>
/// <item>Columns qualified by a table or alias, unqualified ones where exactly one table in the statement has them (Oracle
/// resolves a column before a PL/SQL variable inside SQL, so this is exact), a trigger's <c>:NEW.col</c> / <c>:OLD.col</c>
/// and <c>UPDATE OF col</c>, the column list of an INSERT; a field of a cursor FOR loop's record over the table is only
/// possible (the record's fields are the query's columns, which an alias may rename).</item>
/// <item>Routines called with or without parentheses (a PL/SQL call statement may have none), sequences through
/// <c>NEXTVAL</c> / <c>CURRVAL</c>; <c>A.B(</c> is schema A's routine B only when A is that schema (else a package member).
/// A unit's own name in its header and after its final END is the definition.</item>
/// </list>
/// A bare name resolves in the context's default schema (a unit's owner; a tab's current schema). EXECUTE IMMEDIATE,
/// DBMS_SQL.PARSE and OPEN … FOR a string mark dynamic SQL.
/// </summary>
public sealed class OracleReferenceFinder : IReferenceFinder
{
    readonly record struct Tok(OracleTokenKind Kind, int Offset, int Length, string Text, int Unit)
    {
        public bool IsIdent => Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent;
        public bool IsBind => Kind == OracleTokenKind.Bind;
        public string Name => Kind switch
        {
            OracleTokenKind.QuotedIdent => Text.Length >= 2 ? Text[1..^1] : Text,
            OracleTokenKind.Bind => OracleIdentifiers.Identity(new OracleToken(Kind, Offset, Length, 0, Text)),
            _ => Text.ToUpperInvariant(),
        };
        public bool Is(string word) => Kind == OracleTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
        public bool IsP(string p) => Kind is OracleTokenKind.Punct or OracleTokenKind.Operator && Text == p;
    }

    /// <summary>Non-trivia tokens, each with the index of the splitter's unit (statement or PL/SQL block) it belongs to.</summary>
    static List<Tok> Flatten(string text)
    {
        var list = new List<Tok>();
        var unit = 0;
        foreach (var st in OracleSplitter.Split(text))
        {
            if (st.Kind == OracleStatementKind.ClientCommand) { unit++; continue; }
            foreach (var t in st.Code) list.Add(new Tok(t.Kind, t.Offset, t.Length, t.Text, unit));
            unit++;
        }
        return list;
    }

    /// <summary>Segments ending at a top-level ';' within one unit (a PL/SQL block is many statements).</summary>
    static List<(int Start, int End)> Statements(List<Tok> toks)
    {
        var list = new List<(int, int)>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < toks.Count; i++)
        {
            var boundary = i > start && toks[i].Unit != toks[i - 1].Unit;
            if (boundary)
            {
                list.Add((start, i));
                start = i;
                depth = 0;
            }
            if (toks[i].IsP("(")) depth++;
            else if (toks[i].IsP(")")) depth = Math.Max(0, depth - 1);
            else if (toks[i].IsP(";") && depth == 0)
            {
                if (i > start) list.Add((start, i));
                start = i + 1;
            }
        }
        if (start < toks.Count) list.Add((start, toks.Count));
        return list;
    }

    static readonly HashSet<string> NotAlias = new([
        "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER", "ON", "USING", "GROUP", "ORDER", "HAVING", "UNION", "MINUS",
        "EXCEPT", "INTERSECT", "SET", "VALUES", "FOR", "AND", "OR", "SELECT", "FROM", "AS", "WITH", "INTO", "PARTITION", "SUBPARTITION", "SAMPLE",
        "THEN", "ELSE", "END", "WHEN", "IS", "NOT", "NULL", "LIKE", "IN", "BETWEEN", "LATERAL", "DEFAULT", "RETURNING", "RETURN", "CONNECT", "START",
        "MODEL", "PIVOT", "UNPIVOT", "FETCH", "OFFSET", "LOG", "MATCH_RECOGNIZE", "WINDOW", "APPLY", "VERSIONS", "AS", "LOOP", "BULK", "COLLECT",
        "LIMIT", "NOWAIT", "WAIT", "SKIP", "OF", "CASCADE", "PURGE", "REFERENCES", "CONSTRAINT", "ADD", "MODIFY", "DROP", "RENAME", "IN", "SHARE",
        "EXCLUSIVE", "MODE", "ROW", "COLUMN", "ENABLE", "DISABLE", "COMPILE",
    ], StringComparer.Ordinal);

    static readonly HashSet<string> EndsFrom = new(["WHERE", "GROUP", "ORDER", "HAVING", "UNION", "MINUS", "EXCEPT", "INTERSECT", "FOR", "INTO", "SET",
        "VALUES", "ON", "USING", "RETURNING", "RETURN", "CONNECT", "START", "MODEL", "FETCH", "OFFSET", "LOG", "WINDOW", "LOOP", "THEN"], StringComparer.Ordinal);

    sealed record TableRef(string? Schema, string Name, int NameIndex, string? Alias, int AliasIndex, bool InsertTarget = false);

    static List<int> Chain(List<Tok> toks, int i, int end)
    {
        var parts = new List<int> { i };
        while (parts[^1] + 2 < end && toks[parts[^1] + 1].IsP(".") && toks[parts[^1] + 2].IsIdent) parts.Add(parts[^1] + 2);
        return parts;
    }

    static bool Remote(List<Tok> toks, int last, int end) => last + 1 < end && toks[last + 1].Kind == OracleTokenKind.DbLink;

    /// <summary>The table references of one statement (with aliases); every name token that is one goes into <paramref name="tablePositions"/>.</summary>
    static List<TableRef> Tables(List<Tok> toks, int start, int end, HashSet<int> tablePositions)
    {
        var list = new List<TableRef>();
        var inFrom = false;
        var depthAtFrom = 0;
        var depth = 0;
        var createIndex = toks[start].Is("CREATE") && Enumerable.Range(start, Math.Min(4, end - start)).Any(k => toks[k].Is("INDEX"));
        for (var i = start; i < end; i++)
        {
            var t = toks[i];
            if (t.IsP("(")) { depth++; continue; }
            if (t.IsP(")")) { depth--; if (inFrom && depth < depthAtFrom) inFrom = false; continue; }
            if (t.Kind == OracleTokenKind.Word && EndsFrom.Contains(t.Name) && depth <= depthAtFrom) inFrom = false;
            var prev = i > start ? toks[i - 1] : default;
            var introduces =
                t.Is("FROM") || t.Is("JOIN") || t.Is("UPDATE") && !(prev.Is("FOR") || prev.Is("OF") || prev.Is("BEFORE") || prev.Is("AFTER") || prev.Is("OR") || prev.Is("INSTEAD"))
                || t.Is("INTO") && (prev.Is("INSERT") || prev.Is("MERGE") || prev.Is("ALL") || prev.Is("FIRST") || prev.Kind == OracleTokenKind.Punct && prev.Text == ")" && InsertAll(toks, start, i))
                || t.Is("USING") && Enumerable.Range(start, i - start).Any(k => toks[k].Is("MERGE"))
                || t.Is("DELETE") && !(prev.Is("ON") || prev.Is("OR") || prev.Is("BEFORE") || prev.Is("AFTER")) && i + 1 < end && !toks[i + 1].Is("FROM") && toks[i + 1].IsIdent && !toks[i + 1].Is("WHERE")
                || t.Is("TABLE") && i + 1 < end && toks[i + 1].IsIdent && (prev.Is("ALTER") || prev.Is("DROP") || prev.Is("TRUNCATE") || prev.Is("LOCK") || prev.Is("ANALYZE") || prev.Is("FLASHBACK") || prev.Is("ON") && toks[start].Is("COMMENT"))
                || t.Is("REFERENCES")
                || t.Is("ON") && createIndex && depth == 0
                || t.IsP(",") && inFrom && depth == depthAtFrom;
            if (!introduces) continue;
            if (t.Is("FROM") || t.Is("JOIN") || t.Is("UPDATE") || t.Is("USING")) { inFrom = true; depthAtFrom = depth; }
            var j = i + 1;
            while (j < end && (toks[j].Is("ONLY") || toks[j].Is("LATERAL"))) j++;
            if (j < end && toks[j].IsP("(")) continue; // a subquery or a table collection expression
            if (j >= end || !toks[j].IsIdent || (toks[j].Kind == OracleTokenKind.Word && NotAlias.Contains(toks[j].Name))) continue;
            var chain = Chain(toks, j, end);
            if (Remote(toks, chain[^1], end)) continue;
            var after = chain[^1] + 1;
            if (after < end && toks[after].IsP("(") && !t.Is("REFERENCES") && !t.Is("INTO") && !t.Is("ON")) continue; // a table function call
            string? alias = null;
            var aliasAt = -1;
            if (after < end && toks[after].Is("AS") && after + 1 < end && toks[after + 1].IsIdent) { alias = toks[after + 1].Name; aliasAt = after + 1; }
            else if (after < end && toks[after].IsIdent && !(toks[after].Kind == OracleTokenKind.Word && NotAlias.Contains(toks[after].Name))) { alias = toks[after].Name; aliasAt = after; }
            var insert = t.Is("INTO") && !toks[start].Is("MERGE") && !Enumerable.Range(start, i - start).Any(k => toks[k].Is("SELECT") || toks[k].Is("RETURNING") || toks[k].Is("FETCH"));
            list.Add(new TableRef(chain.Count >= 2 ? toks[chain[^2]].Name : null, toks[chain[^1]].Name, chain[^1], alias, aliasAt, insert));
            foreach (var c in chain) tablePositions.Add(c);
        }
        return list;
    }

    static bool InsertAll(List<Tok> toks, int start, int i) => Enumerable.Range(start, i - start).Any(k => toks[k].Is("INSERT"));

    /// <summary>CREATE [OR REPLACE] [EDITIONABLE | NONEDITIONABLE] [[NO] FORCE] VIEW | PROCEDURE | FUNCTION | TRIGGER | PACKAGE [BODY] | TYPE [BODY] | TABLE | SEQUENCE | SYNONYM name.</summary>
    static (int NameIndex, string Kind)? Header(List<Tok> toks, int start, int end)
    {
        if (!toks[start].Is("CREATE")) return null;
        for (var i = start + 1; i < Math.Min(end, start + 10); i++)
        {
            var t = toks[i];
            if (!(t.Is("VIEW") || t.Is("PROCEDURE") || t.Is("FUNCTION") || t.Is("TRIGGER") || t.Is("PACKAGE") || t.Is("TYPE") || t.Is("TABLE") || t.Is("SEQUENCE") || t.Is("SYNONYM"))) continue;
            var j = i + 1;
            if (j < end && toks[j].Is("BODY")) j++;
            return j < end && toks[j].IsIdent ? (Chain(toks, j, end)[^1], t.Name) : null;
        }
        return null;
    }

    static string Resolve(string? schema, RefContext ctx) => schema ?? ctx.DefaultSchema;

    // ---------------------------------------------------------------------------------------------- find

    public RefScan Find(string text, RefTarget target, RefContext ctx)
    {
        var toks = Flatten(text);
        var hits = new Dictionary<int, RefHit>();
        void Hit(Tok t, RefConfidence c, string? reason = null, bool definition = false) => hits.TryAdd(t.Offset, new RefHit(t.Offset, t.Length, c, reason, definition));
        bool IsTarget(string? schema, string name) => name == target.Name && Resolve(schema, ctx) == target.Schema;
        bool IsTable(TableRef r) => IsTarget(r.Schema, r.Name);
        var dynamic = Dynamic(toks);

        if (Trigger(toks) is { } trig) FindInTrigger(toks, trig, target, ctx, Hit);
        var cursorRecords = CursorRecords(toks, target, ctx);

        foreach (var (start, end) in Statements(toks))
        {
            var tablePos = new HashSet<int>();
            var tables = Tables(toks, start, end, tablePos);
            var into = IntoTargets(toks, start, end);
            var skipFrom = -1;
            var skipTo = -1;
            if (Header(toks, start, end) is var (headerAt, headerKind))
            {
                // A unit's own name: the header, and after its last END (END p; must match the unit's name).
                var hc = ChainEndingAt(toks, headerAt);
                if (target.Kind != RefKind.Column && KindMatches(target.Kind, headerKind) && IsTarget(hc.Count >= 2 ? toks[hc[^2]].Name : null, toks[headerAt].Name))
                {
                    Hit(toks[headerAt], RefConfidence.Exact, null, true);
                    var unit = toks[headerAt].Unit;
                    var last = toks.FindLastIndex(t => t.Unit == unit && t.Is("END"));
                    if (last >= 0 && last + 1 < toks.Count && toks[last + 1].Unit == unit && toks[last + 1].IsIdent && toks[last + 1].Name == target.Name) Hit(toks[last + 1], RefConfidence.Exact, null, true);
                }
                // A view's column list names the view's own columns, not those of the tables it reads.
                if (headerKind == "VIEW" && headerAt + 1 < end && toks[headerAt + 1].IsP("("))
                {
                    skipFrom = headerAt + 1;
                    for (int k = skipFrom, depth = 0; k < end; k++)
                    {
                        if (toks[k].IsP("(")) depth++;
                        else if (toks[k].IsP(")") && --depth == 0) { skipTo = k; break; }
                    }
                }
            }
            for (var i = start; i < end; i++)
            {
                var t = toks[i];
                if (i >= skipFrom && i <= skipTo) continue;
                if (!t.IsIdent || (i > start && toks[i - 1].IsP("."))) continue;
                var chain = Chain(toks, i, end);
                var last = toks[chain[^1]];
                var nextAt = chain[^1] + 1;
                var call = nextAt < end && toks[nextAt].IsP("(");
                var remote = Remote(toks, chain[^1], end);
                var pct = nextAt + 1 < end && toks[nextAt].IsP("%") ? toks[nextAt + 1] : default;
                switch (target.Kind)
                {
                    case RefKind.Table or RefKind.View:
                        if (remote) break;
                        if (tablePos.Contains(chain[^1]))
                        {
                            if (IsTarget(chain.Count >= 2 ? toks[chain[^2]].Name : null, last.Name)) Hit(last, RefConfidence.Exact);
                        }
                        else if (pct.Is("ROWTYPE") && IsTarget(chain.Count >= 2 ? toks[chain[^2]].Name : null, last.Name)) Hit(last, RefConfidence.Exact);
                        else if (chain.Count >= 2)
                        {
                            // A table name used as a column qualifier (t.col, s.t.col) or in t.col%TYPE.
                            var tq = toks[chain[^2]];
                            if (tq.Name != target.Name) break;
                            if (chain.Count >= 3 ? toks[chain[^3]].Name == target.Schema : pct.Is("TYPE") ? target.Schema == ctx.DefaultSchema : tables.Any(r => r.Alias == null && IsTable(r)))
                                Hit(tq, RefConfidence.Exact);
                        }
                        break;
                    case RefKind.Sequence:
                        if (chain.Count >= 2 && (last.Is("NEXTVAL") || last.Is("CURRVAL")))
                        {
                            var seq = toks[chain[^2]];
                            if (IsTarget(chain.Count >= 3 ? toks[chain[^3]].Name : null, seq.Name)) Hit(seq, RefConfidence.Exact);
                        }
                        break;
                    case RefKind.Function or RefKind.Procedure:
                    {
                        if (remote || tablePos.Contains(chain[^1]) || last.Name != target.Name || chain.Count > 2) break;
                        var statementCall = StatementStart(toks, chain[0], start) && !(nextAt < end && toks[nextAt].IsP(":="));
                        var bare = target.Kind == RefKind.Function && IsExpression(toks, chain[0], start);
                        if (!call && !statementCall && !bare) break;
                        if (chain.Count == 2 && toks[chain[0]].Name != target.Schema) break; // pkg.member: a package's subprogram
                        if (chain.Count == 1 && (Declared(toks, last.Name) || Locals(toks, last.Unit).Contains(last.Name))) break; // the unit's own symbol
                        if (IsTarget(chain.Count == 2 ? toks[chain[0]].Name : null, last.Name)) Hit(last, RefConfidence.Exact);
                        break;
                    }
                    case RefKind.Type:
                    {
                        if (tablePos.Contains(chain[^1]) || last.Name != target.Name || chain.Count > 2) break;
                        if (chain.Count == 2 && toks[chain[0]].Name != target.Schema) break;
                        if (chain.Count == 2) Hit(last, RefConfidence.Exact);
                        else if (target.Schema == ctx.DefaultSchema) Hit(last, RefConfidence.Possible, "a bare name: the type, or a variable or column with the same name");
                        break;
                    }
                    case RefKind.Column:
                    {
                        if (last.Name != target.Column || call || tablePos.Contains(chain[^1]) || remote) break;
                        if (chain.Count == 1)
                        {
                            if (i > start && toks[i - 1].Is("AS")) break; // an output alias
                            if (into.Contains(i)) break; // SELECT … INTO variable
                            if (InsertColumns(toks, start, i, tables) is { } insertTable)
                            {
                                if (IsTable(insertTable)) Hit(last, RefConfidence.Exact);
                                break;
                            }
                            var owners = tables.Where(r => !r.InsertTarget && (ctx.Columns(Resolve(r.Schema, ctx), r.Name)?.Contains(target.Column!, StringComparer.Ordinal) ?? true)).ToList();
                            if (owners.Count == 0 && tables.Count == 1) owners = tables; // UPDATE t SET col = …
                            if (!owners.Any(IsTable)) break;
                            if (owners.Count == 1) Hit(last, RefConfidence.Exact);
                            else Hit(last, RefConfidence.Possible, $"{owners.Count} tables in scope may have a column {target.Column}");
                        }
                        else if (chain.Count == 2)
                        {
                            var q = toks[chain[0]].Name;
                            var owner = tables.FirstOrDefault(r => r.Alias == q) ?? tables.FirstOrDefault(r => r.Alias == null && r.Name == q);
                            if (owner != null) { if (IsTable(owner)) Hit(last, RefConfidence.Exact); }
                            else if (pct.Is("TYPE") && IsTarget(null, q)) Hit(last, RefConfidence.Exact);
                            else if (cursorRecords.Contains(q)) Hit(last, RefConfidence.Possible, $"a field of the cursor record {q}: its query reads {target.Schema}.{target.Name} (an alias in the query would rename the field)");
                        }
                        else if (toks[chain[^2]].Name == target.Name && toks[chain[^3]].Name == target.Schema) Hit(last, RefConfidence.Exact);
                        break;
                    }
                }
                i = chain[^1];
            }
        }
        return new RefScan(hits.Values.OrderBy(h => h.Offset).ToList(), dynamic);
    }

    /// <summary>The name starts a PL/SQL statement (a call without parentheses, <c>p;</c>).</summary>
    static bool StatementStart(List<Tok> toks, int at, int start) =>
        at == start || toks[at - 1].Is("BEGIN") || toks[at - 1].Is("THEN") || toks[at - 1].Is("ELSE") || toks[at - 1].Is("LOOP");

    /// <summary>A name in an expression (a function called without parentheses: <c>x := f;</c>, <c>SELECT f FROM dual</c>).</summary>
    static bool IsExpression(List<Tok> toks, int at, int start) =>
        at > start && (toks[at - 1].IsP(":=") || toks[at - 1].Is("RETURN") || toks[at - 1].IsP("=") || toks[at - 1].IsP("||") || toks[at - 1].IsP("+") || toks[at - 1].IsP(","));

    static bool KindMatches(RefKind kind, string header) => (kind, header) switch
    {
        (RefKind.View, "VIEW") or (RefKind.Procedure, "PROCEDURE") or (RefKind.Function, "FUNCTION") or (RefKind.Type, "TYPE") or (RefKind.Table, "TABLE") or (RefKind.Sequence, "SEQUENCE") => true,
        _ => false,
    };

    static List<int> ChainEndingAt(List<Tok> toks, int at)
    {
        var first = at;
        while (first - 2 >= 0 && toks[first - 1].IsP(".") && toks[first - 2].IsIdent) first -= 2;
        return Chain(toks, first, toks.Count);
    }

    /// <summary>EXECUTE IMMEDIATE, DBMS_SQL.PARSE, OPEN cursor FOR a string or variable (not a query).</summary>
    static bool Dynamic(List<Tok> toks)
    {
        for (var i = 0; i + 1 < toks.Count; i++)
        {
            if (toks[i].Is("EXECUTE") && toks[i + 1].Is("IMMEDIATE")) return true;
            if (toks[i].Is("DBMS_SQL") && i + 2 < toks.Count && toks[i + 1].IsP(".") && toks[i + 2].Is("PARSE")) return true;
            if (toks[i].Is("OPEN") && i + 3 < toks.Count && toks[i + 2].Is("FOR") && !(toks[i + 3].Is("SELECT") || toks[i + 3].Is("WITH") || toks[i + 3].IsP("("))) return true;
        }
        return false;
    }

    /// <summary>Records of cursor FOR loops whose query reads the target's table: FOR r IN (SELECT … FROM t …) LOOP.</summary>
    static HashSet<string> CursorRecords(List<Tok> toks, RefTarget target, RefContext ctx)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (target.Kind != RefKind.Column) return set;
        for (var i = 0; i + 3 < toks.Count; i++)
        {
            if (!toks[i].Is("FOR") || !toks[i + 1].IsIdent || !toks[i + 2].Is("IN") || !toks[i + 3].IsP("(")) continue;
            var depth = 0;
            var end = i + 3;
            for (; end < toks.Count; end++)
            {
                if (toks[end].IsP("(")) depth++;
                else if (toks[end].IsP(")") && --depth == 0) break;
            }
            var pos = new HashSet<int>();
            var tables = Tables(toks, i + 4, Math.Min(end, toks.Count), pos);
            if (tables.Any(r => r.Name == target.Name && Resolve(r.Schema, ctx) == target.Schema)) set.Add(toks[i + 1].Name);
        }
        return set;
    }

    /// <summary>The variables a SELECT … INTO / FETCH … INTO / RETURNING … INTO writes: never columns.</summary>
    static HashSet<int> IntoTargets(List<Tok> toks, int start, int end)
    {
        var set = new HashSet<int>();
        for (var i = start; i < end; i++)
        {
            if (!toks[i].Is("INTO") || i == start || toks[i - 1].Is("INSERT") || toks[i - 1].Is("MERGE") || toks[i - 1].Is("ALL") || toks[i - 1].Is("FIRST")) continue;
            if (!Enumerable.Range(start, i - start).Any(k => toks[k].Is("SELECT") || toks[k].Is("FETCH") || toks[k].Is("RETURNING") || toks[k].Is("RETURN"))) continue;
            for (var j = i + 1; j < end && !toks[j].Is("FROM") && !toks[j].Is("LIMIT"); j++)
                if (toks[j].IsIdent && !toks[j].Is("BULK") && !toks[j].Is("COLLECT")) set.Add(j);
        }
        return set;
    }

    /// <summary>A name the unit declares itself (a local variable, constant, cursor or nested subprogram): never the schema object.</summary>
    static bool Declared(List<Tok> toks, string name)
    {
        for (var i = 1; i + 1 < toks.Count; i++)
        {
            if (!toks[i].IsIdent || toks[i].Name != name) continue;
            var prev = toks[i - 1];
            if ((prev.Is("PROCEDURE") || prev.Is("FUNCTION") || prev.Is("CURSOR")) && !(i >= 2 && (toks[i - 2].Is("CREATE") || toks[i - 2].Is("REPLACE") || toks[i - 2].Is("EDITIONABLE") || toks[i - 2].Is("NONEDITIONABLE")))) return true;
        }
        return false;
    }

    /// <summary>CREATE … TRIGGER name {BEFORE | AFTER | INSTEAD OF} event [OF cols] ON table: the table's chain and the OF column positions.</summary>
    static (List<int> Table, List<int> OfColumns)? Trigger(List<Tok> toks)
    {
        if (toks.Count < 6 || !toks[0].Is("CREATE")) return null;
        var t = toks.FindIndex(x => x.Is("TRIGGER"));
        if (t < 0 || t > 8) return null;
        var on = toks.FindIndex(t, x => x.Is("ON"));
        if (on < 0 || on + 1 >= toks.Count || !toks[on + 1].IsIdent || toks[on + 1].Is("DATABASE") || toks[on + 1].Is("SCHEMA")) return null;
        var of = new List<int>();
        for (var k = t; k < on; k++)
            if (toks[k].Is("OF") && !toks[k - 1].Is("INSTEAD"))
                for (var j = k + 1; j < on && toks[j].IsIdent && !toks[j].Is("OR"); j += 2)
                {
                    of.Add(j);
                    if (j + 1 >= on || !toks[j + 1].IsP(",")) break;
                }
        return (Chain(toks, on + 1, toks.Count), of);
    }

    static void FindInTrigger(List<Tok> toks, (List<int> Table, List<int> OfColumns) trig, RefTarget target, RefContext ctx, Action<Tok, RefConfidence, string?, bool> hit)
    {
        var table = trig.Table;
        var name = toks[table[^1]].Name;
        if (!(name == target.Name && Resolve(table.Count >= 2 ? toks[table[^2]].Name : null, ctx) == target.Schema)) return;
        if (target.Kind is RefKind.Table or RefKind.View) hit(toks[table[^1]], RefConfidence.Exact, null, false);
        if (target.Kind != RefKind.Column) return;
        foreach (var k in trig.OfColumns) if (toks[k].Name == target.Column) hit(toks[k], RefConfidence.Exact, null, false);
        // :NEW.col / :OLD.col in the body; NEW.col / OLD.col in the WHEN condition.
        for (var k = 0; k + 2 < toks.Count; k++)
        {
            var row = toks[k].IsBind ? toks[k].Text.TrimStart(':').ToUpperInvariant() : toks[k].IsIdent ? toks[k].Name : null;
            if (row is "NEW" or "OLD" && toks[k + 1].IsP(".") && toks[k + 2].IsIdent && toks[k + 2].Name == target.Column && (k == 0 || !toks[k - 1].IsP(".")))
                hit(toks[k + 2], RefConfidence.Exact, null, false);
        }
    }

    static TableRef? InsertColumns(List<Tok> toks, int start, int i, List<TableRef> tables)
    {
        var depth = 0;
        for (var j = i - 1; j >= start; j--)
        {
            if (toks[j].IsP(")")) depth++;
            else if (toks[j].IsP("("))
            {
                if (depth > 0) { depth--; continue; }
                var name = j - 1;
                if (name >= start && toks[name].IsIdent) return tables.FirstOrDefault(r => r.NameIndex == name && r.InsertTarget) ?? tables.FirstOrDefault(r => r.AliasIndex == name && r.InsertTarget);
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
        while (first - 2 >= start && toks[first - 1].IsP(".") && toks[first - 2].IsIdent) first -= 2;
        var chain = Chain(toks, first, end);
        var pos = chain.IndexOf(at);
        if (Remote(toks, chain[^1], end)) return null;
        var nextAt = chain[^1] + 1;
        var call = nextAt < end && toks[nextAt].IsP("(");
        string Schema() => Resolve(chain.Count >= 2 ? toks[chain[^2]].Name : null, ctx);
        if (Header(toks, start, end) is var (header, kind) && chain[^1] == header && pos == chain.Count - 1)
        {
            RefKind? k = kind switch
            {
                "VIEW" => RefKind.View, "PROCEDURE" => RefKind.Procedure, "FUNCTION" => RefKind.Function, "TABLE" => RefKind.Table, "SEQUENCE" => RefKind.Sequence, "TYPE" => RefKind.Type,
                _ => null,
            };
            return k is { } rk ? new RefTarget(rk, Schema(), toks[at].Name) : null;
        }
        if (tablePos.Contains(chain[^1])) return pos == chain.Count - 1 ? new RefTarget(RefKind.Table, Schema(), toks[at].Name) : null;
        if (chain.Count >= 2 && (toks[chain[^1]].Is("NEXTVAL") || toks[chain[^1]].Is("CURRVAL")) && pos == chain.Count - 2)
            return new RefTarget(RefKind.Sequence, Resolve(chain.Count >= 3 ? toks[chain[^3]].Name : null, ctx), toks[at].Name);
        if (call && pos == chain.Count - 1 && chain.Count <= 2 && (chain.Count == 1 || ctx.SchemasOf(toks[at].Name).Contains(toks[chain[0]].Name, StringComparer.Ordinal)))
        {
            var schema = chain.Count == 2 ? toks[chain[0]].Name : ctx.DefaultSchema;
            var statement = chain[0] == start || toks[chain[0] - 1].Is("BEGIN") || toks[chain[0] - 1].Is("THEN") || toks[chain[0] - 1].Is("ELSE") || toks[chain[0] - 1].Is("LOOP");
            return new RefTarget(statement ? RefKind.Procedure : RefKind.Function, schema, toks[at].Name);
        }
        if (pos == chain.Count - 1)
        {
            TableRef? owner = null;
            if (chain.Count == 1)
            {
                var owners = tables.Where(r => ctx.Columns(Resolve(r.Schema, ctx), r.Name)?.Contains(toks[at].Name, StringComparer.Ordinal) ?? false).ToList();
                owner = owners.Count == 1 ? owners[0] : null;
            }
            else if (chain.Count == 2)
            {
                var q = toks[chain[0]].Name;
                owner = tables.FirstOrDefault(r => r.Alias == q) ?? tables.FirstOrDefault(r => r.Alias == null && r.Name == q);
            }
            else owner = new TableRef(toks[chain[^3]].Name, toks[chain[^2]].Name, -1, null, -1);
            if (owner == null) return null;
            return new RefTarget(RefKind.Column, Resolve(owner.Schema, ctx), owner.Name, toks[at].Name);
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

        var alias = tables.FirstOrDefault(r => r.Alias == name);
        if (alias != null && (alias.AliasIndex == at || (at + 1 < end && toks[at + 1].IsP("."))))
            return Spans(new[] { alias.AliasIndex }.Concat(Enumerable.Range(start, end - start).Where(i => toks[i].IsIdent && toks[i].Name == name && i + 1 < end && toks[i + 1].IsP(".") && (i == start || !toks[i - 1].IsP(".")))));

        var ctes = Enumerable.Range(start, end - start)
            .Where(i => toks[i].IsIdent && i + 2 < end && toks[i + 1].Is("AS") && toks[i + 2].IsP("(") && i > start && (toks[i - 1].Is("WITH") || toks[i - 1].IsP(",")))
            .ToList();
        if (ctes.FirstOrDefault(i => toks[i].Name == name) is var def and > 0 && toks[def].Name == name && (def == at || tablePos.Contains(at) || (at + 1 < end && toks[at + 1].IsP("."))))
            return Spans(new[] { def }.Concat(Enumerable.Range(start, end - start).Where(i => toks[i].IsIdent && toks[i].Name == name && (tablePos.Contains(i) || (i + 1 < end && toks[i + 1].IsP(".") && (i == start || !toks[i - 1].IsP(".")))))));

        // A PL/SQL unit's parameter or declared variable: every bare use in its unit.
        var unit = toks[at].Unit;
        if (Locals(toks, unit).Contains(name))
            return Spans(Enumerable.Range(0, toks.Count).Where(i => toks[i].Unit == unit && toks[i].IsIdent && toks[i].Name == name && (i == 0 || !toks[i - 1].IsP(".")) && !(i + 1 < toks.Count && toks[i + 1].IsP("("))));
        return null;
    }

    /// <summary>
    /// The parameters of a unit's header and the names its declaration sections declare (<c>name type;</c> after
    /// IS / AS / DECLARE, before BEGIN) — first-level only, enough for highlighting.
    /// </summary>
    static HashSet<string> Locals(List<Tok> toks, int unit)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var idx = Enumerable.Range(0, toks.Count).Where(i => toks[i].Unit == unit).ToList();
        if (idx.Count == 0) return set;
        var first = idx[0];
        var last = idx[^1] + 1;
        if (Header(toks, first, last) is var (header, kind) && kind is "PROCEDURE" or "FUNCTION" && header + 1 < last && toks[header + 1].IsP("("))
        {
            var depth = 0;
            var expectName = true;
            for (var j = header + 1; j < last; j++)
            {
                if (toks[j].IsP("(")) { depth++; if (depth == 1) continue; }
                if (toks[j].IsP(")")) { depth--; if (depth == 0) break; }
                if (depth == 1 && toks[j].IsP(",")) { expectName = true; continue; }
                if (depth == 1 && expectName && toks[j].IsIdent) { set.Add(toks[j].Name); expectName = false; }
            }
        }
        var declaring = false;
        var segmentStart = true;
        for (var j = first; j < last; j++)
        {
            var t = toks[j];
            if (t.Is("DECLARE") || ((t.Is("IS") || t.Is("AS")) && j > first && (toks[j - 1].IsP(")") || toks[j - 1].IsIdent) && Header(toks, first, last) is not null && j < first + 200))
            {
                declaring = true;
                segmentStart = true;
                continue;
            }
            if (t.Is("BEGIN")) { declaring = false; continue; }
            if (t.IsP(";")) { segmentStart = true; continue; }
            if (declaring && segmentStart && t.IsIdent && !t.Is("PROCEDURE") && !t.Is("FUNCTION") && !t.Is("CURSOR") && !t.Is("TYPE") && !t.Is("PRAGMA") && !t.Is("SUBTYPE"))
                set.Add(t.Name);
            if (declaring && segmentStart && (t.Is("CURSOR") || t.Is("TYPE") || t.Is("SUBTYPE")) && j + 1 < last && toks[j + 1].IsIdent) set.Add(toks[j + 1].Name);
            segmentStart = false;
        }
        return set;
    }

    // ---------------------------------------------------------------------------------------------- renames

    /// <summary>A name as typed in the rename dialog, in the dictionary's spelling: <c>emp2</c> is EMP2, <c>"emp2"</c> is emp2.</summary>
    public string CatalogName(string typed)
    {
        var t = typed.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t.ToUpperInvariant();
    }

    /// <summary>The new name bare where it can be (it was bare and is a plain upper-case name), else quoted.</summary>
    public string Rename(string original, string newName) =>
        !original.StartsWith('"') && OracleIdentifiers.CanBeBare(newName) ? newName : OracleIdentifiers.Quote(newName);

    /// <summary>Oracle stores view and PL/SQL text as written: nothing follows a rename by itself (the objects become invalid).</summary>
    public bool ServerTracked(string definition, ObjectKind kind) => false;
}
