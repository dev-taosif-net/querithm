using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;

namespace Querithm.Providers.Postgres;

/// <summary>
/// Data compare on PostgreSQL: text keys sort under COLLATE "C" (byte order of UTF-8 = code point order),
/// UUIDs by their text so they line up with SQL Server's; a custom query runs in a READ ONLY transaction, so even a
/// data-modifying CTE is refused by the server.
/// </summary>
public sealed class PostgresDataCompare : IDataCompareDialect
{
    public ValueKind KindOf(string? columnType)
    {
        var t = (columnType ?? "").Trim().ToLowerInvariant();
        if (t.EndsWith("[]", StringComparison.Ordinal)) return ValueKind.Unordered;
        return PostgresDataEdit.BaseName(t) switch
        {
            "character" or "char" or "bpchar" => ValueKind.PaddedText,
            "smallint" or "integer" or "bigint" or "int2" or "int4" or "int8" or "oid" => ValueKind.Integer,
            "numeric" or "decimal" => ValueKind.Decimal,
            "real" or "double precision" or "float4" or "float8" => ValueKind.Float,
            "boolean" or "bool" => ValueKind.Bool,
            "uuid" => ValueKind.Guid,
            "date" => ValueKind.Date,
            "timestamp" or "timestamp without time zone" => ValueKind.DateTime,
            "timestamp with time zone" or "timestamptz" => ValueKind.DateTimeOffset,
            "time" or "time without time zone" => ValueKind.Time,
            "bytea" => ValueKind.Binary,
            "json" or "jsonb" or "xml" or "point" or "line" or "lseg" or "box" or "path" or "polygon" or "circle" or "tsvector" or "tsquery" => ValueKind.Unordered,
            _ => ValueKind.Text,
        };
    }

    public string OrderExpression(string columnExpr, ValueKind kind) => kind switch
    {
        ValueKind.Text or ValueKind.PaddedText or ValueKind.Guid => $"({columnExpr})::text COLLATE \"C\"",
        _ => columnExpr,
    };

    public bool PadsTrailingSpaces => false;

    public string? QueryRefusal(string sql)
    {
        var tokens = PgTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        while (tokens.Count > 0 && tokens[^1].Text == ";") tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count == 0) return "The query is empty.";
        if (tokens.Any(t => t.Text == ";")) return "A compared query must be exactly one statement.";
        if (!(tokens[0].Is("SELECT") || tokens[0].Is("WITH") || tokens[0].Is("VALUES") || tokens[0].Is("TABLE") || tokens[0].Text == "("))
            return "A compared query must be a SELECT (or VALUES / TABLE).";
        return null;
    }

    /// <summary>PostgreSQL takes WITH and ORDER BY inside a derived table: only trailing semicolons and comments go.</summary>
    public (string Prefix, string Body) DerivedQuery(string sql)
    {
        var tokens = PgTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        while (tokens.Count > 0 && tokens[^1].Text == ";") tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count == 0) return ("", sql);
        return ("", sql[tokens[0].Offset..(tokens[^1].Offset + tokens[^1].Length)]);
    }


    public string IsolationHint =>
        "Rows that change while the compare reads show up as differences: compare a quiet copy, or read both sides under REPEATABLE READ.";

    public (string On, string Off)? IdentityInsert(string table) => null;
    public string? InsertOverriding => "OVERRIDING SYSTEM VALUE";
    public int InsertBatch => 1000;
    public (string Begin, string Commit) SyncTransaction => ("BEGIN;", "COMMIT;");
}
