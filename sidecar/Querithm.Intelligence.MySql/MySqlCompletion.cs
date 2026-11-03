using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Intelligence.MySql;

/// <summary>
/// Tier-2 completion and signature help for MySQL / MariaDB (Phase D2): PostgreSQL's token-scoped engine on
/// <see cref="MySqlTokenizer"/> tokens and <see cref="MySqlSplitter"/> statements (DELIMITER-aware), under MySQL's rules —
/// names compare case-insensitively and keep their case, backticks quote them (double quotes too under ANSI_QUOTES), the
/// default schema is the database the last USE before the cursor chose (else the session's), calls take no named
/// arguments. Signature help comes from the stored routines in the snapshot.
/// </summary>
public static class MySqlCompletion
{
    static bool Plain(string s) =>
        s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] is '_' or '$') && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$') && !MySqlKeywords.IsReserved(s);

    /// <summary>A quoted name, also while it is still being typed (no closing quote yet).</summary>
    static string Unquote(string t)
    {
        if (t.Length == 0) return t;
        var q = t[0];
        var inner = t.Length >= 2 && t[^1] == q ? t[1..^1] : t[1..];
        return inner.Replace(new string(q, 2), q.ToString());
    }

    static PgToken ToPg(MySqlToken t) => new(t.Kind switch
    {
        MySqlTokenKind.Word => PgTokenKind.Word, MySqlTokenKind.QuotedIdent => PgTokenKind.QuotedIdent, MySqlTokenKind.String => PgTokenKind.String,
        MySqlTokenKind.Number => PgTokenKind.Number, MySqlTokenKind.Operator => PgTokenKind.Operator, MySqlTokenKind.Punct => PgTokenKind.Punct,
        MySqlTokenKind.Comment or MySqlTokenKind.Hint => PgTokenKind.Comment, MySqlTokenKind.Param or MySqlTokenKind.Variable => PgTokenKind.Param,
        _ => PgTokenKind.Whitespace,
    }, t.Offset, t.Length, t.Line, t.Text);

    /// <summary>
    /// The statements the mysql client would send, each ending in a ';' token where its delimiter (';' or the one a
    /// DELIMITER line chose) stood: the engine starts a new statement after it. DELIMITER lines belong to none.
    /// </summary>
    static List<List<PgToken>> Statements(string text, MySqlLexMode mode)
    {
        var result = new List<List<PgToken>>();
        foreach (var st in MySqlSplitter.Split(text, mode))
        {
            var list = st.Tokens.Select(ToPg).ToList();
            var end = st.Tokens.Count > 0 ? st.Tokens[^1].Offset + st.Tokens[^1].Length : st.End;
            if (end < text.Length) list.Add(new PgToken(PgTokenKind.Punct, end, 1, 0, ";"));
            result.Add(list);
        }
        return result;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(MySqlLexMode, string), PgCompletionRules> Cache = new();

    /// <summary>Rules with <paramref name="database"/> as the default schema (one instance each: it caches its snapshot index).</summary>
    public static PgCompletionRules RulesFor(MySqlLexMode mode, string database) => Cache.GetOrAdd((mode, database), k => new PgCompletionRules
    {
        Id = $"mysql:{(k.Item1.AnsiQuotes ? "ansi" : "std")}{(k.Item1.NoBackslashEscapes ? ":nbe" : "")}:{k.Item2}",
        Names = StringComparer.OrdinalIgnoreCase, FoldsUnquoted = false, DefaultSchema = k.Item2,
        IsReserved = MySqlKeywords.IsReserved, IsPlain = Plain, DollarBodiesAreCode = false,
        Tokenize = s => MySqlTokenizer.Tokenize(s, k.Item1).Select(ToPg).ToList(), Statements = (text, _) => Statements(text, k.Item1),
        Unquote = Unquote, Delimit = MySqlTokenizer.Quote, NamedArguments = false,
        // The types CAST(x AS …) and CONVERT(x, …) take.
        BuiltinTypes = ["BINARY", "CHAR", "DATE", "DATETIME", "DECIMAL", "DOUBLE", "FLOAT", "JSON", "NCHAR", "REAL", "SIGNED", "TIME", "UNSIGNED", "YEAR"],
    });

    /// <summary>
    /// The database the last <c>USE db</c> that ends before <paramref name="offset"/> chose, or null; the document's USE
    /// statements are found once per text (<see cref="DocumentScan{T}"/>).
    /// </summary>
    public static string? DatabaseAt(string? docId, string text, int offset, MySqlLexMode mode) =>
        DatabaseAt(DocumentScan<List<(int End, string Database)>>.Get($"mysql|{mode}|{docId}", text, t => UseSwitches(t, mode)), offset);

    static string? DatabaseAt(List<(int End, string Database)> switches, int offset)
    {
        string? database = null;
        foreach (var (end, db) in switches)
        {
            if (end >= offset) break;
            database = db;
        }
        return database;
    }

    /// <summary>Each <c>USE db</c> of the text: where it ends and the database it chose.</summary>
    static List<(int End, string Database)> UseSwitches(string text, MySqlLexMode mode)
    {
        var switches = new List<(int End, string Database)>();
        foreach (var st in MySqlSplitter.Split(text, mode))
        {
            var code = st.Tokens.Where(t => !t.IsTrivia).ToList();
            if (code.Count == 2 && code[0].Is("USE") && code[1].Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent)
                switches.Add((st.End, MySqlTokenizer.Unquote(code[1])));
        }
        return switches;
    }
}
