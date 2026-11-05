using Querithm.Intelligence.PgSql;

namespace Querithm.Intelligence.DuckDb;

/// <summary>
/// DuckDB tokens (Phase D3): the keyword-free <see cref="PgTokenizer"/> plus DuckDB's lexical differences, each covered by a
/// parity test — <c>$name</c> is one named parameter (PostgreSQL reads <c>$</c> then a word); <c>//</c> (integer division),
/// <c>==</c>, <c>-&gt;</c>, <c>::</c>, struct and list literals, E'' strings, dollar quoting and nested block comments read
/// the same as in PostgreSQL. Never throws.
/// </summary>
public static class DuckDbTokenizer
{
    public static List<PgToken> Tokenize(string s)
    {
        // PT7-1: DuckDB maps some Unicode spaces to ASCII spaces before its (PostgreSQL) scanner runs; every other character
        // from U+0080 up is an identifier character to it, as to PgTokenizer. Tokens keep the text as written.
        var mapped = MapUnicodeSpaces(s);
        var raw = PgTokenizer.Tokenize(mapped);
        var list = new List<PgToken>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var t = raw[i];
            if (!ReferenceEquals(mapped, s)) t = t with { Text = s.Substring(t.Offset, t.Length) };
            if (t is { Kind: PgTokenKind.Operator, Text: "$" } && i + 1 < raw.Count && raw[i + 1] is { Kind: PgTokenKind.Word } w && w.Offset == t.Offset + 1)
            {
                list.Add(new PgToken(PgTokenKind.Param, t.Offset, 1 + w.Length, t.Line, s.Substring(t.Offset, 1 + w.Length)));
                i++;
                continue;
            }
            list.Add(t);
        }
        return list;
    }

    /// <summary>
    /// The text as DuckDB's scanner reads it (PT7-1): Parser::StripUnicodeSpaces replaces U+00A0, U+2000–U+200B, U+202F,
    /// U+205F, U+2060, U+3000 and U+FEFF with an ASCII space before parsing — but only where its own small state machine is
    /// outside '…' / "…" quotes, $tag$ bodies and -- comments (it does not know block comments, so a quote inside one stops
    /// the mapping until the next quote). This is a port of that function (DuckDB 1.5, src/parser/parser.cpp), on the UTF-8
    /// bytes DuckDB is given, so the classifier reads every such character as DuckDB does. Each mapped character is one
    /// UTF-16 unit, so offsets do not move. Returns <paramref name="s"/> itself when nothing is mapped.
    /// </summary>
    public static string MapUnicodeSpaces(string s)
    {
        var any = false;
        foreach (var c in s)
            if (c is '\u00A0' or (>= '\u2000' and <= '\u200B') or '\u202F' or '\u205F' or '\u2060' or '\u3000' or '\uFEFF') { any = true; break; }
        if (!any) return s;
        var q = System.Text.Encoding.UTF8.GetBytes(s);
        var found = StripPositions(q);
        if (found.Count == 0) return s;
        var chars = s.ToCharArray();
        int bytePos = 0, k = 0;
        for (var ci = 0; ci < chars.Length && k < found.Count; ci++)
        {
            var c = s[ci];
            if (bytePos == found[k]) { chars[ci] = ' '; k++; }
            if (c < 0x80) bytePos += 1;
            else if (c < 0x800) bytePos += 2;
            else if (char.IsHighSurrogate(c) && ci + 1 < s.Length && char.IsLowSurrogate(s[ci + 1])) { bytePos += 4; ci++; }
            else bytePos += 3; // BMP, or a lone surrogate (encoded as U+FFFD)
        }
        return new string(chars);
    }

    /// <summary>The byte offsets of the Unicode spaces DuckDB's StripUnicodeSpaces replaces (a line-by-line port).</summary>
    static List<int> StripPositions(byte[] q)
    {
        var found = new List<int>();
        var n = q.Length;
        var pos = 0;
        byte quote = 0;
        int tagStart = 0, tagLen = 0;
    regular:
        for (; pos + 2 < n; pos++)
        {
            var c = q[pos];
            if (c == 0xC2 && q[pos + 1] == 0xA0) found.Add(pos); // U+00A0
            if (c == 0xE2)
            {
                if (q[pos + 1] == 0x80 && (q[pos + 2] is >= 0x80 and <= 0x8B || q[pos + 2] == 0xAF)) found.Add(pos); // U+2000–U+200B, U+202F
                else if (q[pos + 1] == 0x81 && q[pos + 2] is 0x9F or 0xA0) found.Add(pos); // U+205F, U+2060
            }
            else if (c == 0xE3) { if (q[pos + 1] == 0x80 && q[pos + 2] == 0x80) found.Add(pos); } // U+3000
            else if (c == 0xEF) { if (q[pos + 1] == 0xBB && q[pos + 2] == 0xBF) found.Add(pos); } // U+FEFF
            else if (c is (byte)'"' or (byte)'\'')
            {
                quote = c;
                pos++;
                goto inQuotes;
            }
            else if (c == '$' && (q[pos + 1] == '$' || TagFirst(q[pos + 1])))
            {
                tagStart = ++pos;
                for (; pos + 2 < n; pos++)
                {
                    if (q[pos] == '$') { tagLen = pos - tagStart; goto inDollar; }
                    if (!TagFirst(q[pos]) && q[pos] is not (>= (byte)'0' and <= (byte)'9')) goto regular;
                }
                goto end;
            }
            else if (c == '-' && q[pos + 1] == '-') goto inComment;
        }
        goto end;
    inQuotes:
        for (; pos + 1 < n; pos++)
        {
            if (q[pos] != quote) continue;
            if (q[pos + 1] == quote) { pos++; continue; }
            pos++;
            goto regular;
        }
        goto end;
    inDollar:
        for (; pos + 2 < n; pos++)
        {
            // DuckDB reads one past the end there (the C string's NUL), which is never '$'.
            if (q[pos] == '$' && n - (pos + 1) >= tagLen + 1 && pos + tagLen + 1 < n && q[pos + tagLen + 1] == '$'
                && q.AsSpan(pos + 1, tagLen).SequenceEqual(q.AsSpan(tagStart, tagLen)))
            {
                pos += tagLen + 1;
                goto regular;
            }
        }
        goto end;
    inComment:
        for (; pos < n; pos++)
            if (q[pos] is (byte)'\n' or (byte)'\r') goto regular;
    end:
        return found;
    }

    /// <summary>
    /// A dollar tag's first byte: A–Z, a–z, '_' or a byte from 0x80 up. The published source omits '_', but the bundled engine
    /// (DuckDB.NET 1.5.6.1) takes it: measured by a differential run of this port against the engine, 2,696 texts, 0 mismatches.
    /// </summary>
    static bool TagFirst(byte c) => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'_' or >= 0x80;

    /// <summary>
    /// Statements split on ';' outside parentheses, strings and dollar bodies. DuckDB rejects two statements without a ';'
    /// between them (<c>SELECT 1 SELECT 2</c> is a syntax error), so no keyword decides a boundary: such text stays one
    /// statement and the engine reports it.
    /// </summary>
    public static List<List<PgToken>> Split(List<PgToken> tokens)
    {
        var result = new List<List<PgToken>>();
        var cur = new List<PgToken>();
        var depth = 0;
        foreach (var t in tokens)
        {
            if (t is { Kind: PgTokenKind.Punct, Text: "(" }) depth++;
            else if (t is { Kind: PgTokenKind.Punct, Text: ")" }) depth = Math.Max(0, depth - 1);
            cur.Add(t);
            if (t is { Kind: PgTokenKind.Punct, Text: ";" } && depth == 0)
            {
                result.Add(cur);
                cur = [];
            }
        }
        if (cur.Count > 0) result.Add(cur);
        return result;
    }

    /// <summary>Always delimited: DuckDB keeps the case of unquoted names and compares names case-insensitively either way.</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>The name a word or quoted identifier token stands for.</summary>
    public static string Unquote(PgToken t) =>
        t.Kind == PgTokenKind.QuotedIdent && t.Text.Length >= 2 ? t.Text[1..^1].Replace("\"\"", "\"") : t.Text;

    /// <summary>A string literal's value ('' doubled quotes; E'' strings are returned without unescaping).</summary>
    public static string? StringValue(PgToken t) =>
        t.Kind == PgTokenKind.String && t.Text.Length >= 2 && t.Text[0] == '\'' && t.Text[^1] == '\'' ? t.Text[1..^1].Replace("''", "'") : null;
}
