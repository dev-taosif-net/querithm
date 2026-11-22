using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.Oracle;

/// <summary>
/// The table column behind each result column of an Oracle query, predicted from the statement's tokens and the cached
/// catalog (D4 S10): ODP.NET reports a base table without its schema and the same table for both sides of a self-join, so
/// its schema table is not used. Only the plain shape is predicted — <c>SELECT item, … FROM [schema.]name [alias] [WHERE …]
/// [ORDER BY …] [OFFSET … | FETCH …] [FOR UPDATE …]</c> — where an item is <c>*</c>, <c>alias.*</c>, a column, <c>ROWID</c> or
/// <c>alias.column</c> with an optional alias; any other item is computed (no origin). Anything else (joins, a comma list,
/// DISTINCT, GROUP BY, CONNECT BY, set operators, flashback <c>AS OF</c>, partition extensions, a database link) predicts
/// nothing, and the result stays read-only with the analyzer's reason or <see cref="EditRefusal"/>'s.
/// </summary>
public static class OracleResultSources
{
    /// <summary>The address pseudo-column a result may read from its table (the grid's ROWID fallback key).</summary>
    public const string RowId = "ROWID";

    /// <summary>Words that end the FROM item: what follows filters, orders, limits or locks the rows without changing their source.</summary>
    static readonly HashSet<string> Tail = new(["WHERE", "ORDER", "OFFSET", "FETCH", "FOR"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Words at the statement's own level (outside parentheses) that make the source anything but one plain table.</summary>
    static readonly HashSet<string> NotPlain = new(
        ["JOIN", "CROSS", "NATURAL", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "APPLY", "GROUP", "HAVING", "CONNECT", "START", "MODEL", "PIVOT", "UNPIVOT",
         "UNION", "INTERSECT", "MINUS", "EXCEPT", "MATCH_RECOGNIZE", "SAMPLE", "PARTITION", "SUBPARTITION", "AS", "VERSIONS", "WINDOW"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The statement's code tokens without a trailing <c>;</c>; null when the text holds more than one statement.</summary>
    static List<OracleToken>? Code(string statement)
    {
        var code = OracleTokenizer.Tokenize(statement).Where(t => !t.IsTrivia).ToList();
        while (code.Count > 0 && code[^1].IsP(";")) code.RemoveAt(code.Count - 1);
        return OracleSplitter.Split(statement).Count(st => st.Code.Any()) > 1 ? null : code;
    }

    /// <summary>
    /// One origin per result column, or null when the statement's shape is not predicted, the current schema is unknown
    /// for an unqualified name, or the source is not in the snapshot. A synonym is followed to the object it names; a view
    /// keeps its own id (the analyzer says why it is read-only).
    /// </summary>
    public static ColumnOrigin?[]? Predict(string statement, SchemaSnapshot snapshot, string? currentSchema, IReadOnlyList<string> resultNames) =>
        Trace(statement, snapshot, currentSchema, resultNames).Columns;

    /// <summary>What the user is told when the statement is not the one shape this predictor reads.</summary>
    const string NotPlainShape = "on Oracle only a plain SELECT from one table can be edited (no joins, DISTINCT, GROUP BY, CONNECT BY or subquery in FROM)";

    /// <summary>
    /// <see cref="Predict"/> with the reason when nothing is predicted, and whether newer metadata may change that: the
    /// table is not in the snapshot, or <c>*</c> gave another number of columns than the snapshot lists.
    /// </summary>
    public static TracedOrigins Trace(string statement, SchemaSnapshot snapshot, string? currentSchema, IReadOnlyList<string> resultNames)
    {
        var shape = new TracedOrigins(null, NotPlainShape);
        var code = Code(statement);
        if (code == null || code.Count < 4 || !code[0].Is("SELECT")) return shape;
        var fromAt = TopLevelIndex(code, 1, t => t.Is("FROM"));
        if (fromAt < 2 || code[1].Is("DISTINCT") || code[1].Is("UNIQUE")) return shape;
        var itemsStart = code[1].Is("ALL") ? 2 : 1;
        if (itemsStart >= fromAt) return shape;

        var src = Source(code, fromAt + 1);
        if (src is not { } s) return shape;
        if (s.Schema == null && currentSchema == null)
            return new TracedOrigins(null, $"the session's current schema is not known, so {s.Name} cannot be looked up — write it with its schema (SCHEMA.{s.Name})");
        var shown = $"{s.Schema ?? currentSchema}.{s.Name}";
        var obj = Resolve(snapshot, s.Schema, s.Name, currentSchema, out _);
        // DUAL and the data dictionary are Oracle's own: no snapshot lists them, and no refresh would add them.
        if (obj == null && Dictionary(s.Schema, s.Name)) return new TracedOrigins(null, $"{s.Name} is an Oracle system object, not a table of this schema");
        // Not in the snapshot (created since it was read), or listed without its columns: newer metadata may trace it.
        if (obj == null) return new TracedOrigins(null, $"{shown} is not in this schema's cached metadata", Stale: true);
        if (obj.Columns == null) return new TracedOrigins(null, $"the columns of {obj.Schema}.{obj.Name} are not in the cached metadata", Stale: true);
        var columns = obj.Columns.Where(c => c.Ordinal < InvisibleOrdinal).OrderBy(c => c.Ordinal).ToList();

        var origins = new List<ColumnOrigin?>();
        var star = false;
        foreach (var item in SplitItems(code, itemsStart, fromAt))
        {
            if (item.Count == 1 && item[0].IsP("*"))
            {
                if (fromAt - itemsStart != 1) return shape; // a bare * stands alone in Oracle
                star = true;
                origins.AddRange(columns.Select(c => (ColumnOrigin?)new ColumnOrigin(obj.Id, c.Name)));
                continue;
            }
            if (item.Count >= 3 && item[^1].IsP("*") && item[^2].IsP("."))
            {
                if (!Qualifies(item.GetRange(0, item.Count - 2), s)) return shape;
                star = true;
                origins.AddRange(columns.Select(c => (ColumnOrigin?)new ColumnOrigin(obj.Id, c.Name)));
                continue;
            }
            origins.Add(ItemOrigin(item, s, obj, columns));
        }
        if (origins.Count == resultNames.Count) return new TracedOrigins(origins.ToArray());
        // * gave another number of columns than the snapshot lists: the table changed since it was read.
        return star ? new TracedOrigins(null, $"the columns this query returned are not the ones cached for {obj.Schema}.{obj.Name}", Stale: true) : shape;
    }

    static readonly string[] DictionaryPrefixes = ["USER_", "ALL_", "DBA_", "CDB_", "V$", "GV$", "V_$", "GV_$", "NLS_", "SESSION_"];

    /// <summary>DUAL, a dictionary or dynamic performance view, or anything named in SYS / SYSTEM (identities are upper case).</summary>
    static bool Dictionary(string? schema, string name) =>
        schema is "SYS" or "SYSTEM" || (schema is null or "PUBLIC" && (name == "DUAL" || DictionaryPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))));

    /// <summary>Snapshot columns at or past this ordinal are invisible: never part of <c>*</c> (OracleMetadata.InvisibleOrdinalBase).</summary>
    const int InvisibleOrdinal = 100_000;

    /// <summary>
    /// Why a result of this statement cannot be edited whatever its origins say (without the "Read-only: " prefix), or null:
    /// a set operator (MINUS included), the same table twice, an object over a database link, a synonym naming a remote object.
    /// </summary>
    public static string? EditRefusal(string statement, SchemaSnapshot? snapshot)
    {
        var all = OracleTokenizer.Tokenize(statement).Where(t => !t.IsTrivia).ToList();
        var link = all.FindIndex(t => t.Kind == OracleTokenKind.DbLink);
        if (link >= 0) return $"the query reads a remote object over a database link ({all[link].Text}): Querithm edits local tables only";
        if (all.Any(t => t.Is("MINUS"))) return "the query combines several queries (MINUS)";
        if (ResultShape.EditRefusal(Shape(all)) is { } shape) return shape;
        if (snapshot == null || all.Count < 4 || !all[0].Is("SELECT")) return null;
        var fromAt = TopLevelIndex(all, 1, t => t.Is("FROM"));
        if (fromAt < 0 || Source(all, fromAt + 1) is not { } s) return null;
        // An unqualified name may be a synonym in any listed schema (the current one is not known here) or a PUBLIC one.
        var remote = snapshot.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Synonym && o.Name == s.Name && (s.Schema == null || o.Schema == s.Schema)
                                                          && o.Note?.Contains(RemoteNote, StringComparison.Ordinal) == true);
        return remote != null ? $"{remote.Schema}.{remote.Name} is a synonym for a remote object (a database link): Querithm edits local tables only" : null;
    }

    /// <summary>The explorer note on a synonym that names an object over a database link (OracleMetadata writes it).</summary>
    public const string RemoteNote = "remote (database link)";

    /// <summary>
    /// The object an unqualified or qualified name reaches: the current schema's table, view or synonym, else a PUBLIC
    /// synonym; synonyms followed to their listed target. Null when it is not in the snapshot, or the current schema is
    /// unknown for an unqualified name. <paramref name="viaSynonym"/>: a synonym was followed.
    /// </summary>
    public static SnapObject? Resolve(SchemaSnapshot snapshot, string? schema, string name, string? currentSchema, out bool viaSynonym)
    {
        viaSynonym = false;
        static bool Named(SnapObject o) => o.Kind is ObjectKind.Table or ObjectKind.View or ObjectKind.Synonym;
        SnapObject? obj;
        if (schema != null) obj = snapshot.Objects.FirstOrDefault(o => Named(o) && o.Schema == schema && o.Name == name);
        else
        {
            if (currentSchema == null) return null;
            obj = snapshot.Objects.FirstOrDefault(o => Named(o) && o.Schema == currentSchema && o.Name == name)
                  ?? snapshot.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Synonym && o.Schema == "PUBLIC" && o.Name == name);
        }
        for (var hops = 0; obj is { Kind: ObjectKind.Synonym } && hops < 16; hops++)
        {
            viaSynonym = true;
            var target = obj.TargetObjectId;
            obj = target == null ? null : snapshot.Objects.FirstOrDefault(o => o.Id == target);
        }
        return obj is { Kind: ObjectKind.Table or ObjectKind.View } ? obj : null;
    }

    readonly record struct SourceRef(string? Schema, string Name, string? Alias);

    /// <summary>The FROM item at <paramref name="i"/> when it is one plain [schema.]name [alias] followed only by the tail; else null.</summary>
    static SourceRef? Source(List<OracleToken> code, int i)
    {
        if (i >= code.Count || !IsName(code[i])) return null;
        string? schema = null;
        var name = OracleIdentifiers.Identity(code[i]);
        if (i + 2 < code.Count && code[i + 1].IsP(".") && IsName(code[i + 2]))
        {
            schema = name;
            name = OracleIdentifiers.Identity(code[i + 2]);
            i += 2;
        }
        i++;
        if (i < code.Count && (code[i].IsP(".") || code[i].IsP("(") || code[i].Kind == OracleTokenKind.DbLink)) return null;
        string? alias = null;
        if (i < code.Count && IsName(code[i]) && !(code[i].Kind == OracleTokenKind.Word && (Tail.Contains(code[i].Text) || NotPlain.Contains(code[i].Text))))
            alias = OracleIdentifiers.Identity(code[i++]);
        if (i < code.Count && !(code[i].Kind == OracleTokenKind.Word && Tail.Contains(code[i].Text))) return null;
        // The tail may hold subqueries (WHERE x IN (SELECT …)); at its own level it must not join, group or combine.
        var depth = 0;
        for (var k = i; k < code.Count; k++)
        {
            if (code[k].IsP("(")) depth++;
            else if (code[k].IsP(")")) depth--;
            else if (depth == 0 && (code[k].IsP(",") && !InOrderOrFor(code, i, k) || code[k].Kind == OracleTokenKind.Word && NotPlain.Contains(code[k].Text) && !UsedAsName(code, k)))
                return null;
            else if (code[k].Kind == OracleTokenKind.DbLink) return null;
        }
        return new SourceRef(schema, name, alias);
    }

    /// <summary>Words that are never a column's name in the tail: Oracle reserves them, whatever the keyword list says.</summary>
    static readonly HashSet<string> AlwaysClause = new(["GROUP", "HAVING", "CONNECT", "START", "UNION", "INTERSECT", "MINUS", "AS"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Words that follow an operand in a condition or a sort key, never the keyword that opens a clause.</summary>
    static readonly HashSet<string> AfterAName = new(["IS", "IN", "LIKE", "BETWEEN", "NOT", "AND", "OR", "ASC", "DESC", "NULLS"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A <see cref="NotPlain"/> word in the tail that is a column's name there, not a clause: Oracle does not reserve MODEL,
    /// SAMPLE, PARTITION, WINDOW, FULL, LEFT, RIGHT, VERSIONS…, so <c>WHERE model = 'X'</c> and <c>ORDER BY sample</c> are
    /// plain. It is a name when it is qualified (<c>t.model</c>), ends the statement, or is followed by an operator, a
    /// comma, a closing parenthesis or a word of a condition; a clause keyword is followed by another word or by an
    /// opening parenthesis (MODEL DIMENSION, PARTITION BY, SAMPLE (10), LEFT JOIN, WINDOW w AS), and stays a clause.
    /// </summary>
    static bool UsedAsName(List<OracleToken> code, int k)
    {
        if (AlwaysClause.Contains(code[k].Text) || OracleKeywords.IsReserved(code[k].Text)) return false;
        if (k > 0 && code[k - 1].IsP(".")) return true;
        if (k + 1 >= code.Count) return true;
        var next = code[k + 1];
        return next.Kind switch
        {
            OracleTokenKind.Punct or OracleTokenKind.Operator => !next.IsP("("),
            OracleTokenKind.Word => AfterAName.Contains(next.Text),
            _ => false,
        };
    }

    /// <summary>A top-level comma after ORDER BY or FOR UPDATE OF lists sort keys or columns, not tables.</summary>
    static bool InOrderOrFor(List<OracleToken> code, int from, int at)
    {
        for (var k = at - 1; k >= from; k--)
            if (code[k].Is("ORDER") || code[k].Is("FOR")) return true;
        return false;
    }

    static ColumnOrigin? ItemOrigin(List<OracleToken> item, SourceRef s, SnapObject obj, List<SnapColumn> columns)
    {
        // column | qualifier.column | schema.table.column, then [AS] alias; anything else is an expression.
        var at = 0;
        if (item.Count >= 3 && item[1].IsP(".") && IsName(item[0]) && IsName(item[2]))
        {
            var dots = 1;
            while (2 * dots + 2 < item.Count && item[2 * dots + 1].IsP(".") && IsName(item[2 * dots + 2])) dots++;
            if (!Qualifies(item.GetRange(0, 2 * dots - 1), s)) return null;
            at = 2 * dots;
        }
        if (at >= item.Count || !IsName(item[at])) return null;
        var rest = item.Count - at - 1;
        var plain = rest == 0 || (rest == 1 && IsName(item[at + 1]) && !item[at + 1].Is("AS")) || (rest == 2 && item[at + 1].Is("AS") && IsName(item[at + 2]));
        if (!plain) return null;
        // A function call or another expression would have more tokens; a bare word may still be a pseudo-column (ROWNUM, LEVEL).
        var name = OracleIdentifiers.Identity(item[at]);
        if (item[at].Kind == OracleTokenKind.Word && name == RowId) return obj.Kind == ObjectKind.Table ? new ColumnOrigin(obj.Id, RowId) : null;
        var column = columns.FirstOrDefault(c => c.Name == name);
        return column == null ? null : new ColumnOrigin(obj.Id, column.Name);
    }

    /// <summary>The qualifier tokens (<c>q</c> or <c>schema . q</c>) name the source: its alias when it has one (Oracle hides the table name then), else its name.</summary>
    static bool Qualifies(List<OracleToken> q, SourceRef s)
    {
        var names = q.Where((_, i) => i % 2 == 0).ToList();
        if (q.Where((_, i) => i % 2 == 1).Any(t => !t.IsP(".")) || names.Any(t => !IsName(t))) return false;
        var ids = names.Select(OracleIdentifiers.Identity).ToList();
        if (s.Alias != null) return ids.Count == 1 && ids[0] == s.Alias;
        return ids.Count switch
        {
            1 => ids[0] == s.Name,
            2 => s.Schema != null && ids[0] == s.Schema && ids[1] == s.Name,
            _ => false,
        };
    }

    static bool IsName(OracleToken t) => t.Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent && !(t.Kind == OracleTokenKind.Word && OracleKeywords.IsReserved(t.Text) && !t.Is(RowId));

    /// <summary>The first index from <paramref name="start"/> outside any parentheses where <paramref name="match"/> holds; −1 when none.</summary>
    static int TopLevelIndex(List<OracleToken> code, int start, Func<OracleToken, bool> match)
    {
        var depth = 0;
        for (var i = start; i < code.Count; i++)
        {
            if (code[i].IsP("(")) depth++;
            else if (code[i].IsP(")")) depth--;
            else if (depth == 0 && match(code[i])) return i;
        }
        return -1;
    }

    static IEnumerable<List<OracleToken>> SplitItems(List<OracleToken> code, int start, int end)
    {
        var cur = new List<OracleToken>();
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            var t = code[i];
            if (t.IsP("(")) depth++;
            else if (t.IsP(")")) depth--;
            if (depth == 0 && t.IsP(",")) { yield return cur; cur = []; }
            else cur.Add(t);
        }
        yield return cur;
    }

    /// <summary>Oracle tokens for the shared shape check (<see cref="ResultShape"/>).</summary>
    public static List<ShapeToken> Shape(IEnumerable<OracleToken> tokens) =>
        tokens.Where(t => !t.IsTrivia).Select(t => t.Kind switch
        {
            OracleTokenKind.Word => new ShapeToken(ShapeTokenKind.Word, t.Text),
            OracleTokenKind.QuotedIdent => new ShapeToken(ShapeTokenKind.QuotedName, OracleIdentifiers.Identity(t)),
            OracleTokenKind.Punct or OracleTokenKind.Operator => new ShapeToken(ShapeTokenKind.Punct, t.Text),
            _ => new ShapeToken(ShapeTokenKind.Other, t.Text),
        }).ToList();
}
