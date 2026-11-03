namespace Querithm.Intelligence.PgSql;

public enum PgTokenKind { Word, QuotedIdent, String, DollarString, Number, Operator, Punct, Comment, Whitespace, Param }

public readonly record struct PgToken(PgTokenKind Kind, int Offset, int Length, int Line, string Text)
{
    public bool IsTrivia => Kind is PgTokenKind.Whitespace or PgTokenKind.Comment;
    public bool Is(string word) => Kind == PgTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
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
            if (char.IsWhiteSpace(c))
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) { if (s[i] == '\n') line++; i++; }
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
            else if (c == '$' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            {
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                kind = PgTokenKind.Param;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
                kind = PgTokenKind.Word;
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.')) i++;
                kind = PgTokenKind.Number;
            }
            else if (c is '(' or ')' or ',' or ';' or '[' or ']')
            {
                i++;
                kind = PgTokenKind.Punct;
            }
            else if (c == ':' && At(s, i + 1) == ':') { i += 2; kind = PgTokenKind.Operator; }
            else if (c == ':' && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '_'))
            {
                i++;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
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
        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
        if (j < s.Length && s[j] == '$' && (j == i + 1 || !char.IsDigit(s[i + 1]))) return s.Substring(i, j - i + 1);
        return null;
    }

    /// <summary>
    /// Statements split on ';' outside parentheses, strings and dollar bodies, and before a statement keyword that starts
    /// a new statement without one (<c>SELECT 1\nSELECT 2</c>, see <see cref="PgSplitter"/>).
    /// </summary>
    public static List<List<PgToken>> Statements(List<PgToken> tokens) => PgSplitter.Split(tokens);
}
