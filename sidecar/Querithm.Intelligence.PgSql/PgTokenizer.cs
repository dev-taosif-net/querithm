namespace Querithm.Intelligence.PgSql;

public enum PgTokenKind { Word, QuotedIdent, String, DollarString, Number, Operator, Punct, Comment, Whitespace, Param }

public readonly record struct PgToken(PgTokenKind Kind, int Offset, int Length, int Line, string Text)
{
    public bool IsTrivia => Kind is PgTokenKind.Whitespace or PgTokenKind.Comment;
    public bool Is(string word) => Kind == PgTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The character classes the engines' own lexers use (PT7-1), shared by the PostgreSQL, DuckDB, MySQL and SQLite tokenizers.
/// Whitespace is ASCII only: no engine reads U+00A0, U+3000 or any other Unicode space as a separator (DuckDB maps some to
/// ASCII spaces before it parses, see DuckDbTokenizer). Every character from U+0080 up is an identifier character, as the
/// engines read it: PostgreSQL's scanner (and DuckDB's) takes bytes 0x80–0xFF as ident_start / ident_cont, SQLite's
/// tokenizer does the same, and MySQL's utf8mb4 lexer reads every multibyte character as part of an identifier. Reading
/// such a character as whitespace or an operator lets "a&lt;U+00A0&gt;WHERE" look like an alias and a WHERE to the classifier
/// while the engine sees one alias and no WHERE.
/// </summary>
public static class SqlChars
{
    /// <summary>ASCII whitespace: space, \t, \n, \v, \f, \r (never <see cref="char.IsWhiteSpace(char)"/>).</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    public static bool IsDigit(char c) => c is >= '0' and <= '9';

    public static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>An identifier's first character: an ASCII letter, '_' or any character from U+0080 up.</summary>
    public static bool IsIdentStart(char c) => IsAsciiLetter(c) || c == '_' || c >= '\u0080';

    /// <summary>An identifier's later characters: <see cref="IsIdentStart"/>, an ASCII digit or '$'.</summary>
    public static bool IsIdentPart(char c) => IsIdentStart(c) || IsDigit(c) || c == '$';

    /// <summary>
    /// A character that continues a numeric literal's token: ASCII letters and digits, '_', '.' and any character from
    /// U+0080 up (glued on, as the engines glue them; the engine judges the literal).
    /// </summary>
    public static bool IsNumberPart(char c) => IsAsciiLetter(c) || IsDigit(c) || c is '_' or '.' || c >= '\u0080';

    /// <summary>
    /// PT7-4: the refusal for SQL text holding a NUL (U+0000), for engines that read the text as a C string and stop at the
    /// NUL (SQLite, DuckDB): what follows it would never run, and what the classifier checked is not what runs. Null when
    /// there is none.
    /// </summary>
    public static string? NulRefusal(string text, string engine)
    {
        var at = text.IndexOf('\0');
        if (at < 0) return null;
        var line = 1;
        for (var i = 0; i < at; i++) if (text[i] == '\n') line++;
        return $"The SQL text holds a NUL character (U+0000) on line {line}. {engine} stops reading the text there, so what follows it would not run. Remove the character and run the script again.";
    }
}

/// <summary>Tolerant PostgreSQL tokenizer: never throws; unterminated constructs run to end of text.</summary>
public static class PgTokenizer
{
    public static List<PgToken> Tokenize(string s)
    {
        // About one token per 5 characters (whitespace included): sized once, so a 10k-line body does not regrow the list (profiled).
        var list = new List<PgToken>(Math.Max(16, s.Length / 5));
        var i = 0;
        var line = 1;
        while (i < s.Length)
        {
            var start = i;
            var startLine = line;
            var c = s[i];
            PgTokenKind kind;
            if (SqlChars.IsSpace(c))
            {
                while (i < s.Length && SqlChars.IsSpace(s[i])) { if (s[i] == '\n') line++; i++; }
                kind = PgTokenKind.Whitespace;
            }
            else if (c == '-' && At(s, i + 1) == '-')
            {
                // The server's lexer ends a line comment at CR as well as LF (scan.l: non_newline [^\n\r]); DuckDB's does too.
                while (i < s.Length && s[i] is not ('\n' or '\r')) i++;
                kind = PgTokenKind.Comment;
            }
            else if (c == '/' && At(s, i + 1) == '*')
            {
                // Block comments nest in PostgreSQL.
                var depth = 0;
                do
                {
                    if (s[i] == '/' && At(s, i + 1) == '*') { depth++; i += 2; }
                    else if (s[i] == '*' && At(s, i + 1) == '/') { depth--; i += 2; }
                    else { if (s[i] == '\n') line++; i++; }
                } while (i < s.Length && depth > 0);
                kind = PgTokenKind.Comment;
            }
            else if (c == '\'' || ((c is 'E' or 'e') && At(s, i + 1) == '\''))
            {
                var escapes = c != '\'';
                if (escapes) i++;
                i++;
                while (i < s.Length)
                {
                    if (escapes && s[i] == '\\')
                    {
                        i++;
                        if (i < s.Length) { if (s[i] == '\n') line++; i++; }
                        continue;
                    }
                    if (s[i] == '\'') { if (At(s, i + 1) == '\'') { i += 2; continue; } i++; break; }
                    if (s[i] == '\n') line++;
                    i++;
                }
                kind = PgTokenKind.String;
            }
            else if (c == '"')
            {
                i++;
                while (i < s.Length) { if (s[i] == '"') { if (At(s, i + 1) == '"') { i += 2; continue; } i++; break; } i++; }
                kind = PgTokenKind.QuotedIdent;
            }
            else if (c == '$' && DollarTag(s, i) is { } tag)
            {
                var end = s.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                var stop = end < 0 ? s.Length : end + tag.Length;
                for (var k = i; k < stop; k++) if (s[k] == '\n') line++;
                i = stop;
                kind = PgTokenKind.DollarString;
            }
            else if (c == '$' && i + 1 < s.Length && SqlChars.IsDigit(s[i + 1]))
            {
                i++;
                while (i < s.Length && SqlChars.IsDigit(s[i])) i++;
                kind = PgTokenKind.Param;
            }
            else if (SqlChars.IsIdentStart(c))
            {
                while (i < s.Length && SqlChars.IsIdentPart(s[i])) i++;
                kind = PgTokenKind.Word;
            }
            else if (SqlChars.IsDigit(c) || (c == '.' && i + 1 < s.Length && SqlChars.IsDigit(s[i + 1])))
            {
                while (i < s.Length && SqlChars.IsNumberPart(s[i])) i++;
                kind = PgTokenKind.Number;
            }
            else if (c is '(' or ')' or ',' or ';' or '[' or ']')
            {
                i++;
                kind = PgTokenKind.Punct;
            }
            else if (c == ':' && At(s, i + 1) == ':') { i += 2; kind = PgTokenKind.Operator; }
            else if (c == ':' && i + 1 < s.Length && SqlChars.IsIdentStart(s[i + 1]))
            {
                i++;
                while (i < s.Length && SqlChars.IsIdentPart(s[i]) && s[i] != '$') i++;
                kind = PgTokenKind.Param;
            }
            else
            {
                while (i < s.Length && "+-*/<>=~!@#%^&|`?.:".Contains(s[i]))
                {
                    if (s[i] == '-' && At(s, i + 1) == '-') break;
                    if (s[i] == '/' && At(s, i + 1) == '*') break;
                    i++;
                }
                if (i == start) i++;
                kind = PgTokenKind.Operator;
            }
            list.Add(new PgToken(kind, start, i - start, startLine, s.Substring(start, i - start)));
        }
        return list;
    }

    static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    static string? DollarTag(string s, int i)
    {
        var j = i + 1;
        // scan.l: dolq_start [A-Za-z\200-\377_], dolq_cont [A-Za-z\200-\377_0-9].
        while (j < s.Length && SqlChars.IsIdentPart(s[j]) && s[j] != '$') j++;
        if (j < s.Length && s[j] == '$' && (j == i + 1 || !SqlChars.IsDigit(s[i + 1]))) return s.Substring(i, j - i + 1);
        return null;
    }

    /// <summary>
    /// Statements split on ';' outside parentheses, strings and dollar bodies, and before a statement keyword that starts
    /// a new statement without one (<c>SELECT 1\nSELECT 2</c>, see <see cref="PgSplitter"/>).
    /// </summary>
    public static List<List<PgToken>> Statements(List<PgToken> tokens) => PgSplitter.Split(tokens);
}
