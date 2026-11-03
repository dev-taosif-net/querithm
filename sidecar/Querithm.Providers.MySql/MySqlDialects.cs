using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Text;
using MySqlConnector;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;

namespace Querithm.Providers.MySql;

/// <summary>A MySQL / MariaDB COLUMN_TYPE taken apart: "int(11) unsigned zerofill" → ("int", [11], unsigned).</summary>
public readonly record struct MySqlType(string Name, int[] Args, bool Unsigned)
{
    public static MySqlType Parse(string? columnType)
    {
        var t = (columnType ?? "").Trim().ToLowerInvariant();
        var open = t.IndexOf('(');
        var close = open < 0 ? -1 : t.IndexOf(')', open);
        var name = (open < 0 ? t.Split(' ')[0] : t[..open]).Trim();
        int[] args = [];
        // enum('a','b') / set(…) carry values, not sizes.
        if (open >= 0 && close > open && name is not ("enum" or "set"))
            args = t[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries)
                .Select(a => int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n >= 0).ToArray();
        var rest = close >= 0 ? t[(close + 1)..] : open < 0 ? t[name.Length..] : "";
        return new MySqlType(name, args, rest.Contains("unsigned", StringComparison.Ordinal));
    }

    public bool IsBool => Name is "tinyint" or "bool" or "boolean" && (Args is [1] || Name != "tinyint");
    public bool IsBinary => Name is "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob";
    /// <summary>The grid shows the first 64 bytes of a binary value: beyond that the text is shortened.</summary>
    public bool IsShortBinary => Name is "binary" or "varbinary" && Args is [<= 64];
    public bool IsSpatial => Name is "geometry" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection" or "geomcollection";
    public bool IsFloat => Name is "float" or "double" or "real" or "double precision";
    public bool IsText => Name is "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext";

    public (BigInteger Min, BigInteger Max)? IntRange => Name switch
    {
        "tinyint" or "bool" or "boolean" => Unsigned ? (0, byte.MaxValue) : (sbyte.MinValue, sbyte.MaxValue),
        "smallint" => Unsigned ? (0, ushort.MaxValue) : (short.MinValue, short.MaxValue),
        "mediumint" => Unsigned ? (0, 16777215) : (-8388608, 8388607),
        "int" or "integer" => Unsigned ? (0, uint.MaxValue) : (int.MinValue, int.MaxValue),
        "bigint" => Unsigned ? (0, ulong.MaxValue) : (long.MinValue, long.MaxValue),
        _ => null,
    };
}

/// <summary>
/// Grid edits on MySQL / MariaDB (Phase D2). Values travel as parameters: text the server converts with its own rules,
/// except binary (the grid's 0x… hex → bytes), BIT (a whole number) and booleans (TINYINT(1) shown as 1/0, true/false
/// accepted). Values the server would reject or silently change are refused here first for the common types, so a bad
/// import row is rejected instead of failing the statement. There is no row version: lost updates are caught by comparing
/// the original values of the changed columns with &lt;=&gt; (text compares with the column's collation). Row counts are
/// rows matched, not rows changed (MySqlConnector's UseAffectedRows=false), so writing an unchanged value is not a conflict.
/// </summary>
public sealed class MySqlDataEdit : IDataEditDialect
{
    public string Quote(string identifier) => MySqlTokenizer.Quote(identifier);
    public string Placeholder(int index) => "@p" + index.ToString(CultureInfo.InvariantCulture);
    public string TypedPlaceholder(int index, string? columnType) => Placeholder(index);

    public void AddValue(DbCommand command, int index, string? columnType, GridValue value) =>
        command.Parameters.Add(new MySqlParameter(Placeholder(index), value.IsNull || value.Text == null ? DBNull.Value : ToParameter(MySqlType.Parse(columnType), value.Text)));

    /// <summary>The grid's text as the parameter the column takes; <see cref="FormatException"/> when it is not a valid value.</summary>
    public static object ToParameter(MySqlType t, string text)
    {
        var s = text.Trim();
        if (t.IsBinary)
        {
            if (text.EndsWith('…')) throw new FormatException("The binary value is shortened in the grid.");
            if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || (s.Length - 2) % 2 != 0)
                throw new FormatException($"'{Clip(text)}' is not a binary value (write 0x followed by pairs of hex digits).");
            try { return Convert.FromHexString(s.AsSpan(2)); }
            catch (FormatException) { throw new FormatException($"'{Clip(text)}' is not a binary value (write 0x followed by pairs of hex digits)."); }
        }
        if (t.Name == "bit")
            return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) && (t.Args is not [var n and < 64] || bits >> n == 0)
                ? bits : throw new FormatException($"'{Clip(text)}' is not a valid {t.Name}({(t.Args.Length > 0 ? t.Args[0] : 1)}) value (a whole number).");
        if (t.IsBool && ValueClassifier.IsBool(s)) return s.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (t.IntRange is var (min, max))
            return BigInteger.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
                ? s : throw new FormatException($"'{Clip(text)}' is not a valid {t.Name}{(t.Unsigned ? " unsigned" : "")} (whole numbers {min}…{max}).");
        if (t.Name is "decimal" or "numeric" or "dec" or "fixed" || t.IsFloat)
            return IsNumber(s) ? s : throw new FormatException($"'{Clip(text)}' is not a number.");
        if (t.Name == "date") return IsZeroDate(s) || DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? s : throw new FormatException($"'{Clip(text)}' is not a date (yyyy-mm-dd).");
        if (t.Name is "datetime" or "timestamp")
            return IsZeroDate(s) || DateTime.TryParseExact(s, DateTimeForms, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? s : throw new FormatException($"'{Clip(text)}' is not a date and time (yyyy-mm-dd hh:mm:ss).");
        if (t.Name == "year")
            return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && (y is 0 or >= 1901 and <= 2155 || s.Length <= 2)
                ? s : throw new FormatException($"'{Clip(text)}' is not a year (1901–2155).");
        return text;
    }

    static readonly string[] DateTimeForms =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFF",
    ];

    static bool IsZeroDate(string s) => s.StartsWith("0000-00-00", StringComparison.Ordinal);

    /// <summary>[sign] digits [. digits] [e [sign] digits], or a bare fraction.</summary>
    static bool IsNumber(string s)
    {
        var i = 0;
        if (i < s.Length && s[i] is '+' or '-') i++;
        var digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.') { i++; while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; } }
        if (digits == 0) return false;
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            i++;
            if (i < s.Length && s[i] is '+' or '-') i++;
            var exp = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; exp++; }
            if (exp == 0) return false;
        }
        return i == s.Length;
    }

    static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    public (string? Kind, string? Column) Token(SnapObject table) => (null, null);
    public string TokenText(string alias, string column) => throw new NotSupportedException("MySQL / MariaDB tables have no row version.");
    public string TokenMatch(string alias, string column, string valueExpr) => throw new NotSupportedException("MySQL / MariaDB tables have no row version.");
    public string? TokenType => null;

    public string NullSafeEquals(string left, string right) => $"{left} <=> {right}";

    /// <summary>As the grid shows it: binary as 0x + hex, BIT as a number, everything else as the server renders it as text.</summary>
    public string ValueText(string expr, string? columnType)
    {
        var t = MySqlType.Parse(columnType);
        if (t.IsBinary) return $"CONCAT('0x', HEX({expr}))";
        if (t.Name == "bit") return $"CAST(CAST({expr} AS UNSIGNED) AS CHAR)";
        return $"CAST({expr} AS CHAR)";
    }

    public string? LockReason(SnapColumn column) =>
        MySqlType.Parse(column.Type).IsSpatial ? "spatial values are shown as text: change them with an UPDATE statement" : null;

    /// <summary>Floating point and TIME (shown through a time-of-day type) do not round-trip as text; JSON compares as a document; long binary is shortened in the grid.</summary>
    public bool Comparable(string? columnType)
    {
        var t = MySqlType.Parse(columnType);
        if (t.IsBinary) return t.IsShortBinary;
        return !(t.IsFloat || t.IsSpatial || t.Name is "json" or "time");
    }

    public bool ExactKey(string? columnType)
    {
        var t = MySqlType.Parse(columnType);
        if (t.IsBinary) return t.IsShortBinary;
        return !(t.IsFloat || t.IsSpatial || t.Name is "json" or "time");
    }

    public async Task<long> ExecuteCountedAsync(DbCommand command, CancellationToken ct) => await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// MySQL has no RETURNING: the INSERT is followed by a SELECT of the new row by its key — the given key values, and
    /// LAST_INSERT_ID() for the AUTO_INCREMENT column. A key the server fills any other way (an expression default) cannot be
    /// read back, so no key is returned rather than a guessed one.
    /// </summary>
    public string InsertReturningKey(string table, IReadOnlyList<(string Column, string ValueExpr)> columns, IReadOnlyList<(string Column, string? Type, bool Generated)> key)
    {
        var sb = new StringBuilder("INSERT INTO ").Append(table)
            .Append(" (").Append(string.Join(", ", columns.Select(c => Quote(c.Column)))).Append(") VALUES (")
            .Append(string.Join(", ", columns.Select(c => c.ValueExpr))).Append(')');
        var given = columns.GroupBy(c => c.Column, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ValueExpr, StringComparer.OrdinalIgnoreCase);
        var where = key.Select(k => given.TryGetValue(k.Column, out var e) ? $"{Quote(k.Column)} <=> {e}"
            : k.Generated ? $"{Quote(k.Column)} = LAST_INSERT_ID()" : "FALSE").ToList();
        if (key.Count(k => !given.ContainsKey(k.Column)) > 1) where.Add("FALSE"); // one AUTO_INCREMENT column per table
        return sb.Append("; SELECT ").Append(string.Join(", ", key.Select(k => ValueText(Quote(k.Column), k.Type))))
            .Append(" FROM ").Append(table).Append(" WHERE ").Append(string.Join(" AND ", where)).ToString();
    }

    public string PreviewLiteral(string? columnType, GridValue value)
    {
        if (value.UseDefault) return "DEFAULT";
        if (value.IsNull || value.Text == null) return "NULL";
        var t = MySqlType.Parse(columnType);
        try
        {
            return ToParameter(t, value.Text) switch
            {
                byte[] b => "X'" + Convert.ToHexString(b) + "'",
                ulong u => u.ToString(CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                _ => Literal(value.Text),
            };
        }
        catch (FormatException) { return Literal(value.Text); } // the preview shows it; applying reports the error
    }

    /// <summary>A string literal that reads the same with or without NO_BACKSLASH_ESCAPES.</summary>
    static string Literal(string s) => s.Contains('\\') ? "X'" + Convert.ToHexString(Encoding.UTF8.GetBytes(s)) + "'" : "'" + s.Replace("'", "''") + "'";

    public string PreviewTerminator => ";";

    public string? ConcurrencyNote(string? tokenKind) => tokenKind == null
        ? "MySQL / MariaDB keep no row version: concurrent changes are found by comparing the original values of the changed columns (text compares with the column's collation)."
        : null;

    /// <summary>MariaDB has no column list on a derived table (MySQL only since 8.0.19), so the keys are a UNION ALL.</summary>
    public string ValuesRelation(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<string> names, string alias) =>
        IDataEditDialect.UnionAllRelation(rows, names, alias);
}

/// <summary>
/// Import into MySQL / MariaDB (Phase D2): multi-row INSERTs (<see cref="MySqlInsertLoader"/>) inside the transaction rules of
/// import.run — never LOAD DATA LOCAL (see MySqlProvider: local-infile stays off). New tables are InnoDB with types from the
/// file's profile; a widening is offered as MODIFY COLUMN, which restates the whole column.
/// </summary>
public sealed class MySqlImport : IImportDialect
{
    static string Q(string s) => MySqlTokenizer.Quote(s);

    public string TypeFor(ImportColumnProfile c) => c.Kind switch
    {
        ImportKind.Bool => "tinyint(1)",
        ImportKind.Int32 => "int",
        ImportKind.Int64 => "bigint",
        ImportKind.Decimal => c.Precision <= 65 && c.Scale <= 30 ? $"decimal({Math.Max(1, c.Precision)},{c.Scale})" : "text",
        ImportKind.Double => "double",
        ImportKind.Date => "date",
        ImportKind.DateTime => c.Precision is > 0 ? $"datetime({Math.Min(6, c.Precision)})" : "datetime",
        ImportKind.DateTimeOffset => "varchar(64)", // no type keeps the offset: the text is kept as it is
        ImportKind.Time => c.Precision is > 0 ? $"time({Math.Min(6, c.Precision)})" : "time",
        ImportKind.Uuid => "char(36)",
        _ => TextType(c.MaxLength),
    };

    /// <summary>VARCHAR up to 255 characters; past that TEXT types by their byte limits at 4 bytes a character (utf8mb4).</summary>
    static string TextType(int maxLength) => maxLength switch
    {
        <= 255 => "varchar(255)",
        <= 16383 => "text",
        <= 4194303 => "mediumtext",
        _ => "longtext",
    };

    public bool NullUsesDefault => false;

    /// <summary>A blank cell is NULL except in a text column, where the blanks are the value.</summary>
    public bool BlankIsNull(string? type) => !MySqlType.Parse(type).IsText && MySqlType.Parse(type).Name is not ("enum" or "set");

    public ImportFit Check(ImportColumnProfile c, SnapColumn target)
    {
        var t = MySqlType.Parse(target.Type);
        var type = target.Type;
        if (c.Kind == ImportKind.Empty) return new ImportFit(ImportVerdictKind.Fits, "Every value is NULL");
        var fits = new ImportFit(ImportVerdictKind.Fits, $"Fits {type}");
        ImportFit Bad(string what) => new(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not {what}: those rows will be rejected");
        if (t.IsSpatial) return new ImportFit(ImportVerdictKind.Mismatch, $"{type} values cannot be loaded from text");
        if (t.IsBool && c.Kind == ImportKind.Bool) return fits;
        if (t.IntRange is var (min, max))
        {
            if (c.Kind is not (ImportKind.Int32 or ImportKind.Int64 or ImportKind.Decimal) || c.Kind == ImportKind.Decimal && c.Scale > 0) return Bad("whole numbers");
            var lo = BigInteger.Parse(c.Min ?? "0", CultureInfo.InvariantCulture);
            var hi = BigInteger.Parse(c.Max ?? "0", CultureInfo.InvariantCulture);
            if (lo >= min && hi <= max) return fits;
            var widen = lo >= long.MinValue && hi <= long.MaxValue ? "bigint" : lo >= 0 && hi <= ulong.MaxValue ? "bigint unsigned" : "decimal(65,0)";
            return new ImportFit(ImportVerdictKind.Widen, $"Values range {c.Min}…{c.Max}, outside {type}", widen);
        }
        switch (t.Name)
        {
            case "decimal" or "numeric" or "dec" or "fixed":
            {
                var (intDigits, scale) = c.Kind switch
                {
                    ImportKind.Int32 or ImportKind.Int64 => (Math.Max(Digits(c.Min), Digits(c.Max)), 0),
                    ImportKind.Decimal => (c.Precision - c.Scale, c.Scale),
                    ImportKind.Double => (-2, -2),
                    _ => (-1, -1),
                };
                if (intDigits == -1) return Bad("numbers");
                var (p, s) = (t.Args.Length > 0 ? t.Args[0] : 10, t.Args.Length > 1 ? t.Args[1] : 0);
                if (intDigits == -2) return new ImportFit(ImportVerdictKind.Fits, $"Values use exponent notation; {type} rounds them to {s} decimal place(s)");
                if (intDigits <= p - s && scale <= s) return fits;
                var ns = Math.Max(s, scale);
                var np = Math.Max(p - s, intDigits) + ns;
                return np <= 65 && ns <= 30
                    ? new ImportFit(ImportVerdictKind.Widen, $"Values need {intDigits} digit(s) before and {scale} after the point; {type} allows {p - s} and {s}", $"decimal({np},{ns})")
                    : new ImportFit(ImportVerdictKind.Mismatch, $"Values need {intDigits} digit(s) before and {scale} after the point, more than DECIMAL holds (65 digits, 30 after the point)");
            }
            case "float" or "double" or "real" or "double precision":
                return c.Kind is ImportKind.Int32 or ImportKind.Int64 or ImportKind.Decimal or ImportKind.Double ? fits : Bad("numbers");
            case "bit":
                return c.Kind is ImportKind.Int32 or ImportKind.Int64 && !(c.Min ?? "0").StartsWith('-') ? fits : Bad("whole numbers ≥ 0");
            case "year":
                return c.Kind == ImportKind.Int32 ? fits : Bad("years");
            case "date":
                return c.Kind == ImportKind.Date ? fits
                    : c.Kind == ImportKind.DateTime ? new ImportFit(ImportVerdictKind.Widen, "Values have a time of day, which date would drop", "datetime") : Bad("dates (yyyy-mm-dd)");
            case "datetime" or "timestamp":
                if (c.Kind is not (ImportKind.Date or ImportKind.DateTime)) return Bad("dates and times without an offset");
                var fsp = t.Args.Length > 0 ? t.Args[0] : 0;
                if (c.Kind == ImportKind.DateTime && c.Precision > fsp)
                    return new ImportFit(ImportVerdictKind.Fits, $"Fits {type}; the server rounds to {(fsp == 0 ? "whole seconds" : $"{fsp} fractional digit(s)")}");
                return t.Name == "timestamp" ? new ImportFit(ImportVerdictKind.Fits, $"Fits {type} for 1970–2038, read in the session time zone") : fits;
            case "time":
                return c.Kind == ImportKind.Time ? fits : Bad("times of day");
            case "char" or "varchar":
            {
                var n = t.Args.Length > 0 ? t.Args[0] : 1;
                if (c.MaxLength <= n) return fits;
                return new ImportFit(ImportVerdictKind.Widen, $"Values are up to {c.MaxLength:N0} characters; {type} holds {n}", c.MaxLength <= 16383 ? $"varchar({c.MaxLength})" : TextType(c.MaxLength));
            }
            case "tinytext" or "text" or "mediumtext":
            {
                var limit = t.Name switch { "tinytext" => 63, "text" => 16383, _ => 4194303 }; // characters at 4 bytes each
                return c.MaxLength <= limit ? fits : new ImportFit(ImportVerdictKind.Widen, $"Values are up to {c.MaxLength:N0} characters, more than {type} holds", TextType(c.MaxLength));
            }
            case "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob":
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: each value must be 0x followed by hex digits, or the row is rejected");
            case "enum" or "set":
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: a value that is not one of its members stops the import (strict SQL mode)");
            case "json":
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: the server checks each value is valid JSON");
            default:
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: the server converts the text when the rows are loaded");
        }
    }

    static int Digits(string? v) => v == null ? 0 : v.TrimStart('-', '+').TrimStart('0').Length;

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

    /// <summary>A TEXT column cannot be a key without a prefix length: a key column gets VARCHAR(768) (3072 bytes, InnoDB's key limit).</summary>
    public string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey)
    {
        var lines = columns.Select(c =>
        {
            var key = primaryKey.Contains(c.Name);
            var type = key && MySqlType.Parse(c.Type).Name is "tinytext" or "text" or "mediumtext" or "longtext" ? "varchar(768)" : c.Type;
            return $"    {Q(c.Name)} {type}{(c.NotNull || key ? " NOT NULL" : "")}";
        }).ToList();
        if (primaryKey.Count > 0) lines.Add($"    PRIMARY KEY ({string.Join(", ", primaryKey.Select(Q))})");
        return $"CREATE TABLE {Q(schema)}.{Q(table)} (\n{string.Join(",\n", lines)}\n) ENGINE=InnoDB;";
    }

    public string AlterColumnType(string schema, string table, SnapColumn column, string newType) =>
        $"-- MODIFY restates the whole column: add back its DEFAULT, character set, collation and comment if it has them.\n" +
        $"ALTER TABLE {Q(schema)}.{Q(table)} MODIFY COLUMN {Q(column.Name)} {newType} {(column.Nullable ? "NULL" : "NOT NULL")};";

    public string AddColumn(string schema, string table, string column, string type) => $"ALTER TABLE {Q(schema)}.{Q(table)} ADD COLUMN {Q(column)} {type};";

    public IBulkLoader? Loader { get; } = new MySqlInsertLoader(new MySqlTransactions());
}

/// <summary>
/// MySQL / MariaDB's import path: multi-row INSERTs of up to 1,000 rows (and about 4 MB of values, under the 16 MB
/// max_allowed_packet MariaDB ships with), each value converted once for its column's parsed type; a value the type cannot
/// take rejects its row. The generic loader converted every value twice (a validation pass, then the parameter), parsed
/// the column type per value and sent 200 rows per statement: 100k × 10 took 4.97 s (bench #23.mysql, 2026-10-31; a
/// Stopwatch profile of that shape put 60 % of the time in the two conversions). Own: one transaction, all rows or none;
/// Caller: inside the user's transaction (the service holds a savepoint); Batches: a transaction committed every
/// <see cref="BulkTransaction.CommitEvery"/> rows.
/// </summary>
public sealed class MySqlInsertLoader(ITransactionControl transactions) : IBulkLoader
{
    const int MaxRows = 1000;
    const int MaxChars = 4 << 20;

    public async Task LoadAsync(DbConnection connection, BulkTarget target, IBulkRows rows, ImportRunOptions options, BulkTransaction tx, int? commandTimeoutS, CancellationToken ct)
    {
        var n = target.Columns.Count;
        var types = target.Columns.Select(c => MySqlType.Parse(c.Type)).ToArray();
        var head = $"INSERT INTO {MySqlTokenizer.Quote(target.Schema)}.{MySqlTokenizer.Quote(target.Table)} ({string.Join(", ", target.Columns.Select(c => MySqlTokenizer.Quote(c.Name)))}) VALUES ";
        var own = tx.Mode != BulkTxMode.Caller;
        var batches = tx.Mode == BulkTxMode.Batches;
        var values = new object[n];
        long sent = 0, sinceCommit = 0;
        if (own) await transactions.BeginAsync(connection, ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var limit = batches ? (int)Math.Min(MaxRows, tx.CommitEvery - sinceCommit) : MaxRows;
                await using var cmd = connection.CreateCommand();
                cmd.CommandTimeout = commandTimeoutS ?? 0;
                var sql = new StringBuilder(head);
                int count = 0, chars = 0, p = 0;
                while (count < limit && chars < MaxChars && rows.Read())
                {
                    var row = rows.Current;
                    string? bad = null;
                    var size = 0;
                    for (var i = 0; i < n && bad == null; i++)
                    {
                        var text = i < row.Count ? row[i] : null;
                        size += text?.Length ?? 0;
                        try { values[i] = text == null ? DBNull.Value : MySqlDataEdit.ToParameter(types[i], text); }
                        catch (FormatException fe) { bad = $"{target.Columns[i].Name}: {fe.Message}"; }
                    }
                    if (bad != null) { rows.Reject(bad); continue; }
                    sql.Append(count == 0 ? "(" : ", (");
                    for (var i = 0; i < n; i++)
                    {
                        var name = "@p" + (p++).ToString(CultureInfo.InvariantCulture);
                        cmd.Parameters.Add(new MySqlParameter(name, values[i]));
                        sql.Append(i == 0 ? "" : ", ").Append(name);
                    }
                    sql.Append(')');
                    count++;
                    chars += size;
                }
                if (count == 0) break;
                cmd.CommandText = sql.ToString();
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                sent += count;
                sinceCommit += count;
                if (batches && sinceCommit >= tx.CommitEvery)
                {
                    await tx.CommitAsync(() => transactions.CommitAsync(connection, ct)).ConfigureAwait(false);
                    rows.Loaded(sent, true);
                    sinceCommit = 0;
                    await transactions.BeginAsync(connection, ct).ConfigureAwait(false);
                }
                else rows.Loaded(sent, false);
            }
            if (own) await tx.CommitAsync(() => transactions.CommitAsync(connection, ct)).ConfigureAwait(false);
            rows.Loaded(sent, batches);
        }
        catch
        {
            if (own) try { using var cleanup = Cleanup.Start(); await transactions.RollbackAsync(connection, cleanup.Token).ConfigureAwait(false); } catch { /* connection state is re-read afterwards */ }
            throw;
        }
    }
}

/// <summary>
/// Data compare on MySQL / MariaDB (Phase D2): text keys sort by their UTF-8 bytes (<c>CAST(CONVERT(x USING utf8mb4) AS
/// BINARY)</c> — code-point order with no padding and no collation folding, on both products), which is the order the
/// compare reproduces in .NET; a custom query runs in START TRANSACTION READ ONLY, so a write inside it fails on the server.
/// </summary>
public sealed class MySqlDataCompare : IDataCompareDialect
{
    public ValueKind KindOf(string? columnType)
    {
        var t = MySqlType.Parse(columnType);
        if (t.IsSpatial) return ValueKind.Unordered;
        if (t.IntRange != null || t.Name is "year" or "bit") return ValueKind.Integer;
        return t.Name switch
        {
            "char" => ValueKind.PaddedText, // CHAR values come back without their padding
            "decimal" or "numeric" or "dec" or "fixed" => ValueKind.Decimal,
            "float" or "double" or "real" or "double precision" => ValueKind.Float,
            "date" => ValueKind.Date,
            "datetime" or "timestamp" => ValueKind.DateTime,
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => ValueKind.Binary,
            // TIME spans ±838 hours (not a time of day); JSON compares as a document: no shared order.
            "time" or "json" => ValueKind.Unordered,
            _ => ValueKind.Text, // varchar, text types, enum and set (ordered by their text below, not by member index)
        };
    }

    public string OrderExpression(string columnExpr, ValueKind kind) => kind switch
    {
        ValueKind.Text or ValueKind.PaddedText or ValueKind.Guid => $"CAST(CONVERT({columnExpr} USING utf8mb4) AS BINARY)",
        _ => columnExpr,
    };

    /// <summary>MySQL 8's default collation (utf8mb4_0900_ai_ci) is NO PAD; the compare matches text keys exactly.</summary>
    public bool PadsTrailingSpaces => false;

    public string? QueryRefusal(string sql)
    {
        var statements = MySqlSplitter.Split(sql).Select(st => st.Tokens.Where(t => !t.IsTrivia).ToList()).Where(c => c.Count > 0 && !(c.Count == 1 && c[0].Text == ";")).ToList();
        if (statements.Count == 0) return "The query is empty.";
        if (statements.Count > 1) return "A compared query must be exactly one statement.";
        var code = statements[0];
        while (code.Count > 0 && code[^1].Text == ";") code.RemoveAt(code.Count - 1);
        if (!(code[0].Is("SELECT") || code[0].Is("WITH") || code[0].Is("TABLE") || code[0].Is("VALUES") || code[0].Text == "("))
            return "A compared query must be a SELECT (or TABLE / VALUES).";
        var verb = MySqlLanguageService.Verb(code);
        if (code[0].Is("WITH") && verb != "SELECT") return "A compared query must be a SELECT (or TABLE / VALUES).";
        if (verb == "SELECT" && MySqlLanguageService.HasTopLevel(code, "SELECT", "INTO")) return "A compared query cannot use INTO (a variable or a file).";
        return null;
    }

    /// <summary>MySQL takes WITH and ORDER BY inside a derived table: only trailing semicolons and comments go.</summary>
    public (string Prefix, string Body) DerivedQuery(string sql)
    {
        var code = MySqlSplitter.Split(sql).SelectMany(st => st.Tokens).Where(t => !t.IsTrivia).ToList();
        while (code.Count > 0 && code[^1].Text == ";") code.RemoveAt(code.Count - 1);
        if (code.Count == 0) return ("", sql);
        return ("", sql[code[0].Offset..(code[^1].Offset + code[^1].Length)]);
    }

    public string IsolationHint =>
        "Rows that change while the compare reads show up as differences: compare a quiet copy, or read both sides under REPEATABLE READ (InnoDB's default keeps one snapshot per transaction).";

    public (string On, string Off)? IdentityInsert(string table) => null; // an explicit AUTO_INCREMENT value is simply inserted
    public string? InsertOverriding => null;
    public int InsertBatch => 1000;
    public (string Begin, string Commit) SyncTransaction => ("START TRANSACTION;", "COMMIT;");
}
