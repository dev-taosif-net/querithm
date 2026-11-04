using Querithm.Contract.Providers;

namespace Querithm.Intelligence.PgSql;

/// <summary>PostgreSQL / DuckDB tokens (both use <see cref="PgToken"/>) for the grid-edit shape check (<see cref="ResultShape"/>).</summary>
public static class PgResultShape
{
    /// <summary>Why a result of this PostgreSQL statement cannot be edited (self-join, set operator), or null.</summary>
    public static string? EditRefusal(string statement) => ResultShape.EditRefusal(Shape(PgTokenizer.Tokenize(statement)));

    public static List<ShapeToken> Shape(IEnumerable<PgToken> tokens) =>
        tokens.Where(t => !t.IsTrivia).Select(t => t.Kind switch
        {
            PgTokenKind.Word => new ShapeToken(ShapeTokenKind.Word, t.Text),
            PgTokenKind.QuotedIdent => new ShapeToken(ShapeTokenKind.QuotedName, Unquote(t.Text)),
            PgTokenKind.Punct or PgTokenKind.Operator => new ShapeToken(ShapeTokenKind.Punct, t.Text),
            _ => new ShapeToken(ShapeTokenKind.Other, t.Text),
        }).ToList();

    /// <summary>"a""b" → a"b (U&amp;"…" escapes are left as written: the name only has to match itself).</summary>
    static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1].Replace("\"\"", "\"") : text;
}
