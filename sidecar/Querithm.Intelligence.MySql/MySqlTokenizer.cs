namespace Querithm.Intelligence.MySql;

public enum MySqlTokenKind { Word, QuotedIdent, String, Number, Operator, Punct, Comment, Hint, Whitespace, Param, Variable }

public readonly record struct MySqlToken(MySqlTokenKind Kind, int Offset, int Length, int Line, string Text)
{
    public bool IsTrivia => Kind is MySqlTokenKind.Whitespace or MySqlTokenKind.Comment or MySqlTokenKind.Hint;
    public bool Is(string word) => Kind == MySqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    public bool IsP(string punct) => Kind is MySqlTokenKind.Punct or MySqlTokenKind.Operator && Text == punct;
}

/// <summary>The SQL modes that change how MySQL reads text (Phase D2).</summary>
public sealed record MySqlLexMode(bool AnsiQuotes = false, bool NoBackslashEscapes = false)
{
    public static readonly MySqlLexMode Default = new();
}

/// <summary>
/// Tolerant MySQL / MariaDB tokenizer (Phase D2): never throws; unterminated constructs run to the end. Identifiers in
/// backticks (and in "…" under ANSI_QUOTES, else "…" is a string); strings with backslash escapes unless
/// NO_BACKSLASH_ESCAPES; comments "#…", "-- " (the dashes must be followed by whitespace or end) and /* */. Executable
/// comments — /*! … */, /*!80000 … */ and MariaDB's /*M! … */ — are code: their opening and closing markers are trivia and
/// the text between them is tokenized like any other, so the classifier sees inside them. /*+ … */ is an optimizer hint.
/// @x is a user variable, @@x a system variable; ? a parameter.
/// </summary>
public static class MySqlTokenizer
{
    public static List<MySqlToken> Tokenize(string s, MySqlLexMode? mode = null)
    {
        var list = new List<MySqlToken>(Math.Max(16, s.Length / 5));
        list.AddRange(Lex(s, 0, 1, mode ?? MySqlLexMode.Default));
        return list;
    }

    /// <summary>
    /// The tokens from offset <paramref name="i"/> (on line <paramref name="line"/>) to the end, one at a time, so a caller
    /// can stop at what it looks for (<see cref="MySqlSplitter"/> stops at each delimiter).
    /// </summary>
    public static IEnumerable<MySqlToken> Lex(string s, int i, int line, MySqlLexMode mode)
    {
        var inExecutable = 0; // open executable comments: their "*/" closes the marker, not a comment
        while (i < s.Length)
        {
            var start = i;
            var startLine = line;
            var c = s[i];
            MySqlTokenKind kind;
            if (char.IsWhiteSpace(c))
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) { if (s[i] == '\n') line++; i++; }
                kind = MySqlTokenKind.Whitespace;
            }
            else if (inExecutable > 0 && c == '*' && At(s, i + 1) == '/')
            {
                i += 2;
                inExecutable--;
                kind = MySqlTokenKind.Comment; // the executable comment's closing marker
            }
            else if (c == '#' || (c == '-' && At(s, i + 1) == '-' && (i + 2 >= s.Length || char.IsWhiteSpace(s[i + 2]) || char.IsControl(s[i + 2]))))
            {
                while (i < s.Length && s[i] != '\n') i++;
                kind = MySqlTokenKind.Comment;
            }
            else if (c == '/' && At(s, i + 1) == '*' && (At(s, i + 2) == '!' || (At(s, i + 2) == 'M' && At(s, i + 3) == '!')))
            {
                // Executable comment: the marker (and version number) is trivia; what follows is code.
                i += At(s, i + 2) == '!' ? 3 : 4;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                inExecutable++;
                kind = MySqlTokenKind.Comment;
            }
            else if (c == '/' && At(s, i + 1) == '*')
            {
                var hint = At(s, i + 2) == '+';
                i += 2;
                while (i < s.Length && !(s[i] == '*' && At(s, i + 1) == '/')) { if (s[i] == '\n') line++; i++; }
                i = Math.Min(s.Length, i + 2);
                kind = hint ? MySqlTokenKind.Hint : MySqlTokenKind.Comment;
            }
            else if (c == '`' || (c == '"' && mode.AnsiQuotes))
            {
                i = Quoted(s, i, c, false, ref line);
                kind = MySqlTokenKind.QuotedIdent;
            }
            else if (c is '\'' or '"')
            {
                i = Quoted(s, i, c, !mode.NoBackslashEscapes, ref line);
                kind = MySqlTokenKind.String;
            }
            else if (c == '@' && At(s, i + 1) == '@')
            {
                i += 2;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '$')) i++;
                kind = MySqlTokenKind.Variable;
            }
            else if (c == '@')
            {
                i++;
                if (i < s.Length && s[i] is '`' or '\'' or '"') i = Quoted(s, i, s[i], false, ref line);
                else while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '$')) i++;
                kind = MySqlTokenKind.Variable;
            }
            else if (c == '?')
            {
                i++;
                kind = MySqlTokenKind.Param;
            }
            else if (char.IsLetter(c) || c is '_' or '$')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
                kind = MySqlTokenKind.Word;
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.' || ((s[i] is '+' or '-') && s[i - 1] is 'e' or 'E'))) i++;
                kind = MySqlTokenKind.Number;
            }
            else if (c is '(' or ')' or ',' or ';')
            {
                i++;
                kind = MySqlTokenKind.Punct;
            }
            else
            {
                while (i < s.Length && "+-*/<>=~!%^&|.:".Contains(s[i]))
                {
                    if (s[i] == '/' && At(s, i + 1) == '*') break;
                    if (inExecutable > 0 && s[i] == '*' && At(s, i + 1) == '/') break;
                    if (s[i] == '-' && At(s, i + 1) == '-' && (i + 2 >= s.Length || char.IsWhiteSpace(s[i + 2]))) break;
                    i++;
                }
                if (i == start) i++;
                kind = MySqlTokenKind.Operator;
            }
            yield return new MySqlToken(kind, start, i - start, startLine, s.Substring(start, i - start));
        }
    }

    static int Quoted(string s, int i, char q, bool backslash, ref int line)
    {
        i++;
        while (i < s.Length)
        {
            if (backslash && s[i] == '\\') { i += 2; continue; }
            if (s[i] == q) { if (At(s, i + 1) == q) { i += 2; continue; } return i + 1; }
            if (s[i] == '\n') line++;
            i++;
        }
        return Math.Min(i, s.Length);
    }

    static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    public static string Unquote(MySqlToken t) => t.Kind != MySqlTokenKind.QuotedIdent || t.Text.Length < 2 ? t.Text : t.Text[0] switch
    {
        '`' => t.Text[1..^1].Replace("``", "`"),
        '"' => t.Text[1..^1].Replace("\"\"", "\""),
        _ => t.Text,
    };

    /// <summary>Quotes one identifier part with backticks, doubling any backtick (independent of ANSI_QUOTES).</summary>
    public static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";
}

/// <summary>
/// Statement boundaries (Phase D2), as the mysql client draws them: the current delimiter (";" until a
/// <c>DELIMITER x</c> line changes it — a client command, never sent to the server) at the top level, outside strings,
/// identifiers and comments. A stored-program body typed under <c>DELIMITER $$</c> stays one statement.
/// </summary>
public static class MySqlSplitter
{
    /// <summary>One statement: its tokens, and where its text starts and ends (the delimiter excluded).</summary>
    public sealed record Statement(List<MySqlToken> Tokens, int Start, int End);

    public static List<Statement> Split(string text, MySqlLexMode? mode = null)
    {
        var result = new List<Statement>();
        var delimiter = ";";
        var pos = 0;
        int line = 1, counted = 0;
        while (pos < text.Length)
        {
            for (; counted < pos; counted++) if (text[counted] == '\n') line++;
            // A DELIMITER line (only at the start of a line, before any statement text).
            var lineStart = pos;
            var lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0) lineEnd = text.Length;
            var lineText = text[lineStart..lineEnd].Trim();
            if (lineText.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase) || lineText.StartsWith("DELIMITER\t", StringComparison.OrdinalIgnoreCase))
            {
                var d = lineText[10..].Trim();
                if (d.Length > 0) delimiter = d;
                pos = Math.Min(text.Length, lineEnd + 1);
                continue;
            }
            // Lex from here and stop at the first top-level delimiter (or at a DELIMITER command line): re-tokenizing the rest
            // of the text for every statement made a 10,000-statement script quadratic (it never finished in 10 minutes).
            var taken = new List<MySqlToken>();
            int? end = null, command = null;
            var startedCode = false;
            foreach (var t in MySqlTokenizer.Lex(text, pos, line, mode ?? MySqlLexMode.Default))
            {
                var abs = t;
                if (!t.IsTrivia && !startedCode)
                {
                    startedCode = true;
                    if (t.Is("DELIMITER") && LineStartsAt(text, abs.Offset)) { command = abs.Offset; break; }
                }
                // The delimiter may end a word ("END$$") or sit inside an operator run; never inside a string or identifier.
                var at = t.IsTrivia || t.Kind is MySqlTokenKind.String or MySqlTokenKind.QuotedIdent ? -1 : t.Text.IndexOf(delimiter, StringComparison.Ordinal);
                if (at >= 0)
                {
                    if (at > 0) taken.Add(abs with { Length = at, Text = t.Text[..at] });
                    end = abs.Offset + at;
                    break;
                }
                taken.Add(abs);
            }
            if (command is { } c)
            {
                // A client command: change the delimiter, never send it to the server.
                var nl = text.IndexOf('\n', c);
                var d = text[(c + "DELIMITER".Length)..(nl < 0 ? text.Length : nl)].Trim();
                if (d.Length > 0) delimiter = d;
                pos = nl < 0 ? text.Length : nl + 1;
                continue;
            }
            if (!startedCode) break; // only trivia left
            var code = taken.Where(t => !t.IsTrivia).ToList();
            if (code.Count > 0) result.Add(new Statement(taken, code[0].Offset, code[^1].Offset + code[^1].Length));
            pos = end is { } e ? e + delimiter.Length : text.Length;
        }
        return result;
    }

    static bool LineStartsAt(string text, int offset)
    {
        for (var i = offset - 1; i >= 0; i--)
        {
            if (text[i] == '\n') return true;
            if (!char.IsWhiteSpace(text[i])) return false;
        }
        return true;
    }

}
