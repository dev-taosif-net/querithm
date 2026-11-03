using Querithm.Contract;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Intelligence.Sqlite;

/// <summary>
/// Tier-2 completion and signature help for SQLite (Phase D1): PostgreSQL's token-scoped engine on
/// <see cref="SqliteTokenizer"/> tokens and <see cref="SqliteSplitter"/> statements, under SQLite's rules — names compare
/// case-insensitively and keep their case, "x" / [x] / `x` quote them, the default schema is main, calls take no named
/// arguments. SQLite stores no routines, so signature help knows the built-in functions of the bundled library instead
/// (each one is checked against <c>pragma_function_list</c> / <c>pragma_module_list</c> in FileEngines/SqliteIntegrationTests).
/// </summary>
public static class SqliteCompletion
{
    /// <summary>SQLite's keywords (sqlite.org/lang_keywords.html): a name that is one gets quotes when inserted.</summary>
    internal static readonly HashSet<string> Keywords = new((
        "ABORT ACTION ADD AFTER ALL ALTER ALWAYS ANALYZE AND AS ASC ATTACH AUTOINCREMENT BEFORE BEGIN BETWEEN BY CASCADE CASE CAST CHECK " +
        "COLLATE COLUMN COMMIT CONFLICT CONSTRAINT CREATE CROSS CURRENT CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP DATABASE DEFAULT " +
        "DEFERRABLE DEFERRED DELETE DESC DETACH DISTINCT DO DROP EACH ELSE END ESCAPE EXCEPT EXCLUDE EXCLUSIVE EXISTS EXPLAIN FAIL FILTER " +
        "FIRST FOLLOWING FOR FOREIGN FROM FULL GENERATED GLOB GROUP GROUPS HAVING IF IGNORE IMMEDIATE IN INDEX INDEXED INITIALLY INNER " +
        "INSERT INSTEAD INTERSECT INTO IS ISNULL JOIN KEY LAST LEFT LIKE LIMIT MATCH MATERIALIZED NATURAL NO NOT NOTHING NOTNULL NULL NULLS " +
        "OF OFFSET ON OR ORDER OTHERS OUTER OVER PARTITION PLAN PRAGMA PRECEDING PRIMARY QUERY RAISE RANGE RECURSIVE REFERENCES REGEXP " +
        "REINDEX RELEASE RENAME REPLACE RESTRICT RETURNING RIGHT ROLLBACK ROW ROWS SAVEPOINT SELECT SET TABLE TEMP TEMPORARY THEN TIES TO " +
        "TRANSACTION TRIGGER UNBOUNDED UNION UNIQUE UPDATE USING VACUUM VALUES VIEW VIRTUAL WHEN WHERE WINDOW WITH WITHOUT").Split(' '),
        StringComparer.OrdinalIgnoreCase);

    static bool Plain(string s) =>
        s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$') && !Keywords.Contains(s);

    /// <summary>A quoted name, also while it is still being typed (no closing quote yet).</summary>
    static string Unquote(string t)
    {
        if (t.Length == 0) return t;
        var close = t[0] == '[' ? ']' : t[0];
        var inner = t.Length >= 2 && t[^1] == close ? t[1..^1] : t[1..];
        return t[0] == '[' ? inner : inner.Replace(new string(close, 2), close.ToString());
    }

    static PgToken ToPg(SqliteToken t) => new(t.Kind switch
    {
        SqliteTokenKind.Word => PgTokenKind.Word, SqliteTokenKind.QuotedIdent => PgTokenKind.QuotedIdent,
        SqliteTokenKind.String or SqliteTokenKind.Blob => PgTokenKind.String, SqliteTokenKind.Number => PgTokenKind.Number,
        SqliteTokenKind.Operator => PgTokenKind.Operator, SqliteTokenKind.Punct => PgTokenKind.Punct, SqliteTokenKind.Comment => PgTokenKind.Comment,
        SqliteTokenKind.Param => PgTokenKind.Param, _ => PgTokenKind.Whitespace,
    }, t.Offset, t.Length, t.Line, t.Text);

    static SqliteToken FromPg(PgToken t) => new(t.Kind switch
    {
        PgTokenKind.Word => SqliteTokenKind.Word, PgTokenKind.QuotedIdent => SqliteTokenKind.QuotedIdent,
        PgTokenKind.String or PgTokenKind.DollarString => SqliteTokenKind.String, PgTokenKind.Number => SqliteTokenKind.Number,
        PgTokenKind.Operator => SqliteTokenKind.Operator, PgTokenKind.Punct => SqliteTokenKind.Punct, PgTokenKind.Comment => SqliteTokenKind.Comment,
        PgTokenKind.Param => SqliteTokenKind.Param, _ => SqliteTokenKind.Whitespace,
    }, t.Offset, t.Length, t.Line, t.Text);

    /// <summary>SQLite's statements (a CREATE TRIGGER body keeps its own ';'), as runs of the document's tokens.</summary>
    static List<List<PgToken>> Statements(List<PgToken> tokens)
    {
        var result = new List<List<PgToken>>();
        var at = 0;
        foreach (var st in SqliteSplitter.Split(tokens.Select(FromPg).ToList()))
        {
            result.Add(tokens.GetRange(at, st.Count));
            at += st.Count;
        }
        if (at < tokens.Count) result.Add(tokens.GetRange(at, tokens.Count - at));
        return result;
    }

    static SnapObject F(string name, string description, params string[] parameters) => new()
    {
        Schema = "", Name = name, Kind = ObjectKind.Func, ReturnType = description,
        Params = parameters.Select((p, i) => new SnapParam { Name = p, Type = "", Ordinal = i + 1 }).ToArray(),
    };

    /// <summary>
    /// The bundled library's built-in functions (core, date and time, aggregate, window, math and JSON), as sqlite.org
    /// documents them: [X] is optional, … repeats.
    /// </summary>
    public static readonly IReadOnlyList<SnapObject> Builtins =
    [
        F("abs", "The absolute value of X.", "X"),
        F("changes", "Rows changed by the most recent INSERT, UPDATE or DELETE."),
        F("char", "The string of the given Unicode code points.", "X1", "X2", "…"),
        F("coalesce", "The first argument that is not NULL.", "X", "Y", "…"),
        F("concat", "The arguments joined as text, NULLs skipped.", "X", "…"),
        F("concat_ws", "The arguments after SEP joined with SEP, NULLs skipped.", "SEP", "X", "…"),
        F("format", "Text formatted printf-style.", "FORMAT", "…"),
        F("glob", "1 when Y matches the GLOB pattern X.", "X", "Y"),
        F("hex", "X as upper-case hexadecimal.", "X"),
        F("ifnull", "X, or Y when X is NULL.", "X", "Y"),
        F("iif", "Y when X is true, else Z.", "X", "Y", "Z"),
        F("instr", "Position of the first Y in X (1-based), 0 when absent.", "X", "Y"),
        F("last_insert_rowid", "The rowid of the last row inserted on this connection."),
        F("length", "Characters in text X, bytes in a blob.", "X"),
        F("like", "1 when Y matches the LIKE pattern X, with escape character Z.", "X", "Y", "[Z]"),
        F("likelihood", "X, with the planner hint that it is true with probability Y.", "X", "Y"),
        F("likely", "X, with the planner hint that it is usually true.", "X"),
        F("lower", "X in lower case (ASCII).", "X"),
        F("ltrim", "X without the characters of Y (spaces by default) at its start.", "X", "[Y]"),
        F("max", "The largest argument; with one argument, the largest value of the group.", "X", "[Y]", "…"),
        F("min", "The smallest argument; with one argument, the smallest value of the group.", "X", "[Y]", "…"),
        F("nullif", "X, or NULL when X = Y.", "X", "Y"),
        F("octet_length", "Bytes in the text or blob X.", "X"),
        F("printf", "Text formatted printf-style (same as format).", "FORMAT", "…"),
        F("quote", "X as an SQL literal.", "X"),
        F("random", "A random 64-bit integer."),
        F("randomblob", "A blob of N random bytes.", "N"),
        F("replace", "X with every Y replaced by Z.", "X", "Y", "Z"),
        F("round", "X rounded to Y digits after the point (0 by default).", "X", "[Y]"),
        F("rtrim", "X without the characters of Y (spaces by default) at its end.", "X", "[Y]"),
        F("sign", "-1, 0 or +1 for the sign of X.", "X"),
        F("sqlite_version", "The SQLite library version."),
        F("sqlite_source_id", "The check-in identifier of the SQLite source."),
        F("substr", "The part of X from character Y (1-based), Z characters long (to the end by default).", "X", "Y", "[Z]"),
        F("substring", "The part of X from character Y (1-based), Z characters long (to the end by default).", "X", "Y", "[Z]"),
        F("total_changes", "Rows changed since the connection opened."),
        F("trim", "X without the characters of Y (spaces by default) at both ends.", "X", "[Y]"),
        F("typeof", "The storage class of X: null, integer, real, text or blob.", "X"),
        F("unhex", "The blob the hexadecimal text X stands for, ignoring the characters of Y.", "X", "[Y]"),
        F("unicode", "The code point of the first character of X.", "X"),
        F("unlikely", "X, with the planner hint that it is usually false.", "X"),
        F("upper", "X in upper case (ASCII).", "X"),
        F("zeroblob", "A blob of N zero bytes.", "N"),
        // Date and time
        F("date", "The date YYYY-MM-DD of the time value after the modifiers.", "time-value", "[modifier]", "…"),
        F("time", "The time HH:MM:SS of the time value after the modifiers.", "time-value", "[modifier]", "…"),
        F("datetime", "YYYY-MM-DD HH:MM:SS of the time value after the modifiers.", "time-value", "[modifier]", "…"),
        F("julianday", "The Julian day number of the time value after the modifiers.", "time-value", "[modifier]", "…"),
        F("unixepoch", "Seconds since 1970-01-01 of the time value after the modifiers.", "time-value", "[modifier]", "…"),
        F("strftime", "The time value after the modifiers, formatted with FORMAT.", "FORMAT", "time-value", "[modifier]", "…"),
        F("timediff", "The time from B to A as +YYYY-MM-DD HH:MM:SS.SSS.", "A", "B"),
        // Aggregate
        F("avg", "The average of the group's non-NULL values.", "X"),
        F("count", "Rows in the group (*), or the group's non-NULL values of X.", "X | *"),
        F("group_concat", "The group's non-NULL values joined with Y (',' by default).", "X", "[Y]"),
        F("string_agg", "The group's non-NULL values joined with Y.", "X", "Y"),
        F("sum", "The sum of the group's non-NULL values (NULL when there are none).", "X"),
        F("total", "The sum of the group's non-NULL values as a real (0.0 when there are none).", "X"),
        // Window
        F("row_number", "The row's number in its partition, from 1."),
        F("rank", "The row's rank in its partition, with gaps."),
        F("dense_rank", "The row's rank in its partition, without gaps."),
        F("percent_rank", "(rank - 1) / (rows in the partition - 1)."),
        F("cume_dist", "Rows up to and including this one's peers / rows in the partition."),
        F("ntile", "The bucket (1 to N) of the row in its partition.", "N"),
        F("lag", "expr of the row OFFSET rows before (1 by default), else DEFAULT.", "expr", "[offset]", "[default]"),
        F("lead", "expr of the row OFFSET rows after (1 by default), else DEFAULT.", "expr", "[offset]", "[default]"),
        F("first_value", "expr of the first row of the window frame.", "expr"),
        F("last_value", "expr of the last row of the window frame.", "expr"),
        F("nth_value", "expr of row N of the window frame.", "expr", "N"),
        // Math
        F("acos", "The arc cosine of X.", "X"),
        F("asin", "The arc sine of X.", "X"),
        F("atan", "The arc tangent of X.", "X"),
        F("atan2", "The arc tangent of Y/X, in the quadrant of (X, Y).", "Y", "X"),
        F("ceil", "The smallest integer not below X.", "X"),
        F("ceiling", "The smallest integer not below X.", "X"),
        F("cos", "The cosine of X (radians).", "X"),
        F("degrees", "X radians in degrees.", "X"),
        F("exp", "e to the power X.", "X"),
        F("floor", "The largest integer not above X.", "X"),
        F("ln", "The natural logarithm of X.", "X"),
        F("log", "The logarithm of X in base B (10 by default).", "[B]", "X"),
        F("log10", "The base-10 logarithm of X.", "X"),
        F("log2", "The base-2 logarithm of X.", "X"),
        F("mod", "The remainder of X divided by Y.", "X", "Y"),
        F("pi", "π."),
        F("pow", "X to the power Y.", "X", "Y"),
        F("power", "X to the power Y.", "X", "Y"),
        F("radians", "X degrees in radians.", "X"),
        F("sin", "The sine of X (radians).", "X"),
        F("sqrt", "The square root of X.", "X"),
        F("tan", "The tangent of X (radians).", "X"),
        F("trunc", "X with its fractional part removed.", "X"),
        // JSON
        F("json", "X as minified JSON text (an error when X is not JSON).", "X"),
        F("json_array", "A JSON array of the arguments.", "value", "…"),
        F("json_array_length", "Elements in the array of X (at path P).", "X", "[P]"),
        F("json_extract", "The value at each path P of X (a JSON array of them for several paths).", "X", "P", "…"),
        F("json_insert", "X with VALUE inserted at each path P that does not exist yet.", "X", "P", "VALUE", "…"),
        F("json_object", "A JSON object of the label / value pairs.", "label", "value", "…"),
        F("json_patch", "X with the RFC 7396 merge patch P applied.", "X", "P"),
        F("json_remove", "X without the elements at the paths.", "X", "P", "…"),
        F("json_replace", "X with VALUE at each path P that exists.", "X", "P", "VALUE", "…"),
        F("json_set", "X with VALUE at each path P (inserted or replaced).", "X", "P", "VALUE", "…"),
        F("json_type", "The JSON type of X (at path P).", "X", "[P]"),
        F("json_valid", "1 when X is well-formed JSON.", "X", "[flags]"),
        F("json_quote", "X as a JSON value.", "X"),
        F("json_group_array", "A JSON array of the group's values.", "value"),
        F("json_group_object", "A JSON object of the group's name / value pairs.", "name", "value"),
        F("json_each", "One row per element of the top-level array or object of X (at path P).", "X", "[P]"),
        F("json_tree", "One row per element of X (at path P), recursively.", "X", "[P]"),
    ];

    public static readonly PgCompletionRules Rules = new()
    {
        Id = "sqlite", Names = StringComparer.OrdinalIgnoreCase, FoldsUnquoted = false, DefaultSchema = "main",
        IsReserved = Keywords.Contains, IsPlain = Plain, DollarBodiesAreCode = false,
        Tokenize = s => SqliteTokenizer.Tokenize(s).Select(ToPg).ToList(), Statements = (_, tokens) => Statements(tokens),
        Unquote = Unquote, Delimit = SqliteTokenizer.Quote, NamedArguments = false, BuiltinRoutines = Builtins,
        BuiltinTypes = ["INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC", "ANY"],
    };
}
