using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>How one Oracle column travels (D4 S04); <see cref="OracleTypes.Map"/> decides, <see cref="OracleTypes.ReadAsString"/> reads the text kinds.</summary>
internal enum OracleKind
{
    /// <summary>The default mapping (BINARY_FLOAT / BINARY_DOUBLE, BOOLEAN, RAW, LONG RAW).</summary>
    Plain,
    /// <summary>NUMBER(p,s) that fits Decimal128.</summary>
    Decimal,
    /// <summary>NUMBER / FLOAT without precision and scale, or beyond 38 digits: exact decimal text.</summary>
    NumberText,
    /// <summary>DATE, TIMESTAMP(0–6) and WITH LOCAL TIME ZONE(0–6): microseconds read from the components.</summary>
    Timestamp,
    /// <summary>TIMESTAMP(7–9) (and WITH LOCAL TIME ZONE): exact text with every fractional digit.</summary>
    TimestampText,
    /// <summary>TIMESTAMP WITH TIME ZONE: text that keeps the zone (region name or offset).</summary>
    TimestampTzText,
    IntervalYm,
    IntervalDs,
    /// <summary>VARCHAR2, CHAR, NVARCHAR2, NCHAR (also ROWID / UROWID, which ODP.NET reports as VARCHAR2).</summary>
    Text,
    /// <summary>CLOB / NCLOB: streamed in chunks.</summary>
    Clob,
    /// <summary>LONG, XMLTYPE, JSON: whole values (LONG is fetched inline), truncated by the encoder's text limit.</summary>
    LargeText,
    /// <summary>BLOB: streamed in chunks.</summary>
    Blob,
    Vector,
    BFile,
    /// <summary>Object, collection and REF types (SDO_GEOMETRY…) and cursor columns: ODP.NET reads none without a custom type mapping.</summary>
    Unreadable,
}

internal sealed record OracleColumn(OracleKind Kind, ColumnShape? Shape, int? Scale, string TypeName);

internal static class OracleTypes
{
    /// <summary>The kinds of the reader's current result set, for <see cref="ReadAsString"/>.</summary>
    static readonly ConditionalWeakTable<DbDataReader, OracleColumn[]> Columns = new();

    /// <summary>One column from the reader's schema table (ProviderType = OracleDbType, NumericPrecision / NumericScale, UdtTypeName).</summary>
    public static OracleColumn Map(OracleDbType type, int? precision, int? scale, int? size, string? udt)
    {
        switch (type)
        {
            case OracleDbType.Byte or OracleDbType.Int16 or OracleDbType.Int32 or OracleDbType.Int64 or OracleDbType.Decimal
                or OracleDbType.Double or OracleDbType.Single:
                return Number(precision, scale);
            case OracleDbType.BinaryFloat: return new(OracleKind.Plain, new ColumnShape(typeof(float), "BINARY_FLOAT"), null, "BINARY_FLOAT");
            case OracleDbType.BinaryDouble: return new(OracleKind.Plain, new ColumnShape(typeof(double), "BINARY_DOUBLE"), null, "BINARY_DOUBLE");
            case OracleDbType.Boolean: return new(OracleKind.Plain, new ColumnShape(typeof(bool), "BOOLEAN"), null, "BOOLEAN");
            case OracleDbType.Date:
                return new(OracleKind.Timestamp, new ColumnShape(typeof(DateTime), "DATE") { TimestampMicros = DateMicros }, 0, "DATE");
            case OracleDbType.TimeStamp or OracleDbType.TimeStampLTZ:
            {
                var s = scale is >= 0 and <= 9 ? scale.Value : 6;
                var name = $"TIMESTAMP({s})" + (type == OracleDbType.TimeStampLTZ ? " WITH LOCAL TIME ZONE" : "");
                return s <= 6
                    ? new(OracleKind.Timestamp, new ColumnShape(typeof(DateTime), name) { TimestampMicros = type == OracleDbType.TimeStamp ? TimestampMicros : LocalTimestampMicros }, s, name)
                    : new(OracleKind.TimestampText, new ColumnShape(typeof(string), name, ColumnHint.ProviderString), s, name);
            }
            case OracleDbType.TimeStampTZ:
            {
                var s = scale is >= 0 and <= 9 ? scale.Value : 6;
                var name = $"TIMESTAMP({s}) WITH TIME ZONE";
                return new(OracleKind.TimestampTzText, new ColumnShape(typeof(string), name, ColumnHint.ProviderString), s, name);
            }
            case OracleDbType.IntervalYM:
            {
                var name = $"INTERVAL YEAR({precision ?? 2}) TO MONTH";
                return new(OracleKind.IntervalYm, new ColumnShape(typeof(string), name, ColumnHint.ProviderString), null, name);
            }
            case OracleDbType.IntervalDS:
            {
                var name = $"INTERVAL DAY({precision ?? 2}) TO SECOND({scale ?? 6})";
                return new(OracleKind.IntervalDs, new ColumnShape(typeof(string), name, ColumnHint.ProviderString), null, name);
            }
            case OracleDbType.Varchar2 or OracleDbType.NVarchar2 or OracleDbType.Char or OracleDbType.NChar:
            {
                var name = type.ToString().ToUpperInvariant() + (size is > 0 ? $"({size})" : "");
                return new(OracleKind.Text, new ColumnShape(typeof(string), name), null, name);
            }
            case OracleDbType.Clob or OracleDbType.NClob:
            {
                var name = type.ToString().ToUpperInvariant();
                return new(OracleKind.Clob, new ColumnShape(typeof(string), name, ColumnHint.LargeText) { OpenText = OpenClob }, null, name);
            }
            case OracleDbType.Long: return new(OracleKind.LargeText, new ColumnShape(typeof(string), "LONG", ColumnHint.LargeText), null, "LONG");
            case OracleDbType.XmlType: return new(OracleKind.LargeText, new ColumnShape(typeof(string), "XMLTYPE", ColumnHint.LargeText), null, "XMLTYPE");
            case OracleDbType.Json: return new(OracleKind.LargeText, new ColumnShape(typeof(string), "JSON", ColumnHint.LargeText), null, "JSON");
            case OracleDbType.Raw: return new(OracleKind.Plain, new ColumnShape(typeof(byte[]), size is > 0 ? $"RAW({size})" : "RAW"), null, "RAW");
            case OracleDbType.LongRaw: return new(OracleKind.Plain, new ColumnShape(typeof(byte[]), "LONG RAW"), null, "LONG RAW");
            case OracleDbType.Blob: return new(OracleKind.Blob, new ColumnShape(typeof(byte[]), "BLOB") { OpenBinary = OpenBlob }, null, "BLOB");
            case OracleDbType.BFile: return new(OracleKind.BFile, new ColumnShape(typeof(string), "BFILE", ColumnHint.ProviderString), null, "BFILE");
            case OracleDbType.Vector or OracleDbType.Vector_Int8 or OracleDbType.Vector_Float32 or OracleDbType.Vector_Float64 or OracleDbType.Vector_Binary: return new(OracleKind.Vector, new ColumnShape(typeof(string), "VECTOR", ColumnHint.ProviderString), null, "VECTOR");
            case OracleDbType.Object or OracleDbType.Array or OracleDbType.Ref or OracleDbType.RefCursor:
            {
                var name = !string.IsNullOrEmpty(udt) ? udt : type == OracleDbType.RefCursor ? "CURSOR" : type.ToString().ToUpperInvariant();
                return new(OracleKind.Unreadable, new ColumnShape(typeof(string), name, ColumnHint.ProviderString), null, name);
            }
            default:
                return new(OracleKind.Plain, null, null, type.ToString());
        }
    }

    /// <summary>
    /// The NUMBER family, which ODP.NET hands out as Int16 … Double by precision (NUMBER(12,3) as Double: never used, it
    /// would round). Unconstrained NUMBER and FLOAT report scale ±127 (an expression precision 0): exact text. A negative
    /// scale rounds to the left of the point (whole numbers of p − s digits); a scale above the precision is a fraction
    /// with s digits.
    /// </summary>
    internal static OracleColumn Number(int? precision, int? scale)
    {
        if (precision is not > 0 || scale is null or 127 or -127)
        {
            var name = precision is > 0 and < 38 && scale is 127 or -127 ? "FLOAT" : "NUMBER";
            return new(OracleKind.NumberText, new ColumnShape(typeof(decimal), name, ColumnHint.ProviderString), null, name);
        }
        var p = precision.Value;
        var s = scale.Value;
        var typeName = s == 0 ? $"NUMBER({p})" : $"NUMBER({p},{s})";
        var (dp, ds) = s < 0 ? (p - s, 0) : s > p ? (s, s) : (p, s);
        return dp <= 38
            ? new(OracleKind.Decimal, new ColumnShape(typeof(decimal), typeName, ColumnHint.Default, dp, ds), ds, typeName)
            : new(OracleKind.NumberText, new ColumnShape(typeof(decimal), typeName, ColumnHint.ProviderString), null, typeName);
    }

    /// <summary>Every column of the reader's current result set (read once, before any row), remembered for <see cref="ReadAsString"/>.</summary>
    public static IReadOnlyList<ColumnShape?>? Describe(DbDataReader reader)
    {
        if (reader is not OracleDataReader r) return null;
        DataTable? table;
        try { table = r.GetSchemaTable(); }
        catch (Exception ex) when (ex is OracleException or InvalidOperationException) { return null; }
        if (table == null) return null;
        var cols = new OracleColumn[r.FieldCount];
        var hasUdt = table.Columns.Contains("UdtTypeName");
        for (var i = 0; i < cols.Length; i++)
        {
            var row = i < table.Rows.Count ? table.Rows[i] : null;
            if (row == null || row["ProviderType"] is not int pt) { cols[i] = new(OracleKind.Plain, null, null, ""); continue; }
            cols[i] = Map((OracleDbType)pt, Int(row["NumericPrecision"]), Int(row["NumericScale"]), Int(row["ColumnSize"]),
                hasUdt ? row["UdtTypeName"] as string : null);
        }
        Columns.AddOrUpdate(reader, cols);
        return cols.Select(c => c.Shape).ToArray();
    }

    static int? Int(object? v) => v is null or DBNull ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    /// <summary>The text form of a column that travels as text; null for NULL.</summary>
    public static string? ReadAsString(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        if (reader is not OracleDataReader r) return Generic(reader.GetValue(ordinal));
        var col = Columns.TryGetValue(reader, out var cols) && ordinal < cols.Length ? cols[ordinal] : null;
        switch (col?.Kind)
        {
            case OracleKind.Decimal or OracleKind.NumberText: return r.GetOracleDecimal(ordinal).ToString();
            case OracleKind.TimestampText:
                return r.GetProviderSpecificFieldType(ordinal) == typeof(OracleTimeStampLTZ)
                    ? Stamp(r.GetOracleTimeStampLTZ(ordinal), col.Scale ?? 9)
                    : Stamp(r.GetOracleTimeStamp(ordinal), col.Scale ?? 9);
            case OracleKind.TimestampTzText:
            {
                var v = r.GetOracleTimeStampTZ(ordinal);
                return Stamp(v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second, v.Nanosecond, col.Scale ?? 6) + " " + v.TimeZone;
            }
            case OracleKind.IntervalYm: return r.GetOracleIntervalYM(ordinal).ToString();
            case OracleKind.IntervalDs: return r.GetOracleIntervalDS(ordinal).ToString();
            case OracleKind.Vector: return Vector(r.GetValue(ordinal));
            case OracleKind.BFile:
            {
                using var f = r.GetOracleBFile(ordinal);
                return $"BFILENAME('{f.DirectoryName}', '{f.FileName}')";
            }
            case OracleKind.Unreadable: return Unreadable(col.TypeName);
        }
        var value = r.GetOracleValue(ordinal);
        return value switch
        {
            OracleDecimal d => d.ToString(),
            OracleDate dt => Stamp(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, 0, 0),
            OracleTimeStamp ts => Stamp(ts, 9),
            OracleBinary b => "0x" + Convert.ToHexString(b.Value),
            _ => Generic(reader.GetValue(ordinal)),
        };
    }

    /// <summary>The cell text of a value ODP.NET cannot read without a custom type mapping, saying how to see it.</summary>
    internal static string Unreadable(string typeName) => typeName == "CURSOR"
        ? "(a cursor: Querithm does not open cursor columns; return it with DBMS_SQL.RETURN_RESULT instead)"
        : $"({typeName} value not shown: Querithm does not read Oracle object or collection types. Convert it in the query, for example JSON_OBJECT(*), XMLTYPE(…) or SDO_UTIL.TO_WKTGEOMETRY.)";

    static string Generic(object value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>VECTOR as Oracle prints it: <c>[1.5,2,3]</c>, round-trip numbers, invariant culture; BINARY vectors as bytes.</summary>
    internal static string Vector(object value)
    {
        if (value is not Array a) return Generic(value);
        var sb = new StringBuilder("[");
        for (var i = 0; i < a.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(a.GetValue(i) switch
            {
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                float f => f.ToString("R", CultureInfo.InvariantCulture),
                IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
                var x => Convert.ToString(x, CultureInfo.InvariantCulture),
            });
        }
        return sb.Append(']').ToString();
    }

    static string Stamp(OracleTimeStamp v, int scale) => Stamp(v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second, v.Nanosecond, scale);
    static string Stamp(OracleTimeStampLTZ v, int scale) => Stamp(v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second, v.Nanosecond, scale);

    /// <summary>
    /// <c>YYYY-MM-DD HH24:MI:SS[.F…]</c> with exactly <paramref name="scale"/> fractional digits; Oracle's own year numbering
    /// (−4712 … 9999, no year 0).
    /// </summary>
    internal static string Stamp(int year, int month, int day, int hour, int minute, int second, int nanosecond, int scale)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{(year < 0 ? "-" : "")}{Math.Abs(year):D4}-{month:D2}-{day:D2} {hour:D2}:{minute:D2}:{second:D2}");
        if (scale <= 0) return text;
        var frac = nanosecond.ToString("D9", CultureInfo.InvariantCulture)[..Math.Min(9, scale)];
        return text + "." + frac;
    }

    static long DateMicros(DbDataReader r, int i)
    {
        var v = ((OracleDataReader)r).GetOracleDate(i);
        return Micros(v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second, 0);
    }

    static long TimestampMicros(DbDataReader r, int i)
    {
        var v = ((OracleDataReader)r).GetOracleTimeStamp(i);
        return Micros(v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second, v.Nanosecond);
    }

    static long LocalTimestampMicros(DbDataReader r, int i)
    {
        var v = ((OracleDataReader)r).GetOracleTimeStampLTZ(i);
        return Micros(v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second, v.Nanosecond);
    }

    /// <summary>
    /// Microseconds since 1970-01-01 of the wall-clock components, on the proleptic Gregorian calendar (as ODP.NET's own
    /// DateTime reads them). Oracle has no year 0: its year −1 (1 BC) is astronomical year 0, so BC dates arrive in ISO
    /// 8601 numbering (Oracle −4000 is −3999), still the same day. DateTime cannot hold them; this can.
    /// </summary>
    internal static long Micros(int year, int month, int day, int hour, int minute, int second, int nanosecond)
    {
        long y = year < 0 ? year + 1 : year;
        // Days from civil (H. Hinnant), valid for any year.
        y -= month <= 2 ? 1 : 0;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var mp = (month + 9) % 12;
        var doy = (153 * mp + 2) / 5 + day - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        var days = era * 146097 + doe - 719468;
        return ((days * 24 + hour) * 60 + minute) * 60_000_000L + second * 1_000_000L + nanosecond / 1000;
    }

    static TextReader OpenClob(DbDataReader r, int i) => new ClobReader(((OracleDataReader)r).GetOracleClob(i));
    static Stream OpenBlob(DbDataReader r, int i) => new BlobStream(((OracleDataReader)r).GetOracleBlob(i));

    /// <summary>
    /// A CLOB read in chunks up to its length (sent with the prefetch): measured on the fixture, asking for more after the
    /// last character costs a round trip per value (5,000 × 200-character CLOBs: 3.4 s with that read, 80 ms without).
    /// </summary>
    internal sealed class ClobReader(OracleClob clob) : TextReader
    {
        long _remaining = clob.Length / 2; // bytes, UTF-16

        public override int Read(char[] buffer, int index, int count)
        {
            if (_remaining <= 0 || count == 0) return 0;
            var n = clob.Read(buffer, index, (int)Math.Min(count, _remaining));
            _remaining = n <= 0 ? 0 : _remaining - n;
            return Math.Max(n, 0);
        }

        public override int Peek() => -1;

        protected override void Dispose(bool disposing)
        {
            if (disposing) clob.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A BLOB read in chunks up to its length (see <see cref="ClobReader"/>).</summary>
    internal sealed class BlobStream(OracleBlob blob) : Stream
    {
        long _remaining = blob.Length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0 || count == 0) return 0;
            var n = blob.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining = n <= 0 ? 0 : _remaining - n;
            return Math.Max(n, 0);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => blob.Length;
        public override long Position { get => blob.Length - _remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) blob.Dispose();
            base.Dispose(disposing);
        }
    }
}
