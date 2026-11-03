using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Data compare on SQL Server: text keys sort by code point under a binary collation (whatever the column's
/// collation), GUIDs by their text (SQL Server's own uniqueidentifier order puts the last six bytes first), and a
/// custom query must be a single SELECT on the ScriptDom AST — SQL Server has no read-only transaction to fall back on.
/// </summary>
public sealed class SqlServerDataCompare : IDataCompareDialect
{
    public ValueKind KindOf(string? columnType) => SqlServerDataEdit.TypeSpec.Parse(columnType).Name switch
    {
        "char" or "nchar" => ValueKind.PaddedText,
        "tinyint" or "smallint" or "int" or "bigint" => ValueKind.Integer,
        "decimal" or "numeric" or "money" or "smallmoney" => ValueKind.Decimal,
        "float" or "real" => ValueKind.Float,
        "bit" => ValueKind.Bool,
        "uniqueidentifier" => ValueKind.Guid,
        "date" => ValueKind.Date,
        "datetime" or "datetime2" or "smalldatetime" => ValueKind.DateTime,
        "datetimeoffset" => ValueKind.DateTimeOffset,
        "time" => ValueKind.Time,
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => ValueKind.Binary,
        "xml" or "geography" or "geometry" or "hierarchyid" or "sql_variant" => ValueKind.Unordered,
        _ => ValueKind.Text,
    };

    public string OrderExpression(string columnExpr, ValueKind kind) => kind switch
    {
        ValueKind.Text or ValueKind.PaddedText => $"CONVERT(nvarchar(max), {columnExpr}) COLLATE Latin1_General_100_BIN2",
        ValueKind.Guid => $"CONVERT(char(36), {columnExpr}) COLLATE Latin1_General_100_BIN2",
        _ => columnExpr,
    };

    public bool PadsTrailingSpaces => true;

    public string? QueryRefusal(string sql)
    {
        var script = TSqlParsing.NewParser().Parse(new StringReader(sql), out var errors) as TSqlScript;
        if (errors.Count > 0) return $"The query does not parse: {errors[0].Message} (line {errors[0].Line}).";
        var statements = script?.Batches.SelectMany(b => b.Statements).ToList() ?? [];
        if (statements.Count != 1 || statements[0] is not SelectStatement select)
            return "A compared query must be exactly one SELECT statement.";
        if (select.Into != null) return "A compared query cannot use SELECT … INTO (it would create a table).";
        if (select.QueryExpression is QuerySpecification { } q && q.SelectElements.OfType<SelectSetVariable>().Any())
            return "A compared query cannot assign variables.";
        return null;
    }

    public (string Prefix, string Body) DerivedQuery(string sql)
    {
        var script = TSqlParsing.NewParser().Parse(new StringReader(sql), out var errors) as TSqlScript;
        if (errors.Count > 0 || script?.Batches.SelectMany(b => b.Statements).SingleOrDefault() is not SelectStatement select) return ("", sql);
        string Text(TSqlFragment f) => sql.Substring(f.StartOffset, f.FragmentLength);
        var qe = select.QueryExpression;
        var body = Text(qe);
        // ORDER BY is refused in a derived table unless TOP or OFFSET makes it part of the result (Msg 1033).
        if (qe.OrderByClause is { } order && qe.OffsetClause == null && qe is not QuerySpecification { TopRowFilter: not null })
            body = sql[qe.StartOffset..order.StartOffset] + sql[(order.StartOffset + order.FragmentLength)..(qe.StartOffset + qe.FragmentLength)];
        // A CTE cannot open a derived table (Msg 156): it opens the outer statement instead.
        var prefix = select.WithCtesAndXmlNamespaces is { } with ? Text(with) + "\n" : "";
        return (prefix, body.TrimEnd());
    }


    public string IsolationHint =>
        "Rows that change while the compare reads show up as differences: compare a quiet copy, or read under snapshot isolation where the database allows it.";

    public (string On, string Off)? IdentityInsert(string table) => ($"SET IDENTITY_INSERT {table} ON;", $"SET IDENTITY_INSERT {table} OFF;");
    public string? InsertOverriding => null;
    public int InsertBatch => 1000;
    public (string Begin, string Commit) SyncTransaction => ("SET XACT_ABORT ON;\nBEGIN TRANSACTION;", "COMMIT TRANSACTION;");
}
