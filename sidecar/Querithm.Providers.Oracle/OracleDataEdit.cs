using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>What Querithm does with one Oracle column type in the grid and in an import.</summary>
internal enum OracleValueKind
{
    Number, BinaryFloat, BinaryDouble, Date, Timestamp, TimestampLtz, TimestampTz, IntervalYm, IntervalDs,
    Char, NChar, VarChar, NVarChar, Raw, Boolean, RowId,
    Clob, Blob, BFile, Long, LongRaw, Xml, Json, Vector, Object,
}

/// <summary>
/// An Oracle catalog type as OracleMetadata writes it, taken apart: <c>NUMBER(12,2)</c>, <c>VARCHAR2(40 CHAR)</c>,
/// <c>TIMESTAMP(6) WITH TIME ZONE</c>, <c>INTERVAL DAY(2) TO SECOND(6)</c>, <c>INTEGER</c>, <c>OWNER.TYPE</c>; an
/// invisible column's trailing " invisible" is ignored.
/// </summary>
internal readonly record struct OracleColumnType(OracleValueKind Kind, string Name, int? Precision, int? Scale, int? Length, bool CharSemantics)
{
    public static OracleColumnType Parse(string? type)
    {
        var t = (type ?? "").Trim();
        if (t.EndsWith(" invisible", StringComparison.Ordinal)) t = t[..^" invisible".Length].TrimEnd();
        var upper = t.ToUpperInvariant();
        // The first argument list holds the size; INTERVAL DAY(p) TO SECOND(s) has two.
        var groups = new List<string>();
        var bare = new StringBuilder();
        for (var i = 0; i < upper.Length; i++)
        {
            if (upper[i] == '(')
            {
                var close = upper.IndexOf(')', i);
                if (close < 0) break;
                groups.Add(upper[(i + 1)..close]);
                i = close;
                continue;
            }
            bare.Append(upper[i]);
        }
        var name = string.Join(' ', bare.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int? Arg(int group, int index)
        {
            if (group >= groups.Count) return null;
            var parts = groups[group].Split(',', StringSplitOptions.TrimEntries);
            if (index >= parts.Length) return null;
            var digits = new string(parts[index].TakeWhile(c => char.IsAsciiDigit(c) || c == '-').ToArray());
            return int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        var charSemantics = groups.Count > 0 && groups[0].Contains("CHAR", StringComparison.Ordinal);
        var kind = name switch
        {
            "NUMBER" or "FLOAT" or "DECIMAL" or "NUMERIC" or "DEC" or "REAL" or "DOUBLE PRECISION" or "SMALLINT" or "INT" => OracleValueKind.Number,
            "INTEGER" => OracleValueKind.Number,
            "BINARY_FLOAT" => OracleValueKind.BinaryFloat,
            "BINARY_DOUBLE" => OracleValueKind.BinaryDouble,
            "DATE" => OracleValueKind.Date,
            "TIMESTAMP" => OracleValueKind.Timestamp,
            "TIMESTAMP WITH LOCAL TIME ZONE" => OracleValueKind.TimestampLtz,
            "TIMESTAMP WITH TIME ZONE" => OracleValueKind.TimestampTz,
            "INTERVAL YEAR TO MONTH" => OracleValueKind.IntervalYm,
            "INTERVAL DAY TO SECOND" => OracleValueKind.IntervalDs,
            "CHAR" or "CHARACTER" => OracleValueKind.Char,
            "NCHAR" => OracleValueKind.NChar,
            "VARCHAR2" or "VARCHAR" => OracleValueKind.VarChar,
            "NVARCHAR2" => OracleValueKind.NVarChar,
            "RAW" => OracleValueKind.Raw,
            "BOOLEAN" => OracleValueKind.Boolean,
            "ROWID" or "UROWID" => OracleValueKind.RowId,
            "CLOB" or "NCLOB" => OracleValueKind.Clob,
            "BLOB" => OracleValueKind.Blob,
            "BFILE" => OracleValueKind.BFile,
            "LONG" => OracleValueKind.Long,
            "LONG RAW" => OracleValueKind.LongRaw,
            "XMLTYPE" or "SYS.XMLTYPE" => OracleValueKind.Xml,
            "JSON" => OracleValueKind.Json,
            "VECTOR" => OracleValueKind.Vector,
            _ => OracleValueKind.Object,
        };
        return kind switch
        {
            // INTEGER is NUMBER(*,0): no precision, scale 0.
            OracleValueKind.Number when name == "INTEGER" => new(kind, name, null, 0, null, false),
            OracleValueKind.Number when name == "FLOAT" => new(kind, name, Arg(0, 0), null, null, false),
            OracleValueKind.Number => new(kind, name, Arg(0, 0), Arg(0, 0) is null ? null : Arg(0, 1) ?? 0, null, false),
            OracleValueKind.Timestamp or OracleValueKind.TimestampLtz or OracleValueKind.TimestampTz => new(kind, name, null, Arg(0, 0) ?? 6, null, false),
            OracleValueKind.IntervalDs => new(kind, name, Arg(0, 0) ?? 2, Arg(1, 0) ?? 6, null, false),
            OracleValueKind.IntervalYm => new(kind, name, Arg(0, 0) ?? 2, null, null, false),
            OracleValueKind.Char or OracleValueKind.NChar => new(kind, name, null, null, Arg(0, 0) ?? 1, charSemantics || kind == OracleValueKind.NChar),
            OracleValueKind.VarChar or OracleValueKind.NVarChar or OracleValueKind.Raw => new(kind, name, null, null, Arg(0, 0), charSemantics || kind == OracleValueKind.NVarChar),
            _ => new(kind, name, null, null, null, false),
        };
    }

    public bool IsText => Kind is OracleValueKind.Char or OracleValueKind.NChar or OracleValueKind.VarChar or OracleValueKind.NVarChar;
    public bool IsNational => Kind is OracleValueKind.NChar or OracleValueKind.NVarChar;

    /// <summary>Kinds the grid only shows (in part, or as text it cannot write back).</summary>
    public bool IsLocked => Kind is OracleValueKind.Clob or OracleValueKind.Blob or OracleValueKind.BFile or OracleValueKind.Long or OracleValueKind.LongRaw
        or OracleValueKind.Xml or OracleValueKind.Json or OracleValueKind.Vector or OracleValueKind.Object;
}

/// <summary>
/// Grid edits on Oracle (D4 S10). Values travel as typed ODP.NET parameters bound by name (<c>:p0</c>): the grid's text is
/// converted here — NUMBER through OracleDecimal (exact, 38 digits, exponent forms expanded), DATE / TIMESTAMP from
/// <c>YYYY-MM-DD HH24:MI:SS[.F…]</c> (never through NLS_DATE_FORMAT), RAW from 0x…, CHAR as CHAR (blank-padded compare) —
/// and a value the column cannot take is refused before anything is sent. There is no row version Querithm relies on:
/// ORA_ROWSCN is kept per block unless the table was created with ROWDEPENDENCIES, so a change to another row of the same
/// block would look like a conflict, and a commit can leave it unchanged for the row read. Lost updates are found by
/// comparing the original values of the changed columns with <c>DECODE</c>, which matches NULL to NULL (an empty string
/// is NULL in Oracle, so <c>''</c> and NULL compare equal). Every UPDATE / DELETE must change exactly one row, or the whole
/// patch rolls back (Core). Without a proven key the row's ROWID stands in when the result reads it and the table is an
/// ordinary heap table with row movement disabled (<see cref="RowAddressRefusal"/>). LOB, LONG, BFILE, XMLTYPE, JSON,
/// VECTOR and object columns are read-only per column with the reason.
/// </summary>
public sealed class OracleDataEdit : IDataEditDialect
{
    public string Quote(string identifier) => OracleIdentifiers.Quote(identifier);
    public string Placeholder(int index) => ":p" + index.ToString(CultureInfo.InvariantCulture);
    public string TypedPlaceholder(int index, string? columnType) => Placeholder(index);

    public void AddValue(DbCommand command, int index, string? columnType, GridValue value)
    {
        if (command is OracleCommand oc) oc.BindByName = true;
        var t = OracleColumnType.Parse(columnType);
        var (type, v) = value.IsNull || value.Text == null ? (DbTypeOf(t), DBNull.Value) : ToParameter(t, value.Text);
        command.Parameters.Add(new OracleParameter("p" + index.ToString(CultureInfo.InvariantCulture), type, v, System.Data.ParameterDirection.Input));
    }

    /// <summary>The driver type a NULL of this column binds as (a UNION ALL relation needs every row of a column alike).</summary>
    internal static OracleDbType DbTypeOf(OracleColumnType t) => t.Kind switch
    {
        OracleValueKind.Number => OracleDbType.Decimal,
        OracleValueKind.BinaryFloat => OracleDbType.BinaryFloat,
        OracleValueKind.BinaryDouble => OracleDbType.BinaryDouble,
        OracleValueKind.Date => OracleDbType.Date,
        OracleValueKind.Timestamp => OracleDbType.TimeStamp,
        OracleValueKind.TimestampLtz => OracleDbType.TimeStampLTZ,
        OracleValueKind.TimestampTz => OracleDbType.TimeStampTZ,
        OracleValueKind.IntervalYm => OracleDbType.IntervalYM,
        OracleValueKind.IntervalDs => OracleDbType.IntervalDS,
        OracleValueKind.Char => OracleDbType.Char,
        OracleValueKind.NChar => OracleDbType.NChar,
        OracleValueKind.NVarChar => OracleDbType.NVarchar2,
        OracleValueKind.Raw => OracleDbType.Raw,
        OracleValueKind.Boolean => OracleDbType.Boolean,
        OracleValueKind.Clob => OracleDbType.Clob,
        _ => OracleDbType.Varchar2,
    };

    /// <summary>The grid's (or a file's) text as the parameter the column takes; <see cref="FormatException"/> when it is not a valid value.</summary>
    internal static (OracleDbType Type, object Value) ToParameter(OracleColumnType t, string text)
    {
        var s = text.Trim();
        switch (t.Kind)
        {
            case OracleValueKind.Number:
                // NUMBER(1) is how an Oracle table without BOOLEAN keeps a flag (an import's true / false column lands there).
                if (t is { Precision: 1, Scale: 0 } && ValueClassifier.IsBool(s)) return (OracleDbType.Decimal, s.Equals("true", StringComparison.OrdinalIgnoreCase) ? OracleDecimal.One : OracleDecimal.Zero);
                return (OracleDbType.Decimal, Number(t, s, text));
            case OracleValueKind.BinaryFloat:
                return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? (OracleDbType.BinaryFloat, f) : throw new FormatException($"'{Clip(text)}' is not a number.");
            case OracleValueKind.BinaryDouble:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (OracleDbType.BinaryDouble, d) : throw new FormatException($"'{Clip(text)}' is not a number.");
            case OracleValueKind.Date:
            {
                var p = Stamp(s, text, "a date (yyyy-mm-dd hh:mi:ss)");
                if (p.Nanos != 0) throw new FormatException($"'{Clip(text)}' has a fraction of a second, which DATE does not keep (use a TIMESTAMP column).");
                return (OracleDbType.Date, Checked(() => new OracleDate(p.Year, p.Month, p.Day, p.Hour, p.Minute, p.Second), text, "a date"));
            }
            case OracleValueKind.Timestamp:
            {
                var p = Stamp(s, text, "a timestamp (yyyy-mm-dd hh:mi:ss.ff)");
                return (OracleDbType.TimeStamp, Checked(() => new OracleTimeStamp(p.Year, p.Month, p.Day, p.Hour, p.Minute, p.Second, p.Nanos), text, "a timestamp"));
            }
            case OracleValueKind.TimestampLtz:
            {
                var p = Stamp(s, text, "a timestamp (yyyy-mm-dd hh:mi:ss.ff)");
                return (OracleDbType.TimeStampLTZ, Checked(() => new OracleTimeStampLTZ(p.Year, p.Month, p.Day, p.Hour, p.Minute, p.Second, p.Nanos), text, "a timestamp"));
            }
            case OracleValueKind.TimestampTz:
            {
                // "2026-01-02 03:04:05.123456 +02:00" or "… Europe/Paris" (the grid's text); a bare timestamp takes the session zone.
                var space = s.LastIndexOf(' ');
                var zoned = space > 10 && (s[space + 1] is '+' or '-' || char.IsAsciiLetter(s[space + 1]));
                var p = Stamp(zoned ? s[..space] : s, text, "a timestamp with a time zone (yyyy-mm-dd hh:mi:ss.ff +hh:mi)");
                return (OracleDbType.TimeStampTZ, Checked(() => zoned
                    ? new OracleTimeStampTZ(p.Year, p.Month, p.Day, p.Hour, p.Minute, p.Second, p.Nanos, s[(space + 1)..])
                    : new OracleTimeStampTZ(p.Year, p.Month, p.Day, p.Hour, p.Minute, p.Second, p.Nanos), text, "a timestamp with a time zone"));
            }
            case OracleValueKind.IntervalYm:
                return (OracleDbType.IntervalYM, Checked(() => new OracleIntervalYM(s), text, "a year-to-month interval (+yy-mm)"));
            case OracleValueKind.IntervalDs:
                return (OracleDbType.IntervalDS, Checked(() => new OracleIntervalDS(s), text, "a day-to-second interval (+dd hh:mi:ss.ff)"));
            case OracleValueKind.Raw:
            {
                if (text.EndsWith('…')) throw new FormatException("The binary value is shortened in the grid.");
                if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || (s.Length - 2) % 2 != 0)
                    throw new FormatException($"'{Clip(text)}' is not a binary value (write 0x followed by pairs of hex digits).");
                byte[] bytes;
                try { bytes = Convert.FromHexString(s.AsSpan(2)); }
                catch (FormatException) { throw new FormatException($"'{Clip(text)}' is not a binary value (write 0x followed by pairs of hex digits)."); }
                if (t.Length is { } n && bytes.Length > n) throw new FormatException($"The value is {bytes.Length:N0} bytes; RAW({n}) holds {n:N0}.");
                return (OracleDbType.Raw, bytes);
            }
            case OracleValueKind.Boolean:
                if (ValueClassifier.IsBool(s)) return (OracleDbType.Boolean, s.Equals("true", StringComparison.OrdinalIgnoreCase));
                if (s is "1" or "0") return (OracleDbType.Boolean, s == "1");
                throw new FormatException($"'{Clip(text)}' is not a boolean (true or false).");
            case OracleValueKind.Char or OracleValueKind.NChar or OracleValueKind.VarChar or OracleValueKind.NVarChar:
            {
                if (t.Length is { } max)
                {
                    var size = t.CharSemantics ? text.EnumerateRunes().Count() : Encoding.UTF8.GetByteCount(text);
                    // CHAR values come back blank-padded: the padding itself always fits.
                    if (size > max && !(t.Kind is OracleValueKind.Char or OracleValueKind.NChar && text.TrimEnd(' ').Length <= max))
                        throw new FormatException($"The value is {size:N0} {(t.CharSemantics ? "characters" : "bytes")}; {t.Name}({max}{(t.CharSemantics && !t.IsNational ? " CHAR" : t.IsNational ? "" : " BYTE")}) holds {max:N0}.");
                }
                return (DbTypeOf(t), text);
            }
            case OracleValueKind.Clob:
                return (OracleDbType.Clob, text);
            default:
                return (OracleDbType.Varchar2, text);
        }
    }

    static T Checked<T>(Func<T> make, string text, string what)
    {
        try { return make(); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or OracleTypeException or InvalidCastException)
        {
            throw new FormatException($"'{Clip(text)}' is not {what}.");
        }
    }

    /// <summary>
    /// [sign] digits [. digits] [e [sign] digits] as an exact OracleDecimal (the exponent is applied to the digits here:
    /// OracleDecimal.Parse reads the point whatever the culture, but no exponent). NUMBER(p,s) holds p − s digits before the point.
    /// </summary>
    internal static OracleDecimal Number(OracleColumnType t, string s, string text)
    {
        var plain = PlainNumber(s) ?? throw new FormatException($"'{Clip(text)}' is not a number.");
        var unsigned = plain.TrimStart('-');
        var point = unsigned.IndexOf('.');
        var intDigits = (point < 0 ? unsigned : unsigned[..point]).TrimStart('0').Length;
        if (t.Precision is { } p && t.Scale is { } sc && sc >= 0 && intDigits > p - sc)
            throw new FormatException($"'{Clip(text)}' has {intDigits} digit(s) before the point; {t.Name}({p}{(sc != 0 ? $",{sc}" : "")}) holds {Math.Max(0, p - sc)}.");
        var significant = unsigned.Replace(".", "").TrimStart('0').TrimEnd('0').Length;
        if (significant > 40 || intDigits > 126) throw new FormatException($"'{Clip(text)}' is beyond what an Oracle NUMBER holds (38 significant digits, up to 10^126).");
        try { return OracleDecimal.Parse(plain); }
        catch (Exception ex) when (ex is OracleTypeException or FormatException or OverflowException) { throw new FormatException($"'{Clip(text)}' is not a number Oracle can hold."); }
    }

    /// <summary>The number as plain decimal text (no exponent, no '+', no needless zeros), or null when it is not one.</summary>
    internal static string? PlainNumber(string s)
    {
        var i = 0;
        var negative = false;
        if (i < s.Length && s[i] is '+' or '-') negative = s[i++] == '-';
        var digits = new StringBuilder();
        var scale = 0;
        var any = false;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { digits.Append(s[i++]); any = true; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { digits.Append(s[i++]); scale++; any = true; }
        }
        if (!any) return null;
        var exp = 0;
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            i++;
            var expNeg = false;
            if (i < s.Length && s[i] is '+' or '-') expNeg = s[i++] == '-';
            var start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]) && i - start < 4) i++;
            if (i == start || !int.TryParse(s.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out exp)) return null;
            if (expNeg) exp = -exp;
        }
        if (i != s.Length) return null;
        scale -= exp;
        var all = digits.ToString();
        if (scale < 0) { all += new string('0', -scale); scale = 0; }
        if (all.Length <= scale) all = new string('0', scale - all.Length + 1) + all;
        var intPart = all[..^scale].TrimStart('0');
        var frac = scale > 0 ? all[^scale..].TrimEnd('0') : "";
        if (intPart.Length == 0) intPart = "0";
        var text = frac.Length > 0 ? intPart + "." + frac : intPart;
        return negative && text != "0" ? "-" + text : text;
    }

    internal readonly record struct StampParts(int Year, int Month, int Day, int Hour, int Minute, int Second, int Nanos);

    /// <summary>[-]YYYY-MM-DD[( |T)HH24:MI[:SS[.F…]]]: Oracle's year numbering (−4712 … 9999), at most nine fractional digits.</summary>
    internal static StampParts Stamp(string s, string text, string what)
    {
        var bad = new FormatException($"'{Clip(text)}' is not {what}.");
        var i = 0;
        var negative = s.StartsWith('-');
        if (negative) i++;
        int Digits(int n)
        {
            if (i + n > s.Length) throw bad;
            var v = 0;
            for (var k = 0; k < n; k++)
            {
                if (!char.IsAsciiDigit(s[i + k])) throw bad;
                v = v * 10 + (s[i + k] - '0');
            }
            i += n;
            return v;
        }
        void Expect(char c)
        {
            if (i >= s.Length || s[i] != c) throw bad;
            i++;
        }
        var year = Digits(4);
        Expect('-');
        var month = Digits(2);
        Expect('-');
        var day = Digits(2);
        int hour = 0, minute = 0, second = 0, nanos = 0;
        if (i < s.Length)
        {
            if (s[i] is not (' ' or 'T')) throw bad;
            i++;
            hour = Digits(2);
            Expect(':');
            minute = Digits(2);
            if (i < s.Length)
            {
                Expect(':');
                second = Digits(2);
                if (i < s.Length)
                {
                    Expect('.');
                    var start = i;
                    while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                    var frac = s[start..i];
                    if (frac.Length is 0 or > 9 || i != s.Length) throw bad;
                    nanos = int.Parse(frac.PadRight(9, '0'), CultureInfo.InvariantCulture);
                }
            }
        }
        if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59 || year == 0) throw bad;
        return new StampParts(negative ? -year : year, month, day, hour, minute, second, nanos);
    }

    static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    public (string? Kind, string? Column) Token(SnapObject table) => (null, null);
    public string TokenText(string alias, string column) => throw new NotSupportedException("Querithm keeps no row version for Oracle tables (ORA_ROWSCN is per block).");
    public string TokenMatch(string alias, string column, string valueExpr) => throw new NotSupportedException("Querithm keeps no row version for Oracle tables (ORA_ROWSCN is per block).");
    public string? TokenType => null;

    /// <summary>The date part of every text form and literal: a signed year (SYYYY), so BC dates keep their era.</summary>
    const string DateFormat = "SYYYY-MM-DD HH24:MI:SS";

    /// <summary>DECODE matches NULL to NULL (and so <c>''</c> to NULL, which Oracle stores the same).</summary>
    public string NullSafeEquals(string left, string right) => $"DECODE({left}, {right}, 1, 0) = 1";

    /// <summary>
    /// The grid's text forms: NUMBER exact with a point, DATE / TIMESTAMP as [-]YYYY-MM-DD HH24:MI:SS[.FF], RAW as 0x + hex;
    /// what the grid cannot show as text is named. The year is signed (SYYYY, Oracle's numbering: -0100 is 100 BC), so a
    /// BC value never reads as the same AD one; LTRIM drops the blank SYYYY puts before an AD year.
    /// </summary>
    public string ValueText(string expr, string? columnType)
    {
        var t = OracleColumnType.Parse(columnType);
        string Text(string format) => $"LTRIM(TO_CHAR({expr}, '{format}'))";
        return t.Kind switch
        {
            OracleValueKind.Number or OracleValueKind.BinaryFloat or OracleValueKind.BinaryDouble => $"TO_CHAR({expr}, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')",
            OracleValueKind.Date => Text(DateFormat),
            OracleValueKind.Timestamp or OracleValueKind.TimestampLtz => Text(t.Scale is 0 ? DateFormat : $"{DateFormat}.FF{t.Scale ?? 6}"),
            OracleValueKind.TimestampTz => Text(t.Scale is 0 ? $"{DateFormat} TZR" : $"{DateFormat}.FF{t.Scale ?? 6} TZR"),
            OracleValueKind.Raw => $"'0x' || RAWTOHEX({expr})",
            OracleValueKind.Boolean => $"CASE WHEN {expr} THEN 'true' WHEN NOT {expr} THEN 'false' END",
            OracleValueKind.RowId => $"ROWIDTOCHAR({expr})",
            OracleValueKind.IntervalYm or OracleValueKind.IntervalDs => $"TO_CHAR({expr})",
            OracleValueKind.Char or OracleValueKind.NChar or OracleValueKind.VarChar or OracleValueKind.NVarChar => expr,
            OracleValueKind.Clob => $"DBMS_LOB.SUBSTR({expr}, 1000, 1)",
            _ => $"'({t.Name} value not shown)'",
        };
    }

    public string? LockReason(SnapColumn column)
    {
        if (column.Name == OracleResultSources.RowId && column.Type == OracleResultSources.RowId)
            return "the row's address (ROWID) is kept by the server";
        var t = OracleColumnType.Parse(column.Type);
        return t.Kind switch
        {
            OracleValueKind.Clob or OracleValueKind.Blob => $"{t.Name} values are read in part in the grid: change them with an UPDATE statement",
            OracleValueKind.Long or OracleValueKind.LongRaw => $"{t.Name} values cannot be compared or matched: change them with an UPDATE statement",
            OracleValueKind.BFile => "a BFILE points to a file on the server: change it with an UPDATE statement (BFILENAME)",
            OracleValueKind.Xml or OracleValueKind.Json or OracleValueKind.Vector => $"{t.Name} values are shown as text the grid cannot write back: change them with an UPDATE statement",
            OracleValueKind.Object => $"{column.Type} is an object or collection type, which Querithm does not read: change it with an UPDATE statement",
            _ => null,
        };
    }

    /// <summary>BINARY_FLOAT / BINARY_DOUBLE do not round-trip as text; locked kinds are never compared; RAW only while the grid shows it whole.</summary>
    public bool Comparable(string? columnType)
    {
        var t = OracleColumnType.Parse(columnType);
        if (t.IsLocked) return false;
        if (t.Kind == OracleValueKind.Raw) return t.Length is <= 64;
        return t.Kind is not (OracleValueKind.BinaryFloat or OracleValueKind.BinaryDouble);
    }

    public bool ExactKey(string? columnType) => Comparable(columnType);

    public async Task<long> ExecuteCountedAsync(DbCommand command, CancellationToken ct) => await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Oracle's RETURNING goes into variables, not a result set: an anonymous block inserts, keeps the new row's ROWID and
    /// returns the key columns as text through an implicit result (DBMS_SQL.RETURN_RESULT) — which also covers an identity
    /// key, a trigger-filled key and the ROWID key itself. Oracle has no DEFAULT VALUES: with no column given, the first real
    /// key column takes its DEFAULT.
    /// </summary>
    public string InsertReturningKey(string table, IReadOnlyList<(string Column, string ValueExpr)> columns, IReadOnlyList<(string Column, string? Type, bool Generated)> key)
    {
        IReadOnlyList<(string Column, string ValueExpr)> cols = columns;
        if (cols.Count == 0)
        {
            var first = key.FirstOrDefault(k => k.Column != OracleResultSources.RowId).Column
                        ?? throw new InvalidOperationException("Oracle has no DEFAULT VALUES: give at least one column of the new row a value.");
            cols = [(first, "DEFAULT")];
        }
        var keyText = string.Join(", ", key.Select(k => k.Column == OracleResultSources.RowId && k.Type == OracleResultSources.RowId
            ? "ROWIDTOCHAR(ROWID)" : ValueText(Quote(k.Column), k.Type)));
        return $"""
            DECLARE
              q$rid UROWID;
              q$key SYS_REFCURSOR;
            BEGIN
              INSERT INTO {table} ({string.Join(", ", cols.Select(c => Quote(c.Column)))}) VALUES ({string.Join(", ", cols.Select(c => c.ValueExpr))}) RETURNING ROWID INTO q$rid;
              OPEN q$key FOR SELECT {keyText} FROM {table} WHERE ROWID = q$rid;
              DBMS_SQL.RETURN_RESULT(q$key);
            END;
            """;
    }

    public string PreviewLiteral(string? columnType, GridValue value)
    {
        if (value.UseDefault) return "DEFAULT";
        if (value.IsNull || value.Text == null) return "NULL";
        var t = OracleColumnType.Parse(columnType);
        try
        {
            var (_, v) = ToParameter(t, value.Text);
            return (t.Kind, v) switch
            {
                (OracleValueKind.Number, OracleDecimal d) => d.ToString(),
                (OracleValueKind.BinaryFloat or OracleValueKind.BinaryDouble, _) => PlainNumber(value.Text.Trim()) ?? Literal(value.Text),
                (OracleValueKind.Date, _) => $"TO_DATE({Literal(value.Text.Trim().Replace('T', ' '))}, '{DateFormat}')",
                (OracleValueKind.Timestamp, _) => $"TIMESTAMP {Literal(value.Text.Trim().Replace('T', ' '))}",
                (OracleValueKind.TimestampTz, _) => $"TIMESTAMP {Literal(value.Text.Trim().Replace('T', ' '))}",
                (OracleValueKind.TimestampLtz, _) => $"CAST(TIMESTAMP {Literal(value.Text.Trim().Replace('T', ' '))} AS TIMESTAMP WITH LOCAL TIME ZONE)",
                (OracleValueKind.IntervalYm, _) => $"TO_YMINTERVAL({Literal(value.Text.Trim())})",
                (OracleValueKind.IntervalDs, _) => $"TO_DSINTERVAL({Literal(value.Text.Trim())})",
                (OracleValueKind.Raw, byte[] b) => $"HEXTORAW('{Convert.ToHexString(b)}')",
                (OracleValueKind.Boolean, bool b) => b ? "TRUE" : "FALSE",
                (OracleValueKind.NChar or OracleValueKind.NVarChar, _) => "N" + Literal(value.Text),
                _ => Literal(value.Text),
            };
        }
        catch (FormatException) { return Literal(value.Text); } // the preview shows it; applying reports the error
    }

    /// <summary>Oracle string literals have no backslash escapes: only the quote is doubled.</summary>
    static string Literal(string s) => "'" + s.Replace("'", "''") + "'";

    public string PreviewTerminator => ";";

    public string? ConcurrencyNote(string? tokenKind) => tokenKind == null
        ? "Oracle keeps no row version Querithm can rely on (ORA_ROWSCN is per block unless the table was created with ROWDEPENDENCIES): concurrent changes are found by comparing the original values of the changed columns (an empty string is NULL in Oracle and compares as NULL)."
        : null;

    /// <summary>Oracle (before 23ai) has no VALUES derived table and no AS before a table alias: a UNION ALL of SELECT … FROM DUAL.</summary>
    public string ValuesRelation(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<string> names, string alias) =>
        "(" + string.Join(" UNION ALL ", rows.Select((r, i) => "SELECT " + string.Join(", ", r.Select((e, k) => i == 0 ? $"{e} AS {names[k]}" : e)) + " FROM DUAL")) + $") {alias}";

    public string? RowAddress => OracleResultSources.RowId;

    public string? RowAddressRefusal(SnapObject table, string? rowAddress) => rowAddress switch
    {
        "heap" => null,
        "iot" => "it is an index-organized table, whose ROWIDs are logical and follow the primary key",
        "moves" => "row movement is enabled, so an update of the partition key, a shrink or a flashback can give a row a new ROWID",
        _ => "the cached metadata does not say whether its ROWIDs stay put (refresh the explorer)",
    };
}
