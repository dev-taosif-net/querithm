namespace Querithm.Intelligence.Sqlite;

public enum SqliteTokenKind { Word, QuotedIdent, String, Blob, Number, Operator, Punct, Comment, Whitespace, Param }

public readonly record struct SqliteToken(SqliteTokenKind Kind, int Offset, int Length, int Line, string Text)
{
    public bool IsTrivia => Kind is SqliteTokenKind.Whitespace or SqliteTokenKind.Comment;
    public bool Is(string word) => Kind == SqliteTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    public bool IsP(string punct) => Kind is SqliteTokenKind.Punct or SqliteTokenKind.Operator && Text == punct;
}

/// <summary>
/// Tolerant SQLite tokenizer (Phase D1): never throws; unterminated constructs run to the end of the text. Identifiers are
/// quoted with "x", [x] or `x` (a doubled quote escapes it); strings are '…' with '' as the escape; blobs X'…'; comments
/// -- and /* */ (block comments do not nest); parameters ?, ?NNN, :name, @name and $name.
/// </summary>
public static class SqliteTokenizer
{
    public static List<SqliteToken> Tokenize(string s)
    {
        var list = new List<SqliteToken>(Math.Max(16, s.Length / 5));
        var i = 0;
        var line = 1;
        while (i < s.Length)
        {
            var start = i;
            var startLine = line;
            var c = s[i];
            SqliteTokenKind kind;
            if (char.IsWhiteSpace(c))
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) { if (s[i] == '\n') line++; i++; }
                kind = SqliteTokenKind.Whitespace;
            }
            else if (c == '-' && At(s, i + 1) == '-')
            {
                while (i < s.Length && s[i] != '\n') i++;
                kind = SqliteTokenKind.Comment;
            }
            else if (c == '/' && At(s, i + 1) == '*')
            {
                i += 2;
                while (i < s.Length && !(s[i] == '*' && At(s, i + 1) == '/')) { if (s[i] == '\n') line++; i++; }
                i = Math.Min(s.Length, i + 2);
                kind = SqliteTokenKind.Comment;
            }
            else if ((c is 'x' or 'X') && At(s, i + 1) == '\'')
            {
                i += 2;
                while (i < s.Length && s[i] != '\'') { if (s[i] == '\n') line++; i++; }
                i = Math.Min(s.Length, i + 1);
                kind = SqliteTokenKind.Blob;
            }
            else if (c == '\'')
            {
                i = Quoted(s, i, '\'', ref line);
                kind = SqliteTokenKind.String;
            }
            else if (c is '"' or '`')
            {
                i = Quoted(s, i, c, ref line);
                kind = SqliteTokenKind.QuotedIdent;
            }
            else if (c == '[')
            {
                i++;
                while (i < s.Length && s[i] != ']') { if (s[i] == '\n') line++; i++; }
                i = Math.Min(s.Length, i + 1);
                kind = SqliteTokenKind.QuotedIdent;
            }
            else if (c == '?')
            {
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                kind = SqliteTokenKind.Param;
            }
            else if (c is ':' or '@' or '$' && i + 1 < s.Length && (char.IsLetterOrDigit(s[i + 1]) || s[i + 1] == '_'))
            {
                i++;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
                kind = SqliteTokenKind.Param;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
                kind = SqliteTokenKind.Word;
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.' || ((s[i] is '+' or '-') && s[i - 1] is 'e' or 'E'))) i++;
                kind = SqliteTokenKind.Number;
            }
            else if (c is '(' or ')' or ',' or ';')
            {
                i++;
                kind = SqliteTokenKind.Punct;
            }
            else
            {
                while (i < s.Length && "+-*/<>=~!%^&|.".Contains(s[i]))
                {
                    if (s[i] == '-' && At(s, i + 1) == '-') break;
                    if (s[i] == '/' && At(s, i + 1) == '*') break;
                    i++;
                }
                if (i == start) i++;
                kind = SqliteTokenKind.Operator;
            }
            list.Add(new SqliteToken(kind, start, i - start, startLine, s.Substring(start, i - start)));
        }
        return list;
    }

    static int Quoted(string s, int i, char q, ref int line)
    {
        i++;
        while (i < s.Length)
        {
            if (s[i] == q) { if (At(s, i + 1) == q) { i += 2; continue; } return i + 1; }
            if (s[i] == '\n') line++;
            i++;
        }
        return i;
    }

    static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>The identifier a token names: quotes removed and doubled quote characters undone; words as written.</summary>
    public static string Unquote(SqliteToken t) => t.Kind != SqliteTokenKind.QuotedIdent || t.Text.Length < 2 ? t.Text : t.Text[0] switch
    {
        '[' => t.Text[1..^1],
        '"' => t.Text[1..^1].Replace("\"\"", "\""),
        '`' => t.Text[1..^1].Replace("``", "`"),
        _ => t.Text,
    };

    /// <summary>
    /// Bare <c>?</c> parameters numbered as SQLite numbers them (one more than the largest number given so far), on tokens:
    /// Microsoft.Data.Sqlite binds by name only, and a bare <c>?</c> has none. Nothing else in the text changes.
    /// </summary>
    public static string NumberAnonymousParameters(string sql)
    {
        var tokens = Tokenize(sql);
        if (!tokens.Any(t => t.Kind == SqliteTokenKind.Param && t.Text == "?")) return sql;
        var sb = new System.Text.StringBuilder(sql.Length + 8);
        var max = 0;
        foreach (var t in tokens)
        {
            if (t.Kind == SqliteTokenKind.Param && t.Text.StartsWith('?'))
            {
                if (t.Text == "?") { max++; sb.Append('?').Append(max.ToString(System.Globalization.CultureInfo.InvariantCulture)); continue; }
                if (int.TryParse(t.Text.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) max = Math.Max(max, n);
            }
            sb.Append(t.Text);
        }
        return sb.ToString();
    }

    /// <summary>Quotes one identifier part for SQL Querithm generates: always "…", doubling any '"'.</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

/// <summary>
/// Statement boundaries (Phase D1): ';' at the top level, except inside a CREATE TRIGGER body (BEGIN … END, whose own
/// statements end with ';'), where CASE … END is counted too.
/// </summary>
public static class SqliteSplitter
{
    public static List<List<SqliteToken>> Split(List<SqliteToken> tokens)
    {
        var statements = new List<List<SqliteToken>>();
        var current = new List<SqliteToken>();
        var code = new List<SqliteToken>(); // non-trivia tokens of the current statement
        var depth = 0; // BEGIN/CASE … END inside a trigger body
        var inTrigger = false;
        foreach (var t in tokens)
        {
            current.Add(t);
            if (t.IsTrivia) continue;
            code.Add(t);
            if (!inTrigger && code.Count <= 4 && t.Is("TRIGGER") && code[0].Is("CREATE")) inTrigger = true;
            if (inTrigger)
            {
                if (t.Is("BEGIN") || (depth > 0 && t.Is("CASE"))) depth++;
                else if (t.Is("END") && depth > 0) depth--;
            }
            if (t.IsP(";") && depth == 0)
            {
                statements.Add(current);
                current = [];
                code.Clear();
                inTrigger = false;
            }
        }
        if (current.Any(t => !t.IsTrivia)) statements.Add(current);
        return statements;
    }
}
