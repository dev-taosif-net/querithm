using Querithm.Contract.Providers;

namespace Querithm.Intelligence.Sqlite;

/// <summary>SQLite tokens for the grid-edit shape check (<see cref="ResultShape"/>).</summary>
public static class SqliteResultShape
{
    /// <summary>Why a result of this SQLite statement cannot be edited (self-join, compound SELECT), or null.</summary>
    public static string? EditRefusal(string statement) => ResultShape.EditRefusal(Shape(SqliteTokenizer.Tokenize(statement)));

    public static List<ShapeToken> Shape(IEnumerable<SqliteToken> tokens) =>
        tokens.Where(t => !t.IsTrivia).Select(t => t.Kind switch
        {
            SqliteTokenKind.Word => new ShapeToken(ShapeTokenKind.Word, t.Text),
            // [t], "t" and `t` all name t.
            SqliteTokenKind.QuotedIdent => new ShapeToken(ShapeTokenKind.QuotedName, SqliteTokenizer.Unquote(t)),
            SqliteTokenKind.Punct or SqliteTokenKind.Operator => new ShapeToken(ShapeTokenKind.Punct, t.Text),
            _ => new ShapeToken(ShapeTokenKind.Other, t.Text),
        }).ToList();
}
