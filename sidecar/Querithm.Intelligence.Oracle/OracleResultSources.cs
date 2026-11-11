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
    public static ColumnOrigin?[]? Predict(string statement, SchemaSnapshot snapshot, string? currentSchema, IReadOnlyList<string> resultNames)
    {
        var code = Code(statement);
        if (code == null || code.Count < 4 || !code[0].Is("SELECT")) return null;
        var fromAt = TopLevelIndex(code, 1, t => t.Is("FROM"));
        if (fromAt < 2 || code[1].Is("DISTINCT") || code[1].Is("UNIQUE")) return null;
        var itemsStart = code[1].Is("ALL") ? 2 : 1;
        if (itemsStart >= fromAt) return null;

        var src = Source(code, fromAt + 1);
        if (src is not { } s) return null;
        var obj = Resolve(snapshot, s.Schema, s.Name, currentSchema, out _);
        if (obj?.Columns == null) return null;
        var columns = obj.Columns.Where(c => c.Ordinal < InvisibleOrdinal).OrderBy(c => c.Ordinal).ToList();

        var origins = new List<ColumnOrigin?>();
        foreach (var item in SplitItems(code, itemsStart, fromAt))
        {
            if (item.Count == 1 && item[0].IsP("*"))
            {
                if (fromAt - itemsStart != 1) return null; // a bare * stands alone in Oracle
                origins.AddRange(columns.Select(c => (ColumnOrigin?)new ColumnOrigin(obj.Id, c.Name)));
                continue;
            }
            if (item.Count >= 3 && item[^1].IsP("*") && item[^2].IsP("."))
            {
                if (!Qualifies(item.GetRange(0, item.Count - 2), s)) return null;
                origins.AddRange(columns.Select(c => (ColumnOrigin?)new ColumnOrigin(obj.Id, c.Name)));
                continue;
            }
            origins.Add(ItemOrigin(item, s, obj, columns));
        }
        return origins.Count == resultNames.Count ? origins.ToArray() : null;
    }

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
            else if (depth == 0 && (code[k].IsP(",") && !InOrderOrFor(code, i, k) || code[k].Kind == OracleTokenKind.Word && NotPlain.Contains(code[k].Text)))
                return null;
            else if (code[k].Kind == OracleTokenKind.DbLink) return null;
        }
        return new SourceRef(schema, name, alias);
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
