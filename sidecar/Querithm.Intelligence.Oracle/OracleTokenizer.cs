namespace Querithm.Intelligence.Oracle;

public enum OracleTokenKind { Word, QuotedIdent, String, Number, Operator, Punct, Comment, Hint, Whitespace, Bind, DbLink }

public readonly record struct OracleToken(OracleTokenKind Kind, int Offset, int Length, int Line, string Text)
{
    public bool IsTrivia => Kind is OracleTokenKind.Whitespace or OracleTokenKind.Comment or OracleTokenKind.Hint;
    public bool Is(string word) => Kind == OracleTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    public bool IsP(string punct) => Kind is OracleTokenKind.Punct or OracleTokenKind.Operator && Text == punct;
    public int End => Offset + Length;
}

/// <summary>
/// Tolerant Oracle SQL / PL/SQL tokenizer (D4 S02): never throws; an unterminated string, quoted name or comment runs to
/// the end of the text. Unquoted names are letters, digits, <c>_</c>, <c>$</c> and <c>#</c> (folded to upper case by
/// <see cref="OracleIdentifiers"/>); <c>"…"</c> names are exact. Strings: <c>'…'</c> with <c>''</c>, the national
/// <c>N'…'</c>, and alternative quoting <c>q'X…X'</c> / <c>nq'X…X'</c>, where X is any character and <c>[ { ( &lt;</c>
/// close with their partner — the only way in which a <c>'</c> may stand alone inside a literal. Comments <c>--</c> and
/// <c>/* */</c> (not nested); <c>/*+ */</c> and <c>--+</c> are optimizer hints. <c>:name</c>, <c>:1</c> and
/// <c>:"name"</c> are binds (<c>:=</c> is an operator); <c>@name</c> right after a name is a database link.
/// </summary>
public static class OracleTokenizer
{
    public static List<OracleToken> Tokenize(string s)
    {
        var list = new List<OracleToken>(Math.Max(16, s.Length / 5));
        list.AddRange(Lex(s, 0, 1));
        return list;
    }

    /// <summary>The tokens from <paramref name="i"/> (on line <paramref name="line"/>) to the end, one at a time.</summary>
    public static IEnumerable<OracleToken> Lex(string s, int i, int line)
    {
        var prevSignificant = OracleTokenKind.Whitespace;
        var prevEnd = -1;
        while (i < s.Length)
        {
            var start = i;
            var startLine = line;
            var c = s[i];
            OracleTokenKind kind;
            if (IsSpace(c))
            {
                while (i < s.Length && IsSpace(s[i])) { if (s[i] == '\n') line++; i++; }
                kind = OracleTokenKind.Whitespace;
            }
            else if (c == '-' && At(s, i + 1) == '-')
            {
                var hint = At(s, i + 2) == '+';
                while (i < s.Length && s[i] != '\n') i++;
                kind = hint ? OracleTokenKind.Hint : OracleTokenKind.Comment;
            }
            else if (c == '/' && At(s, i + 1) == '*')
            {
                var hint = At(s, i + 2) == '+';
                i += 2;
                while (i < s.Length && !(s[i] == '*' && At(s, i + 1) == '/')) { if (s[i] == '\n') line++; i++; }
                i = Math.Min(s.Length, i + 2);
                kind = hint ? OracleTokenKind.Hint : OracleTokenKind.Comment;
            }
            else if (c == '"')
            {
                i = Quoted(s, i, '"', ref line);
                kind = OracleTokenKind.QuotedIdent;
            }
            else if (c == '\'')
            {
                i = Quoted(s, i, '\'', ref line);
                kind = OracleTokenKind.String;
            }
            else if (StringPrefix(s, i) is var prefix and > 0)
            {
                i += prefix;
                i = s[i] == '\'' ? Quoted(s, i, '\'', ref line) : i; // N'…'
                kind = OracleTokenKind.String;
            }
            else if (AlternativePrefix(s, i) is var alt and > 0)
            {
                i = Alternative(s, i + alt, ref line);
                kind = OracleTokenKind.String;
            }
            else if (c == ':' && At(s, i + 1) != '=' && (IsIdentStart(At(s, i + 1)) || IsDigit(At(s, i + 1)) || At(s, i + 1) == '"'))
            {
                i++;
                if (s[i] == '"') i = Quoted(s, i, '"', ref line);
                else while (i < s.Length && (IsIdentPart(s[i]) || IsDigit(s[i]))) i++;
                kind = OracleTokenKind.Bind;
            }
            else if (c == '@' && prevEnd == start && prevSignificant is OracleTokenKind.Word or OracleTokenKind.QuotedIdent
                && (IsIdentStart(At(s, i + 1)) || At(s, i + 1) == '"'))
            {
                // table@remote, "T"@"REMOTE.DB": the link name (dots included) right after an object name.
                i++;
                while (i < s.Length && (IsIdentPart(s[i]) || s[i] is '.' or '"'))
                {
                    if (s[i] == '"') { i = Quoted(s, i, '"', ref line); continue; }
                    i++;
                }
                kind = OracleTokenKind.DbLink;
            }
            else if (IsIdentStart(c))
            {
                while (i < s.Length && IsIdentPart(s[i])) i++;
                kind = OracleTokenKind.Word;
            }
            else if (IsDigit(c) || (c == '.' && IsDigit(At(s, i + 1))))
            {
                i = Number(s, i);
                kind = OracleTokenKind.Number;
            }
            else if (c is '(' or ')' or ',' or ';')
            {
                i++;
                kind = OracleTokenKind.Punct;
            }
            else
            {
                i += Operator(s, i);
                kind = OracleTokenKind.Operator;
            }
            var tok = new OracleToken(kind, start, i - start, startLine, s.Substring(start, i - start));
            if (!tok.IsTrivia) { prevSignificant = kind; }
            prevEnd = i;
            yield return tok;
        }
    }

    /// <summary>The length of an <c>N</c> prefix directly before <c>'</c> (national character literal), else 0.</summary>
    static int StringPrefix(string s, int i) => s[i] is 'n' or 'N' && At(s, i + 1) == '\'' ? 1 : 0;

    /// <summary>The length of a <c>q'</c> / <c>nq'</c> prefix up to the delimiter character (quote included), else 0.</summary>
    static int AlternativePrefix(string s, int i)
    {
        var j = i;
        if (s[j] is 'n' or 'N') j++;
        if (At(s, j) is not ('q' or 'Q') || At(s, j + 1) != '\'') return 0;
        // The delimiter: any character except space, tab and return (Oracle SQL Language Reference, Text literals).
        var d = At(s, j + 2);
        return d is '\0' or ' ' or '\t' or '\r' or '\n' ? 0 : j + 2 - i;
    }

    /// <summary>From the delimiter at <paramref name="i"/>: the text up to the closing delimiter followed by <c>'</c>.</summary>
    static int Alternative(string s, int i, ref int line)
    {
        var open = s[i];
        var close = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
        i++;
        while (i < s.Length)
        {
            if (s[i] == close && At(s, i + 1) == '\'') return i + 2;
            if (s[i] == '\n') line++;
            i++;
        }
        return s.Length;
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
        return s.Length;
    }

    /// <summary>
    /// A numeric literal: digits, one fraction, an exponent and an <c>f</c>/<c>d</c> suffix. Never two dots: <c>1..10</c>
    /// is a PL/SQL range (number, <c>..</c>, number).
    /// </summary>
    static int Number(string s, int i)
    {
        while (i < s.Length && IsDigit(s[i])) i++;
        if (At(s, i) == '.' && At(s, i + 1) != '.') { i++; while (i < s.Length && IsDigit(s[i])) i++; }
        if (At(s, i) is 'e' or 'E' && (IsDigit(At(s, i + 1)) || (At(s, i + 1) is '+' or '-' && IsDigit(At(s, i + 2)))))
        {
            i += 2;
            while (i < s.Length && IsDigit(s[i])) i++;
        }
        if (At(s, i) is 'f' or 'F' or 'd' or 'D' && !IsIdentPart(At(s, i + 1))) i++;
        // Letters glued on (12abc) stay with the number, as the server reads them; it judges the literal.
        while (i < s.Length && IsIdentPart(s[i])) i++;
        return i;
    }

    static readonly string[] TwoCharOperators = ["||", ":=", "=>", "..", "<=", ">=", "<>", "!=", "^=", "~=", "**", "<<", ">>"];

    static int Operator(string s, int i)
    {
        foreach (var op in TwoCharOperators)
            if (string.CompareOrdinal(s, i, op, 0, 2) == 0) return 2;
        return 1;
    }

    static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <summary>ASCII whitespace (never <see cref="char.IsWhiteSpace(char)"/>: the server does not treat U+00A0 as a separator).</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
    static bool IsDigit(char c) => c is >= '0' and <= '9';
    static bool IsIdentStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' || c >= '\u0080';
    static bool IsIdentPart(char c) => IsIdentStart(c) || IsDigit(c) || c is '_' or '$' or '#';

    /// <summary>The value of a string token: prefix and quotes off, <c>''</c> undone (alternative quoting has no escapes).</summary>
    public static string StringValue(OracleToken t)
    {
        var text = t.Text;
        var i = 0;
        if (i < text.Length && text[i] is 'n' or 'N') i++;
        if (i < text.Length && text[i] is 'q' or 'Q' && i + 2 < text.Length && text[i + 1] == '\'')
        {
            var body = text[(i + 3)..];
            return body.Length >= 2 ? body[..^2] : "";
        }
        var inner = text[i..];
        if (inner.Length < 2) return "";
        return inner[1..(inner[^1] == '\'' && inner.Length > 1 ? ^1 : ^0)].Replace("''", "'", StringComparison.Ordinal);
    }
}
