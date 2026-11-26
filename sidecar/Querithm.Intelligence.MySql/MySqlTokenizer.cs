using Querithm.Intelligence.PgSql;

namespace Querithm.Intelligence.MySql;

public enum MySqlTokenKind { Word, QuotedIdent, String, Number, Operator, Punct, Comment, Hint, Whitespace, Param, Variable }

public readonly record struct MySqlToken(MySqlTokenKind Kind, int Offset, int Length, int Line, string Text)
{
    public bool IsTrivia => Kind is MySqlTokenKind.Whitespace or MySqlTokenKind.Comment or MySqlTokenKind.Hint;
    public bool Is(string word) => Kind == MySqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    public bool IsP(string punct) => Kind is MySqlTokenKind.Punct or MySqlTokenKind.Operator && Text == punct;
}

/// <summary>
/// The SQL modes that change how MySQL reads text (Phase D2), and the server facts the text cannot tell: which
/// version-gated executable comments run. By default every executable comment is code. With <paramref name="CodeUpTo"/>
/// set the reading is a server of that version: MySQL runs <c>/*!NNNNN</c> when NNNNN ≤ it and never MariaDB's
/// <c>/*M!</c>; with <paramref name="MariaDb"/>, MariaDB runs <c>/*M!NNNNNN</c> when ≤ it, <c>/*M!</c> always, and
/// <c>/*!NNNNN</c> below 50700 (MySQL 5.7+ comments are skipped) or a six-digit one ≤ it. A skipped one is a comment that
/// may hold one nested <c>/* … */</c> (the server's consume_comment(lip, 1)).
/// </summary>
public sealed record MySqlLexMode(bool AnsiQuotes = false, bool NoBackslashEscapes = false, int CodeUpTo = int.MaxValue, bool MariaDb = false)
{
    public static readonly MySqlLexMode Default = new();

    /// <summary>Every executable comment is code (no server version assumed).</summary>
    public bool AllCode => CodeUpTo == int.MaxValue && !MariaDb;

    /// <summary>Whether this reading's server runs the executable comment of this kind and version.</summary>
    public bool Runs(bool mariaMarker, int? version)
    {
        if (AllCode) return true;
        if (mariaMarker) return MariaDb && (version is not { } m || m <= CodeUpTo);
        if (version is not { } v) return true;
        return MariaDb ? v < 50700 || (v >= 100000 && v <= CodeUpTo) : v <= CodeUpTo;
    }
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
    /// <summary>What lexing carries from one statement to the next: the executable comments still open.</summary>
    public sealed class LexState
    {
        public int InExecutable;
    }

    /// <param name="state">
    /// Shared by the statements of one split (fourth round, M13): <c>/*!50000 SELECT 1; DROP TABLE t; */</c> is still inside
    /// the executable comment after the ';', so its "*/" closes it instead of becoming a statement of its own.
    /// </param>
    public static IEnumerable<MySqlToken> Lex(string s, int i, int line, MySqlLexMode mode, LexState? state = null)
    {
        var st = state ?? new LexState(); // open executable comments: their "*/" closes the marker, not a comment
        var lexed = 0;
        while (i < s.Length)
        {
            Tier2Cancellation.Check(lexed++);
            var start = i;
            var startLine = line;
            var c = s[i];
            MySqlTokenKind kind;
            // PT7-1: the server's character classes (SqlChars) — ASCII whitespace only, every multibyte character an
            // identifier character (utf8mb4), so "a<U+00A0>WHERE" is one name here as it is to the server.
            if (SqlChars.IsSpace(c))
            {
                while (i < s.Length && SqlChars.IsSpace(s[i])) { if (s[i] == '\n') line++; i++; }
                kind = MySqlTokenKind.Whitespace;
            }
            else if (st.InExecutable > 0 && c == '*' && At(s, i + 1) == '/')
            {
                i += 2;
                st.InExecutable--;
                kind = MySqlTokenKind.Comment; // the executable comment's closing marker
            }
            else if (c == '#' || (c == '-' && At(s, i + 1) == '-' && (i + 2 >= s.Length || DashCommentEnd(s[i + 2]))))
            {
                while (i < s.Length && s[i] != '\n') i++;
                kind = MySqlTokenKind.Comment;
            }
            else if (c == '/' && At(s, i + 1) == '*' && Executable(s, i) is { } marker)
            {
                if (mode.Runs(marker.Maria, marker.Version))
                {
                    // Executable comment: the marker (and version number) is trivia; what follows is code.
                    i = marker.End;
                    st.InExecutable++;
                }
                else
                {
                    // Skipped by this reading's server: a comment, which may hold one nested comment (third round).
                    i = SkippedEnd(s, marker.End, ref line);
                }
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
                while (i < s.Length && (SqlChars.IsIdentPart(s[i]) || s[i] == '.')) i++;
                kind = MySqlTokenKind.Variable;
            }
            else if (c == '@')
            {
                i++;
                // @'x' / @"x" (and 'u'@'h'): the quoted part is read like a string of this reading, backslash escapes included
                // (third round: `SELECT @'\' ' ; DROP TABLE t; -- '` hid a DROP); `x` and ANSI_QUOTES "x" are names.
                if (i < s.Length && s[i] is '`' or '\'' or '"')
                    i = Quoted(s, i, s[i], !mode.NoBackslashEscapes && (s[i] == '\'' || (s[i] == '"' && !mode.AnsiQuotes)), ref line);
                else while (i < s.Length && (SqlChars.IsIdentPart(s[i]) || s[i] == '.')) i++;
                kind = MySqlTokenKind.Variable;
            }
            else if (c == '?')
            {
                i++;
                kind = MySqlTokenKind.Param;
            }
            else if (SqlChars.IsIdentStart(c) || c == '$')
            {
                while (i < s.Length && SqlChars.IsIdentPart(s[i])) i++;
                kind = MySqlTokenKind.Word;
            }
            else if (SqlChars.IsDigit(c) || (c == '.' && i + 1 < s.Length && SqlChars.IsDigit(s[i + 1])))
            {
                while (i < s.Length && (SqlChars.IsNumberPart(s[i]) || s[i] == '$' || ((s[i] is '+' or '-') && s[i - 1] is 'e' or 'E'))) i++;
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
                    if (st.InExecutable > 0 && s[i] == '*' && At(s, i + 1) == '/') break;
                    if (s[i] == '-' && At(s, i + 1) == '-' && (i + 2 >= s.Length || DashCommentEnd(s[i + 2]))) break;
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

    /// <summary>
    /// The executable comment opening at <paramref name="i"/> (<c>/*!</c>, <c>/*!NNNNN</c>, <c>/*M!</c>, <c>/*M!NNNNNN</c>):
    /// whether it is MariaDB's, its version (5 or 6 digits; none for an ungated one) and where its code starts. Null otherwise.
    /// </summary>
    internal static (bool Maria, int? Version, int End)? Executable(string s, int i)
    {
        bool maria;
        int j;
        if (At(s, i + 2) == '!') { maria = false; j = i + 3; }
        else if (At(s, i + 2) == 'M' && At(s, i + 3) == '!') { maria = true; j = i + 4; }
        else return null;
        var from = j;
        var version = 0;
        while (j < s.Length && j - from < 6 && SqlChars.IsDigit(s[j])) version = version * 10 + (s[j++] - '0');
        while (j < s.Length && SqlChars.IsDigit(s[j])) j++; // longer runs: trivia as before
        return (maria, j - from >= 5 ? version : null, j);
    }

    /// <summary>The end of a skipped executable comment from <paramref name="i"/>: its <c>*/</c>, stepping over one nested comment.</summary>
    static int SkippedEnd(string s, int i, ref int line)
    {
        var nested = false;
        while (i < s.Length)
        {
            if (s[i] == '*' && At(s, i + 1) == '/')
            {
                if (!nested) return i + 2;
                nested = false;
                i += 2;
                continue;
            }
            if (!nested && s[i] == '/' && At(s, i + 1) == '*') { nested = true; i += 2; continue; }
            if (s[i] == '\n') line++;
            i++;
        }
        return s.Length;
    }

    /// <summary>
    /// A version-gated executable comment some server skips: <c>/*!NNNNN</c> (a server older than NNNNN, MariaDB for
    /// 50700–99999) and <c>/*M!</c> (MySQL). <c>/*!</c> without a version runs everywhere.
    /// </summary>
    static bool IsGated(string s, int i) => Executable(s, i) is { } e && (e.Maria || e.Version != null);

    /// <summary>The text holds a version-gated executable comment (a character check, so a reading is added at most needlessly).</summary>
    public static bool HasGatedComment(string s)
    {
        for (var i = s.IndexOf("/*", StringComparison.Ordinal); i >= 0; i = s.IndexOf("/*", i + 2, StringComparison.Ordinal))
            if (IsGated(s, i)) return true;
        return false;
    }

    /// <summary>
    /// The version-gated comments whose reading as code or as a comment can change more than their own content (third
    /// round): the comment reading ends somewhere else than the code reading's closing marker (a quote, a nested comment,
    /// a <c>--</c> inside), or the code inside holds a ';', an unbalanced parenthesis or a compound-statement word. Only
    /// these get a reading per server version; the others (mysqldump's) change only their own content, which the
    /// all-comment reading covers. Read in <paramref name="mode"/> with every executable comment as code.
    /// </summary>
    public static List<(bool Maria, int? Version)> SplitChangingGates(string s, MySqlLexMode mode)
    {
        var result = new List<(bool, int?)>();
        var open = new Stack<(int Offset, bool Maria, int? Version, int Parens, bool Risky)>();
        foreach (var t in Lex(s, 0, 1, mode with { CodeUpTo = int.MaxValue, MariaDb = false }))
        {
            if (t.Kind == MySqlTokenKind.Comment && t.Text.StartsWith("/*", StringComparison.Ordinal) && Executable(s, t.Offset) is { } e)
            {
                open.Push((t.Offset, e.Maria, e.Version, 0, false));
                continue;
            }
            if (open.Count == 0) continue;
            var top = open.Pop();
            if (t.Kind == MySqlTokenKind.Comment && t.Text == "*/")
            {
                var line = 0;
                var commentEnd = SkippedEnd(s, Executable(s, top.Offset)!.Value.End, ref line);
                if ((top.Maria || top.Version != null) && (top.Risky || top.Parens != 0 || commentEnd != t.Offset + 2)) result.Add((top.Maria, top.Version));
                if (open.Count > 0) { var outer = open.Pop(); open.Push(outer with { Risky = true }); } // nested executable comments
                continue;
            }
            if (t.IsP("(")) top.Parens++;
            else if (t.IsP(")")) top.Parens--;
            else if (t.IsP(";") || (t.Kind == MySqlTokenKind.Comment && !t.Text.StartsWith("/*!", StringComparison.Ordinal)) || t.Kind == MySqlTokenKind.Hint
                     || (t.Kind == MySqlTokenKind.Word && CompoundWords.Contains(t.Text))) top.Risky = true;
            open.Push(top);
        }
        foreach (var unclosed in open)
            if (unclosed.Maria || unclosed.Version != null) result.Add((unclosed.Maria, unclosed.Version));
        return result;
    }

    /// <summary>
    /// An optimizer hint (<c>/*+ … */</c>) that MySQL's hint scanner ends at another <c>*/</c> (third round): it reads
    /// <c>`…`</c> and <c>"…"</c> (and, to fail closed, <c>'…'</c>) as quoted, so a <c>*/</c> inside them does not end the
    /// hint there — ``SELECT /*+ BKA(`*/ ' `) */ 1; DROP TABLE t; -- '`` hid a DROP behind one SELECT.
    /// </summary>
    public static bool HintEndsElsewhere(string s, MySqlToken hint) =>
        hint.Kind == MySqlTokenKind.Hint && (HintEnd(s, hint.Offset, backslash: false) != hint.Offset + hint.Length || HintEnd(s, hint.Offset, backslash: true) != hint.Offset + hint.Length);

    /// <summary>
    /// Where the hint reader ends a hint opening at <paramref name="at"/>; read both with and without backslash escapes in
    /// its quotes (fourth round, H3: <c>SET_VAR(x='\' */ ')</c>), since it is not known which the server uses there.
    /// </summary>
    static int HintEnd(string s, int at, bool backslash)
    {
        var i = at + 3;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '*' && At(s, i + 1) == '/') return i + 2;
            if (c is '`' or '"' or '\'')
            {
                i++;
                while (i < s.Length)
                {
                    if (backslash && c != '`' && s[i] == '\\') { i += 2; continue; }
                    if (s[i] == c) { if (At(s, i + 1) == c) { i += 2; continue; } break; }
                    i++;
                }
                i++;
                continue;
            }
            i++;
        }
        return s.Length;
    }

    /// <summary>
    /// The versions the text's executable comments name, by a character scan (markers inside strings included, so a gate
    /// another gate's string hides is counted too — fourth round, C1): MySQL's five-digit /*!NNNNN, and MariaDB's /*M!N…
    /// (any length) with six-digit /*!NNNNNN.
    /// </summary>
    public static (List<int> MySql, List<int> MariaDb) GateVersions(string s)
    {
        var mysql = new SortedSet<int>();
        var maria = new SortedSet<int>();
        for (var i = s.IndexOf("/*", StringComparison.Ordinal); i >= 0; i = s.IndexOf("/*", i + 2, StringComparison.Ordinal))
        {
            if (Executable(s, i) is not { Version: { } v } e) continue;
            if (!e.Maria && v < 100000) mysql.Add(v);
            if (e.Maria || v >= 100000) maria.Add(v);
        }
        return ([.. mysql], [.. maria]);
    }

    static readonly HashSet<string> CompoundWords = new(StringComparer.OrdinalIgnoreCase) { "BEGIN", "END", "CASE", "IF", "LOOP", "WHILE", "REPEAT", "FOR", "DELIMITER" };

    /// <summary>
    /// What makes "--" a comment: the server's my_isspace or my_iscntrl on the next byte — ASCII whitespace and ASCII
    /// control characters (a multibyte character's lead byte is neither).
    /// </summary>
    static bool DashCommentEnd(char c) => c < ' ' || c == '\u007F' || SqlChars.IsSpace(c);

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
        var statements = 0;
        var lexState = new MySqlTokenizer.LexState();
        while (pos < text.Length)
        {
            Tier2Cancellation.Check(statements++);
            for (; counted < pos; counted++) if (text[counted] == '\n') line++;
            // A DELIMITER line (only at the start of a line, before any statement text). Code audit 2026-11-25 (H4): the
            // rest of the line was cut out and trimmed for every statement, so 100,000 statements on one line allocated
            // the line 100,000 times; and a DELIMITER after a statement on the same line counted, which the mysql client
            // does not do. Now the text is only compared in place, at a line start.
            if (LineStartsAt(text, pos))
            {
                var lead = pos;
                while (lead < text.Length && text[lead] is ' ' or '\t' or '\r') lead++;
                if (string.Compare(text, lead, "DELIMITER", 0, 9, StringComparison.OrdinalIgnoreCase) == 0 && lead + 9 < text.Length && text[lead + 9] is ' ' or '\t')
                {
                    var lineEnd = text.IndexOf('\n', lead);
                    if (lineEnd < 0) lineEnd = text.Length;
                    var d = text[(lead + 9)..lineEnd].Trim();
                    if (d.Length > 0) delimiter = d;
                    pos = Math.Min(text.Length, lineEnd + 1);
                    continue;
                }
            }
            // Lex from here and stop at the first top-level delimiter (or at a DELIMITER command line): re-tokenizing the rest
            // of the text for every statement made a 10,000-statement script quadratic (it never finished in 10 minutes).
            var taken = new List<MySqlToken>();
            int? end = null, command = null;
            var startedCode = false;
            foreach (var t in MySqlTokenizer.Lex(text, pos, line, mode ?? MySqlLexMode.Default, lexState))
            {
                var abs = t;
                if (!t.IsTrivia && !startedCode)
                {
                    startedCode = true;
                    if (t.Is("DELIMITER") && LineStartsAt(text, abs.Offset)) { command = abs.Offset; break; }
                }
                // The delimiter may end a word ("END$$") or sit inside an operator run; never inside a string or identifier.
                // It may also run on past the token: mysqldump's ";;" is two ';' tokens.
                var at = t.IsTrivia || t.Kind is MySqlTokenKind.String or MySqlTokenKind.QuotedIdent ? -1 : DelimiterAt(text, t, delimiter);
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

    /// <summary>Where in the token the delimiter starts (compared with the text, so it may run past the token), or -1.</summary>
    static int DelimiterAt(string text, MySqlToken t, string delimiter)
    {
        if (delimiter.Length == 1) return t.Text.IndexOf(delimiter[0]);
        for (var k = 0; k < t.Length; k++)
            if (string.CompareOrdinal(text, t.Offset + k, delimiter, 0, delimiter.Length) == 0) return k;
        return -1;
    }

    static bool LineStartsAt(string text, int offset)
    {
        for (var i = offset - 1; i >= 0; i--)
        {
            if (text[i] == '\n') return true;
            if (!SqlChars.IsSpace(text[i])) return false;
        }
        return true;
    }

}
