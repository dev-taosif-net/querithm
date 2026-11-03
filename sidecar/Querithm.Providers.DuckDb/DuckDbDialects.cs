using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Text;
using DuckDB.NET.Data;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;

namespace Querithm.Providers.DuckDb;

/// <summary>DuckDB type text (duckdb_columns().data_type): "DECIMAL(10,2)", "VARCHAR", "INTEGER[]", "STRUCT(a INTEGER)".</summary>
static class DuckDbTypes
{
    /// <summary>The type name without arguments ("DECIMAL(10,2)" → "DECIMAL"); nested and list types keep their shape word.</summary>
    public static string BaseName(string? type)
    {
        var t = (type ?? "").Trim().ToUpperInvariant();
        if (t.EndsWith(']')) return "LIST";
        var cut = t.IndexOf('(');
        return (cut < 0 ? t : t[..cut]).Trim();
    }

    public static (int P, int S)? DecimalArgs(string? type)
    {
        var t = (type ?? "").Trim();
        var open = t.IndexOf('(');
        var close = t.IndexOf(')');
        if (open < 0 || close < open || BaseName(t) is not ("DECIMAL" or "NUMERIC")) return null;
        var args = t[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
        return args.Length == 2 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? (p, s) : null;
    }

    /// <summary>A cast target from the sidecar's own metadata, never from the webview; anything that could end the cast is refused.</summary>
    public static string? Safe(string? type) =>
        string.IsNullOrWhiteSpace(type) || type.Contains(';') || type.Contains("--") || type.Contains("/*") ? null : type;

    public static readonly HashSet<string> Floats = new(["FLOAT", "REAL", "FLOAT4", "DOUBLE", "FLOAT8"], StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> Nested = new(["LIST", "STRUCT", "MAP", "UNION", "ARRAY"], StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Grid edits on DuckDB (Phase D3): values travel as VARCHAR parameters cast to the catalog type in the statement
/// (CAST($p0 AS INTEGER)), so DuckDB's own casts parse them. There is no row version: lost updates are caught by comparing
/// the original values of the changed columns, typed; a mismatch is a conflict and nothing is written. Keys must be a
/// declared PRIMARY KEY (tables without one stay read-only with the analyzer's reason).
/// </summary>
public sealed class DuckDbDataEdit : IDataEditDialect
{
    public string Quote(string identifier) => DuckDbTokenizer.Quote(identifier);
    public string Placeholder(int index) => "$p" + index.ToString(CultureInfo.InvariantCulture);

    public string TypedPlaceholder(int index, string? columnType) =>
        DuckDbTypes.Safe(columnType) is { } t ? $"CAST({Placeholder(index)} AS {t})" : Placeholder(index);

    public void AddValue(DbCommand command, int index, string? columnType, GridValue value) =>
        command.Parameters.Add(new DuckDBParameter("p" + index.ToString(CultureInfo.InvariantCulture), value.IsNull || value.Text == null ? DBNull.Value : ToInput(columnType, value.Text)));

    /// <summary>The grid's text → DuckDB's cast input where the two differ (BLOB is shown as 0x…, read back by unhex).</summary>
    static object ToInput(string? columnType, string text)
    {
        if (DuckDbTypes.BaseName(columnType) is "BLOB" or "BYTEA")
        {
            if (text.EndsWith('…')) throw new FormatException("The binary value is shortened in the grid.");
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(text[2..]) : Encoding.UTF8.GetBytes(text);
        }
        return text;
    }

    public (string? Kind, string? Column) Token(SnapObject table) => (null, null);
    public string TokenText(string alias, string column) => throw new NotSupportedException("DuckDB has no row version.");
    public string TokenMatch(string alias, string column, string valueExpr) => throw new NotSupportedException("DuckDB has no row version.");
    public string? TokenType => null;

    public string NullSafeEquals(string left, string right) => $"{left} IS NOT DISTINCT FROM {right}";

    /// <summary>The value as the grid shows it: BLOB as 0x + hex, every other type in DuckDB's VARCHAR form.</summary>
    public string ValueText(string expr, string? columnType) =>
        DuckDbTypes.BaseName(columnType) is "BLOB" or "BYTEA" ? $"('0x' || hex({expr}))" : $"CAST({expr} AS VARCHAR)";

    public string? LockReason(SnapColumn column) => DuckDbTypes.Nested.Contains(DuckDbTypes.BaseName(column.Type))
        ? "lists, structs, maps and unions are shown as text: change them with an UPDATE statement"
        : null;

    /// <summary>Floats are shown rounded and nested values as text: neither compares exactly with what the grid holds.</summary>
    public bool Comparable(string? columnType)
    {
        var n = DuckDbTypes.BaseName(columnType);
        return !DuckDbTypes.Floats.Contains(n) && !DuckDbTypes.Nested.Contains(n);
    }

    public bool ExactKey(string? columnType)
    {
        var n = DuckDbTypes.BaseName(columnType);
        return !DuckDbTypes.Floats.Contains(n) && !DuckDbTypes.Nested.Contains(n) && n is not ("BLOB" or "BYTEA" or "JSON");
    }

    public async Task<long> ExecuteCountedAsync(DbCommand command, CancellationToken ct) => await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

    public string InsertReturningKey(string table, IReadOnlyList<(string Column, string ValueExpr)> columns, IReadOnlyList<(string Column, string? Type, bool Generated)> key)
    {
        var sb = new StringBuilder("INSERT INTO ").Append(table);
        if (columns.Count > 0)
            sb.Append(" (").Append(string.Join(", ", columns.Select(c => Quote(c.Column)))).Append(") VALUES (").Append(string.Join(", ", columns.Select(c => c.ValueExpr))).Append(')');
        else sb.Append(" DEFAULT VALUES");
        return sb.Append(" RETURNING ").Append(string.Join(", ", key.Select(k => ValueText(Quote(k.Column), k.Type)))).ToString();
    }

    public string PreviewLiteral(string? columnType, GridValue value)
    {
        if (value.UseDefault) return "DEFAULT";
        if (value.IsNull || value.Text == null) return "NULL";
        return DuckDbTypes.BaseName(columnType) is "BLOB" or "BYTEA" && value.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? $"unhex('{value.Text[2..].Replace("'", "''")}')"
            : "'" + value.Text.Replace("'", "''") + "'";
    }

    public string PreviewTerminator => ";";

    public string? ConcurrencyNote(string? tokenKind) => tokenKind == null
        ? "DuckDB keeps no row version: concurrent changes are found by comparing the original values of the changed columns."
        : null;
}

/// <summary>
/// Import into DuckDB (Phase D3): the appender (<see cref="DuckDbBulkLoader"/>, chosen over batched INSERTs by §14 metric
/// 23) inside the transaction rules of import.run; new tables get DuckDB types; ALTER COLUMN … TYPE widens in place.
/// </summary>
public sealed class DuckDbImport : IImportDialect
{
    static string Q(string s) => DuckDbTokenizer.Quote(s);

    public string TypeFor(ImportColumnProfile c) => c.Kind switch
    {
        ImportKind.Bool => "BOOLEAN",
        ImportKind.Int32 => "INTEGER",
        ImportKind.Int64 => "BIGINT",
        ImportKind.Decimal => c.Precision <= 38 ? $"DECIMAL({Math.Clamp(c.Precision, 1, 38)},{Math.Clamp(c.Scale, 0, Math.Clamp(c.Precision, 1, 38))})" : "VARCHAR",
        ImportKind.Double => "DOUBLE",
        ImportKind.Date => "DATE",
        ImportKind.DateTime => "TIMESTAMP",
        ImportKind.DateTimeOffset => "TIMESTAMPTZ",
        ImportKind.Time => "TIME",
        ImportKind.Uuid => "UUID",
        _ => "VARCHAR",
    };

    public bool NullUsesDefault => false;

    public bool BlankIsNull(string? type) => DuckDbTypes.BaseName(type) is not ("VARCHAR" or "TEXT" or "STRING" or "CHAR" or "BPCHAR");

    static (BigInteger Min, BigInteger Max)? IntRange(string n) => n switch
    {
        "TINYINT" or "INT1" => (sbyte.MinValue, sbyte.MaxValue),
        "SMALLINT" or "INT2" or "SHORT" => (short.MinValue, short.MaxValue),
        "INTEGER" or "INT" or "INT4" or "SIGNED" => (int.MinValue, int.MaxValue),
        "BIGINT" or "INT8" or "LONG" => (long.MinValue, long.MaxValue),
        "HUGEINT" or "INT128" => (-(BigInteger.One << 127), (BigInteger.One << 127) - 1),
        "UTINYINT" => (0, byte.MaxValue),
        "USMALLINT" => (0, ushort.MaxValue),
        "UINTEGER" => (0, uint.MaxValue),
        "UBIGINT" => (0, ulong.MaxValue),
        _ => null,
    };

    static int Digits(string? v) => v == null ? 0 : v.TrimStart('-', '+').TrimStart('0').Length;

    public ImportFit Check(ImportColumnProfile c, SnapColumn target)
    {
        var type = target.Type;
        var name = DuckDbTypes.BaseName(type);
        if (c.Kind == ImportKind.Empty) return new ImportFit(ImportVerdictKind.Fits, "Every value is NULL");
        var fits = new ImportFit(ImportVerdictKind.Fits, $"Fits {type}");
        ImportFit Bad(string what) => new(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not {what}: those rows will be rejected");
        if (IntRange(name) is var (min, max))
        {
            if (c.Kind is not (ImportKind.Int32 or ImportKind.Int64))
                return c.Kind == ImportKind.Decimal && c.Scale == 0 && c.Precision <= 38 ? new ImportFit(ImportVerdictKind.Widen, $"Whole numbers up to {c.Precision} digits do not fit {type}", $"DECIMAL({c.Precision},0)") : Bad("whole numbers");
            var lo = BigInteger.Parse(c.Min ?? "0", CultureInfo.InvariantCulture);
            var hi = BigInteger.Parse(c.Max ?? "0", CultureInfo.InvariantCulture);
            if (lo >= min && hi <= max) return fits;
            return new ImportFit(ImportVerdictKind.Widen, $"Values range {c.Min}…{c.Max}, outside {type}", hi <= long.MaxValue && lo >= long.MinValue ? "BIGINT" : "HUGEINT");
        }
        switch (name)
        {
            case "DECIMAL" or "NUMERIC":
            {
                if (c.Kind == ImportKind.Double) return new ImportFit(ImportVerdictKind.Widen, "Values use exponent notation or are very long", "DOUBLE");
                var (intDigits, scale) = c.Kind switch
                {
                    ImportKind.Int32 or ImportKind.Int64 => (Math.Max(Digits(c.Min), Digits(c.Max)), 0),
                    ImportKind.Decimal => (c.Precision - c.Scale, c.Scale),
                    _ => (-1, -1),
                };
                if (intDigits < 0) return Bad("numbers");
                var (p, s) = DuckDbTypes.DecimalArgs(type) ?? (18, 3);
                if (intDigits <= p - s && scale <= s) return fits;
                var ns = Math.Max(s, scale);
                var np = Math.Max(p - s, intDigits) + ns;
                return new ImportFit(ImportVerdictKind.Widen, $"Values need {intDigits} digit(s) before and {scale} after the point; {type} allows {p - s} and {s}",
                    np <= 38 ? $"DECIMAL({np},{ns})" : "DOUBLE");
            }
            case "FLOAT" or "REAL" or "FLOAT4" or "DOUBLE" or "FLOAT8":
                return c.Kind is ImportKind.Int32 or ImportKind.Int64 or ImportKind.Decimal or ImportKind.Double ? fits : Bad("numbers");
            case "BOOLEAN" or "BOOL" or "LOGICAL":
                return c.Kind == ImportKind.Bool || c.Kind == ImportKind.Int32 && c.Min is "0" or "1" && c.Max is "0" or "1" ? fits : Bad("true/false");
            case "DATE":
                return c.Kind == ImportKind.Date ? fits
                    : c.Kind == ImportKind.DateTime ? new ImportFit(ImportVerdictKind.Widen, "Values have a time of day, which DATE would drop", "TIMESTAMP") : Bad("dates (yyyy-mm-dd)");
            case "TIMESTAMP" or "DATETIME" or "TIMESTAMP_US" or "TIMESTAMP_MS" or "TIMESTAMP_S" or "TIMESTAMP_NS":
                if (c.Kind is ImportKind.Date or ImportKind.DateTime) return fits;
                return c.Kind == ImportKind.DateTimeOffset ? new ImportFit(ImportVerdictKind.Widen, "Values carry a UTC offset, which TIMESTAMP would drop", "TIMESTAMPTZ") : Bad("dates and times");
            case "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE":
                if (c.Kind is ImportKind.Date or ImportKind.DateTime) return new ImportFit(ImportVerdictKind.Fits, $"Fits {type}; values without an offset are read in the session time zone");
                return c.Kind == ImportKind.DateTimeOffset ? fits : Bad("dates and times");
            case "TIME":
                return c.Kind == ImportKind.Time ? fits : Bad("times of day");
            case "UUID":
                return c.Kind == ImportKind.Uuid ? fits : Bad("UUIDs");
            case "VARCHAR" or "TEXT" or "STRING" or "CHAR" or "BPCHAR" or "JSON":
                return fits;
            default:
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: DuckDB converts the text when the rows are loaded");
        }
    }

    static string Describe(ImportColumnProfile c) => c.Kind switch
    {
        ImportKind.Bool => "true/false",
        ImportKind.Int32 or ImportKind.Int64 => "whole numbers",
        ImportKind.Decimal => "decimals",
        ImportKind.Double => "numbers",
        ImportKind.Date => "dates",
        ImportKind.DateTime => "dates and times",
        ImportKind.DateTimeOffset => "dates and times with offsets",
        ImportKind.Time => "times of day",
        ImportKind.Uuid => "UUIDs",
        _ => "text",
    };

    public string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey)
    {
        var lines = columns.Select(c => $"    {Q(c.Name)} {c.Type}{(c.NotNull || primaryKey.Contains(c.Name) ? " NOT NULL" : "")}").ToList();
        if (primaryKey.Count > 0) lines.Add($"    PRIMARY KEY ({string.Join(", ", primaryKey.Select(Q))})");
        return $"CREATE TABLE {Q(schema)}.{Q(table)} (\n{string.Join(",\n", lines)}\n);";
    }

    public string AlterColumnType(string schema, string table, SnapColumn column, string newType) =>
        $"ALTER TABLE {Q(schema)}.{Q(table)} ALTER COLUMN {Q(column.Name)} TYPE {newType};";

    public string AddColumn(string schema, string table, string column, string type) =>
        $"ALTER TABLE {Q(schema)}.{Q(table)} ADD COLUMN {Q(column)} {type};";

    public IBulkLoader? Loader { get; } = new DuckDbBulkLoader();
}
