using System.Collections.Frozen;

namespace Querithm.Intelligence.TSql.Completion;

enum TK : byte { Word, Quoted, Variable, String, Number, Punct }

/// <summary>One significant token. <see cref="Kw"/> is the interned upper-case keyword when the word is one we care about.</summary>
readonly record struct Tok(TK Kind, int Start, int Length, string? Kw)
{
    public int End => Start + Length;
}

/// <summary>
/// Hand-written tolerant T-SQL lexer for tier 2. ScriptDom's own token stream costs ~75 ms on a 12k-line proc, too slow
/// for every keystroke; this one is a single pass over the chars (a few ms) and never throws. Only used to find batches,
/// statement spans and the cursor context — the statements themselves are parsed by ScriptDom.
/// </summary>
static class SqlLexer
{
    internal static readonly FrozenSet<string> Keywords = new[]
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WITH", "DECLARE", "SET", "EXEC", "EXECUTE", "IF", "ELSE", "WHILE",
        "BEGIN", "END", "RETURN", "PRINT", "CREATE", "ALTER", "DROP", "TRUNCATE", "OPEN", "FETCH", "CLOSE", "DEALLOCATE",
        "THROW", "RAISERROR", "USE", "BREAK", "CONTINUE", "GOTO", "WAITFOR", "GRANT", "REVOKE", "DENY", "COMMIT", "ROLLBACK",
        "SAVE", "DBCC", "BACKUP", "RESTORE", "CHECKPOINT", "KILL", "BULK", "REVERT", "ENABLE", "DISABLE",
        "UNION", "ALL", "EXCEPT", "INTERSECT", "AS", "ON", "THEN", "WHEN", "CASE", "FOR", "TRAN", "TRANSACTION", "TRY", "CATCH",
        "DISTRIBUTED", "CONVERSATION", "DIALOG", "ROWS", "ROW", "OR", "AND", "NOT", "FROM", "JOIN", "APPLY", "CROSS", "OUTER",
        "INNER", "LEFT", "RIGHT", "FULL", "WHERE", "GROUP", "BY", "HAVING", "ORDER", "INTO", "VALUES", "OUTPUT", "OUT", "USING",
        "TABLE", "CURSOR", "PROC", "PROCEDURE", "FUNCTION", "TRIGGER", "VIEW", "EXTERNAL", "RETURNS", "OPTION", "TOP",
        "DISTINCT", "EXISTS", "IN", "IS", "NULL", "LIKE", "BETWEEN", "NEXT", "PRIOR", "FIRST", "LAST", "ABSOLUTE", "RELATIVE",
        "DEFAULT", "PIVOT", "UNPIVOT", "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT", "OFFSET", "READONLY", "MATCHED", "TARGET",
        "SOURCE", "OVER", "PARTITION", "CHECK", "NOCHECK", "ADD", "COLUMN", "CONSTRAINT", "PRIMARY", "KEY", "FOREIGN",
        "REFERENCES", "UNIQUE", "INDEX", "IDENTITY", "COLLATE", "OF", "AFTER", "INSTEAD", "GO", "WINDOW", "ASC", "DESC",
        "LOCAL", "GLOBAL", "STATIC", "KEYSET", "DYNAMIC", "FAST_FORWARD", "FORWARD_ONLY", "SCROLL", "READ_ONLY",
        "OPENJSON", "SYNONYM", "TYPE", "SCHEMA", "VARYING",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> KwLookup = Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    internal sealed class Result
    {
        public required List<Tok> Tokens { get; init; }
        /// <summary>Spans of comments and strings (start, end) in order, for "cursor is inside a string/comment".</summary>
        public required List<(int Start, int End, bool Unterminated)> Opaque { get; init; }
    }

    static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c == '#';
    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$';

    public static Result Lex(string s)
    {
        var toks = new List<Tok>(Math.Max(16, s.Length / 5));
        var opaque = new List<(int, int, bool)>();
        LexRange(s, 0, toks, opaque, int.MaxValue, null);
        return new Result { Tokens = toks, Opaque = opaque };
    }

    /// <summary>
    /// Re-lexes <paramref name="text"/> reusing the previous version's tokens: everything ending before the first changed
    /// char is kept, and once the new lexer reaches an item start inside the unchanged suffix that was also an item start
    /// in the old text, the old remainder is copied with shifted offsets. Lexing from an item start depends only on the
    /// text that follows, so the result equals a full <see cref="Lex"/>.
    /// </summary>
    public static Result LexIncremental(string oldText, Result old, string text)
    {
        var p = oldText.AsSpan().CommonPrefixLength(text);
        var max = Math.Min(oldText.Length, text.Length) - p;
        var sfx = 0;
        while (sfx < max && oldText[oldText.Length - 1 - sfx] == text[text.Length - 1 - sfx]) sfx++;
        var delta = text.Length - oldText.Length;
        var toks = new List<Tok>(old.Tokens.Count + 16);
        var opaque = new List<(int, int, bool)>(old.Opaque.Count + 4);
        var restart = 0;
        // Tokens are ordered and non-overlapping, so End is monotonic: copy the reusable prefix in one block.
        int lo = 0, hi = old.Tokens.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (old.Tokens[mid].End < p) lo = mid + 1; else hi = mid;
        }
        if (lo > 0)
        {
            toks.AddRange(old.Tokens.GetRange(0, lo));
            restart = old.Tokens[lo - 1].End;
        }
        foreach (var o in old.Opaque)
        {
            if (o.End >= p) break;
            opaque.Add(o);
            restart = Math.Max(restart, o.End);
        }
        var suffixStart = text.Length - sfx;
        var oldTokens = old.Tokens;
        var oldOpaque = old.Opaque;
        bool CanResync(int pos)
        {
            var op = pos - delta;
            return ContainsStart(oldTokens, op) || ContainsOpaqueStart(oldOpaque, op);
        }
        var at = LexRange(text, restart, toks, opaque, suffixStart, CanResync);
        if (at >= 0)
        {
            var op = at - delta;
            var k = FirstStartAtOrAfter(oldTokens, op);
            for (; k < oldTokens.Count; k++) { var t = oldTokens[k]; toks.Add(t with { Start = t.Start + delta }); }
            foreach (var o in oldOpaque) if (o.Start >= op) opaque.Add((o.Start + delta, o.End + delta, o.Unterminated));
        }
        return new Result { Tokens = toks, Opaque = opaque };
    }

    static int FirstStartAtOrAfter(List<Tok> toks, int offset)
    {
        int lo = 0, hi = toks.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (toks[mid].Start < offset) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    static bool ContainsStart(List<Tok> toks, int offset)
    {
        var i = FirstStartAtOrAfter(toks, offset);
        return i < toks.Count && toks[i].Start == offset;
    }

    static bool ContainsOpaqueStart(List<(int Start, int End, bool Unterminated)> list, int offset)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid].Start < offset) lo = mid + 1; else hi = mid;
        }
        return lo < list.Count && list[lo].Start == offset;
    }

    /// <summary>Lexes from <paramref name="i"/>; stops (returning the position) at the first item start at or after
    /// <paramref name="resyncFrom"/> accepted by <paramref name="canResync"/>, else returns -1 at the end.</summary>
    static int LexRange(string s, int i, List<Tok> toks, List<(int, int, bool)> opaque, int resyncFrom, Func<int, bool>? canResync)
    {
        var n = s.Length;
        while (i < n)
        {
            var c = s[i];
            if (c <= ' ' || char.IsWhiteSpace(c)) { i++; continue; }
            if (canResync != null && i >= resyncFrom && canResync(i)) return i;
            var start = i;
            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                while (i < n && s[i] != '\n') i++;
                opaque.Add((start, i, false));
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                var depth = 0;
                var closed = false;
                while (i < n)
                {
                    if (s[i] == '/' && i + 1 < n && s[i + 1] == '*') { depth++; i += 2; continue; }
                    if (s[i] == '*' && i + 1 < n && s[i + 1] == '/')
                    {
                        depth--; i += 2;
                        if (depth == 0) { closed = true; break; }
                        continue;
                    }
                    i++;
                }
                opaque.Add((start, i, !closed));
                continue;
            }
            if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < n && s[i + 1] == '\''))
            {
                if (c != '\'') i++;
                i++;
                var closed = false;
                while (i < n)
                {
                    if (s[i] == '\'')
                    {
                        if (i + 1 < n && s[i + 1] == '\'') { i += 2; continue; }
                        i++; closed = true; break;
                    }
                    i++;
                }
                toks.Add(new Tok(TK.String, start, i - start, null));
                opaque.Add((start, i, !closed));
                continue;
            }
            if (c == '[' || c == '"')
            {
                var close = c == '[' ? ']' : '"';
                i++;
                while (i < n)
                {
                    if (s[i] == close)
                    {
                        if (i + 1 < n && s[i + 1] == close) { i += 2; continue; }
                        i++; break;
                    }
                    // An unterminated identifier stops at the line end so it cannot swallow the rest of the document.
                    if (s[i] == '\n') break;
                    i++;
                }
                toks.Add(new Tok(TK.Quoted, start, i - start, null));
                continue;
            }
            if (c == '@')
            {
                i++;
                while (i < n && IsWordChar(s[i])) i++;
                toks.Add(new Tok(TK.Variable, start, i - start, null));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                if (c == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < n && char.IsAsciiHexDigit(s[i])) i++;
                }
                else
                {
                    while (i < n && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    if (i < n && (s[i] == 'e' || s[i] == 'E'))
                    {
                        i++;
                        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
                        while (i < n && char.IsDigit(s[i])) i++;
                    }
                }
                toks.Add(new Tok(TK.Number, start, i - start, null));
                continue;
            }
            if (IsWordStart(c) || c == '$')
            {
                i++;
                while (i < n && IsWordChar(s[i])) i++;
                var span = s.AsSpan(start, i - start);
                string? kw = null;
                if (span.Length <= 14 && KwLookup.TryGetValue(span, out var k)) kw = k;
                toks.Add(new Tok(TK.Word, start, i - start, kw));
                continue;
            }
            // Punctuation, with the two-char operators kept together.
            if (i + 1 < n)
            {
                var d = s[i + 1];
                if ((d == '=' && "<>!+-*/%&|^".Contains(c)) || (c == '<' && d == '>') || (c == '!' && (d == '<' || d == '>')) || (c == ':' && d == ':'))
                {
                    toks.Add(new Tok(TK.Punct, start, 2, null));
                    i += 2;
                    continue;
                }
            }
            toks.Add(new Tok(TK.Punct, start, 1, null));
            i++;
        }
        return -1;
    }

    public static bool IsPunct(this Tok t, string text, char c) => t.Kind == TK.Punct && t.Length == 1 && text[t.Start] == c;

    /// <summary>Identifier value with brackets/quotes stripped.</summary>
    public static string Ident(string text, Tok t)
    {
        if (t.Kind == TK.Quoted)
        {
            var close = text[t.Start] == '[' ? ']' : '"';
            var inner = text.AsSpan(t.Start + 1, t.Length - 1);
            if (inner.Length > 0 && inner[^1] == close) inner = inner[..^1];
            return inner.ToString().Replace(new string(close, 2), close.ToString());
        }
        return text.Substring(t.Start, t.Length);
    }

    public static bool IsName(this Tok t) => t.Kind is TK.Word or TK.Quoted;
}
