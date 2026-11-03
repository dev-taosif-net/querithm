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
        var raw = PgTokenizer.Tokenize(s);
        var list = new List<PgToken>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var t = raw[i];
            if (t is { Kind: PgTokenKind.Operator, Text: "$" } && i + 1 < raw.Count && raw[i + 1] is { Kind: PgTokenKind.Word } w && w.Offset == t.Offset + 1)
            {
                list.Add(new PgToken(PgTokenKind.Param, t.Offset, 1 + w.Length, t.Line, "$" + w.Text));
                i++;
                continue;
            }
            list.Add(t);
        }
        return list;
    }

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
