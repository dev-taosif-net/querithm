using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Grid edits on SQL Server: typed SqlParameters built from the catalog type, rowversion tokens,
/// @@ROWCOUNT read straight after each statement (trigger DML inflates RecordsAffected) and OUTPUT … INTO a table
/// variable for generated keys (OUTPUT without INTO fails on tables with triggers).
/// </summary>
public sealed class SqlServerDataEdit : IDataEditDialect
{
    public string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public string Placeholder(int index) => "@p" + index.ToString(CultureInfo.InvariantCulture);

    public string TypedPlaceholder(int index, string? columnType) => Placeholder(index);

    /// <summary>A catalog type display ("decimal(18,2)", "nvarchar(max)") split into base name and arguments.</summary>
    public readonly record struct TypeSpec(string Name, int? A, int? B, bool Max)
    {
        public static TypeSpec Parse(string? type)
        {
            var t = (type ?? "nvarchar(max)").Trim().ToLowerInvariant();
            var open = t.IndexOf('(');
            if (open < 0 || !t.EndsWith(')')) return new TypeSpec(t, null, null, false);
            var name = t[..open].Trim();
            var args = t[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
            if (args.Length > 0 && args[0] == "max") return new TypeSpec(name, null, null, true);
            int? a = args.Length > 0 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : null;
            int? b = args.Length > 1 && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
            return new TypeSpec(name, a, b, false);
        }

        /// <summary>Fractional-second digits for datetime2/time/datetimeoffset (7 when the display omits it).</summary>
        public int TimeScale => A ?? 7;
    }

    public void AddValue(DbCommand command, int index, string? columnType, GridValue value)
    {
        var spec = TypeSpec.Parse(columnType);
        var p = new SqlParameter { ParameterName = Placeholder(index) };
        Configure(p, spec);
        p.Value = value.IsNull || value.Text == null ? DBNull.Value : Convert(spec, value.Text);
        command.Parameters.Add(p);
    }

    static void Configure(SqlParameter p, TypeSpec t)
    {
        switch (t.Name)
        {
            case "bigint": p.SqlDbType = SqlDbType.BigInt; break;
            case "int": p.SqlDbType = SqlDbType.Int; break;
            case "smallint": p.SqlDbType = SqlDbType.SmallInt; break;
            case "tinyint": p.SqlDbType = SqlDbType.TinyInt; break;
            case "bit": p.SqlDbType = SqlDbType.Bit; break;
            case "decimal" or "numeric":
                p.SqlDbType = SqlDbType.Decimal;
                p.Precision = (byte)(t.A ?? 18);
                p.Scale = (byte)(t.B ?? 0);
                break;
            case "money": p.SqlDbType = SqlDbType.Money; break;
            case "smallmoney": p.SqlDbType = SqlDbType.SmallMoney; break;
            case "float": p.SqlDbType = t.A is { } bits && bits <= 24 ? SqlDbType.Real : SqlDbType.Float; break;
            case "real": p.SqlDbType = SqlDbType.Real; break;
            case "date": p.SqlDbType = SqlDbType.Date; break;
            case "datetime": p.SqlDbType = SqlDbType.DateTime; break;
            case "smalldatetime": p.SqlDbType = SqlDbType.SmallDateTime; break;
            case "datetime2": p.SqlDbType = SqlDbType.DateTime2; p.Scale = (byte)t.TimeScale; break;
            case "datetimeoffset": p.SqlDbType = SqlDbType.DateTimeOffset; p.Scale = (byte)t.TimeScale; break;
            case "time": p.SqlDbType = SqlDbType.Time; p.Scale = (byte)t.TimeScale; break;
            case "uniqueidentifier": p.SqlDbType = SqlDbType.UniqueIdentifier; break;
            case "binary": p.SqlDbType = SqlDbType.Binary; p.Size = t.A ?? 1; break;
            case "varbinary": p.SqlDbType = SqlDbType.VarBinary; p.Size = t.Max ? -1 : t.A ?? 8000; break;
            case "image": p.SqlDbType = SqlDbType.Image; break;
            case "timestamp" or "rowversion": p.SqlDbType = SqlDbType.Binary; p.Size = 8; break;
            case "char": p.SqlDbType = SqlDbType.Char; p.Size = t.A ?? 1; break;
            case "varchar": p.SqlDbType = SqlDbType.VarChar; p.Size = t.Max ? -1 : t.A ?? 8000; break;
            case "text": p.SqlDbType = SqlDbType.Text; break;
            case "nchar": p.SqlDbType = SqlDbType.NChar; p.Size = t.A ?? 1; break;
            case "nvarchar": p.SqlDbType = SqlDbType.NVarChar; p.Size = t.Max ? -1 : t.A ?? 4000; break;
            case "ntext": p.SqlDbType = SqlDbType.NText; break;
            case "xml": p.SqlDbType = SqlDbType.Xml; break;
            default: p.SqlDbType = SqlDbType.NVarChar; p.Size = -1; break; // the server converts implicitly
        }
    }

    static readonly string[] DateFormats = ["yyyy-MM-dd"];

    /// <summary>The grid's invariant text → the CLR value for the parameter. Throws FormatException with a readable message.</summary>
    public static object Convert(TypeSpec t, string text)
    {
        var s = text.Trim();
        var inv = CultureInfo.InvariantCulture;
        try
        {
            return t.Name switch
            {
                "bigint" => long.Parse(s, NumberStyles.Integer, inv),
                "int" => int.Parse(s, NumberStyles.Integer, inv),
                "smallint" => short.Parse(s, NumberStyles.Integer, inv),
                "tinyint" => byte.Parse(s, NumberStyles.Integer, inv),
                "bit" => s switch
                {
                    "1" => true, "0" => false,
                    _ when bool.TryParse(s, out var b) => b,
                    _ => throw new FormatException(),
                },
                // An exponent ("1E5") is read as the exact number it writes, as the preview shows it (8a review).
                "decimal" or "numeric" => SqlDecimal.Parse(PlainDecimal(s) ?? s),
                "money" or "smallmoney" => decimal.Parse(PlainDecimal(s) ?? s, NumberStyles.Number, inv),
                "float" or "real" => double.Parse(s, NumberStyles.Float, inv),
                "date" => DateTime.ParseExact(s, DateFormats, inv, DateTimeStyles.None),
                "datetime" or "smalldatetime" or "datetime2" => DateTime.Parse(s, inv, DateTimeStyles.None),
                "datetimeoffset" => DateTimeOffset.Parse(s, inv, DateTimeStyles.None),
                "time" => TimeSpan.Parse(s, inv),
                "uniqueidentifier" => Guid.Parse(s),
                "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => Hex(s),
                _ => text, // strings keep their spaces
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new FormatException($"'{Clip(text)}' is not a valid {t.Name} value.");
        }
    }

    static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    static byte[] Hex(string s)
    {
        if (s.EndsWith('…')) throw new FormatException("The binary value is shortened in the grid.");
        var h = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return System.Convert.FromHexString(h.Length % 2 == 1 ? "0" + h : h);
    }

    public (string? Kind, string? Column) Token(SnapObject table)
    {
        var rv = table.Columns?.FirstOrDefault(c => TypeSpec.Parse(c.Type).Name is "timestamp" or "rowversion");
        return rv == null ? (null, null) : ("rowversion", rv.Name);
    }

    static string Prefix(string alias) => alias.Length > 0 ? alias + "." : "";

    // rowversion is not binary for CONVERT's style argument until cast.
    public string TokenText(string alias, string column) => $"CONVERT(varchar(18), CAST({Prefix(alias)}{Quote(column)} AS binary(8)), 1)";

    public string TokenMatch(string alias, string column, string valueExpr) => $"{Prefix(alias)}{Quote(column)} = {valueExpr}";

    public string? TokenType => "binary(8)";

    public string NullSafeEquals(string left, string right) => $"({left} = {right} OR ({left} IS NULL AND {right} IS NULL))";

    public string ValueText(string expr, string? columnType) => TypeSpec.Parse(columnType).Name switch
    {
        "datetime" or "smalldatetime" => $"CONVERT(varchar(23), {expr}, 121)",
        "datetime2" or "datetimeoffset" or "date" or "time" => $"CONVERT(varchar(40), {expr}, 121)",
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => $"CONVERT(varchar(max), CAST({expr} AS varbinary(max)), 1)",
        "float" or "real" => $"CONVERT(varchar(30), {expr}, 3)",
        "geography" or "geometry" or "hierarchyid" => $"{expr}.ToString()",
        "text" => $"CAST({expr} AS varchar(max))",
        _ => $"CAST({expr} AS nvarchar(max))",
    };

    public string? LockReason(SnapColumn column) => TypeSpec.Parse(column.Type).Name switch
    {
        "timestamp" or "rowversion" => "rowversion columns are set by the server",
        "geography" or "geometry" or "hierarchyid" => $"{column.Type} values cannot be edited in the grid",
        "sql_variant" => "sql_variant values cannot be edited in the grid (the base type is unknown)",
        _ => null,
    };

    public bool Comparable(string? columnType)
    {
        var t = TypeSpec.Parse(columnType);
        return t.Name switch
        {
            "float" or "real" or "text" or "ntext" or "image" or "xml" or "binary" or "varbinary" or "sql_variant"
                or "geography" or "geometry" or "hierarchyid" or "timestamp" or "rowversion" => false,
            // Arrow carries microseconds: the 7th fractional digit is not in the grid.
            "datetime2" or "time" or "datetimeoffset" => t.TimeScale <= 6,
            _ => true,
        };
    }

    public bool ExactKey(string? columnType)
    {
        var t = TypeSpec.Parse(columnType);
        return t.Name switch
        {
            "float" or "real" or "sql_variant" or "geography" or "geometry" or "hierarchyid" or "xml" or "text" or "ntext" or "image" => false,
            "datetime2" or "time" or "datetimeoffset" => t.TimeScale <= 6,
            "binary" or "varbinary" => !t.Max && (t.A ?? 8000) <= 64,
            _ => true,
        };
    }

    public async Task<long> ExecuteCountedAsync(DbCommand command, CancellationToken ct)
    {
        command.CommandText += "\nSELECT CAST(@@ROWCOUNT AS bigint);";
        long count = 0;
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // The count is the last result: a trigger may return its own result sets before it.
        do
        {
            if (r.FieldCount == 1 && r.GetFieldType(0) == typeof(long))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) count = r.GetInt64(0);
            else
                while (await r.ReadAsync(ct).ConfigureAwait(false)) { }
        } while (await r.NextResultAsync(ct).ConfigureAwait(false));
        return count;
    }

    public string InsertReturningKey(string table, IReadOnlyList<(string Column, string ValueExpr)> columns, IReadOnlyList<(string Column, string? Type, bool Generated)> key)
    {
        var sb = new StringBuilder();
        sb.Append("DECLARE @q_key TABLE (");
        sb.Append(string.Join(", ", key.Select((k, i) => $"k{i} {KeyVariableType(k.Type)}")));
        sb.Append(");\nINSERT INTO ").Append(table);
        if (columns.Count > 0) sb.Append(" (").Append(string.Join(", ", columns.Select(c => Quote(c.Column)))).Append(')');
        sb.Append(" OUTPUT ").Append(string.Join(", ", key.Select(k => "INSERTED." + Quote(k.Column)))).Append(" INTO @q_key");
        if (columns.Count > 0) sb.Append(" VALUES (").Append(string.Join(", ", columns.Select(c => c.ValueExpr))).Append(')');
        else sb.Append(" DEFAULT VALUES");
        sb.Append(";\nSELECT ").Append(string.Join(", ", key.Select((k, i) => ValueText($"k{i}", k.Type)))).Append(" FROM @q_key;");
        return sb.ToString();
    }

    /// <summary>A table-variable column type for a key column (alias types are stored by their base name).</summary>
    static string KeyVariableType(string? type)
    {
        var t = TypeSpec.Parse(type);
        return t.Name is "timestamp" or "rowversion" ? "binary(8)" : (type ?? "nvarchar(max)");
    }

    public string PreviewLiteral(string? columnType, GridValue value)
    {
        if (value.UseDefault) return "DEFAULT";
        if (value.IsNull || value.Text == null) return "NULL";
        var t = TypeSpec.Parse(columnType);
        return t.Name switch
        {
            "bigint" or "int" or "smallint" or "tinyint" or "bit" or "decimal" or "numeric" or "money" or "smallmoney"
                when IsNumber(value.Text, exponent: false) => value.Text.Trim(),
            // The server reads an exact numeric only in plain form ('1E5' fails as varchar to numeric): written out exactly.
            "decimal" or "numeric" or "money" or "smallmoney" when PlainDecimal(value.Text) is { } plain => plain,
            "float" or "real" when IsNumber(value.Text, exponent: true) => value.Text.Trim(),
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" when IsHex(value.Text) => value.Text.Trim(),
            "nchar" or "nvarchar" or "ntext" or "xml" => "N'" + value.Text.Replace("'", "''") + "'",
            _ => "'" + value.Text.Replace("'", "''") + "'",
        };
    }

    /// <summary>
    /// A number in exponent form ("1E5", "-2.50e-3") as plain decimal text ("100000", "-0.00250"): the digits shifted one by
    /// one, never through a double, so it is exact. Null when the text has no exponent, is not such a number, or its
    /// exponent is past what any decimal holds (|e| over 100: left to the server to refuse).
    /// </summary>
    internal static string? PlainDecimal(string text)
    {
        var s = text.Trim();
        var e = s.IndexOfAny(['e', 'E']);
        if (e < 0 || !IsNumber(s, exponent: true)) return null;
        if (!int.TryParse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exp) || Math.Abs(exp) > 100) return null;
        var mantissa = s[..e];
        var sign = mantissa.StartsWith('-') ? "-" : "";
        mantissa = mantissa.TrimStart('+', '-');
        var dot = mantissa.IndexOf('.');
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var point = (dot < 0 ? mantissa.Length : dot) + exp; // where the decimal point falls among the digits
        if (point <= 0)
        {
            digits = new string('0', 1 - point) + digits;
            point = 1;
        }
        else if (point > digits.Length) digits += new string('0', point - digits.Length);
        var whole = digits[..point].TrimStart('0');
        var fraction = digits[point..];
        return sign + (whole.Length == 0 ? "0" : whole) + (fraction.Length > 0 ? "." + fraction : "");
    }

    /// <summary>
    /// A number written bare into the preview (L7): an optional sign, ASCII digits, an optional fraction and, for float /
    /// real, an exponent. Anything else — NaN, Infinity, ∞, other digit scripts, thousands separators — stays a quoted
    /// literal, which the server converts or refuses; a parse such as double.TryParse would accept NaN and emit it bare.
    /// </summary>
    internal static bool IsNumber(string text, bool exponent)
    {
        var s = text.Trim();
        var i = 0;
        if (i < s.Length && s[i] is '+' or '-') i++;
        var digits = Digits(s, ref i);
        if (i < s.Length && s[i] == '.')
        {
            i++;
            digits += Digits(s, ref i);
        }
        if (digits == 0) return false;
        if (exponent && i < s.Length && s[i] is 'e' or 'E')
        {
            i++;
            if (i < s.Length && s[i] is '+' or '-') i++;
            if (Digits(s, ref i) == 0) return false;
        }
        return i == s.Length;

        static int Digits(string s, ref int i)
        {
            var start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            return i - start;
        }
    }

    static bool IsHex(string s)
    {
        var t = s.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && t.Length > 2 && t[2..].All(Uri.IsHexDigit);
    }

    public string PreviewTerminator => ";";

    public string? ConcurrencyNote(string? tokenKind) => tokenKind == "rowversion"
        ? "Rows are matched on their key and rowversion: a row someone else changed since it was read is reported as a conflict, not overwritten."
        : "This table has no rowversion column: the original values of the changed columns are compared (float, binary, LOB and xml columns, and the 7th fractional digit of time values, are not checked).";
}
