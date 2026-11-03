using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Querithm.Providers.Postgres;

/// <summary>
/// Renders PostgreSQL binary values as psql-style text. Used for columns Npgsql cannot read at all (user composites
/// and arrays of them): their binary layout carries each field's type OID, so the common built-in types are decoded
/// here and anything else falls back to text (enums, citext) or <c>\x</c> hex.
/// </summary>
public static class PgBinaryText
{
    static readonly DateTime PgEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Types from user schemas (and their arrays); pg_catalog types always have Npgsql's own handling.</summary>
    public static bool IsUserType(string dataTypeName)
    {
        var dot = dataTypeName.IndexOf('.');
        return dot > 0 && dataTypeName[..dot] != "pg_catalog";
    }

    /// <summary>A whole column value: an array when <paramref name="isArray"/>, else a composite (or, failing that, any value).</summary>
    public static string Render(ReadOnlySpan<byte> data, bool isArray)
    {
        if (isArray && TryArray(data, out var a)) return a;
        if (TryComposite(data, out var c)) return c;
        return Fallback(data);
    }

    /// <summary>One value of a known type OID.</summary>
    public static string Value(uint oid, ReadOnlySpan<byte> d)
    {
        try
        {
            switch (oid)
            {
                case 16: return d[0] != 0 ? "t" : "f";
                case 21: return BinaryPrimitives.ReadInt16BigEndian(d).ToString(CultureInfo.InvariantCulture);
                case 23: return BinaryPrimitives.ReadInt32BigEndian(d).ToString(CultureInfo.InvariantCulture);
                case 20: return BinaryPrimitives.ReadInt64BigEndian(d).ToString(CultureInfo.InvariantCulture);
                case 26: return BinaryPrimitives.ReadUInt32BigEndian(d).ToString(CultureInfo.InvariantCulture);
                case 700: return BinaryPrimitives.ReadSingleBigEndian(d).ToString("R", CultureInfo.InvariantCulture);
                case 701: return BinaryPrimitives.ReadDoubleBigEndian(d).ToString("R", CultureInfo.InvariantCulture);
                case 1700: return Numeric(d);
                case 790: return (BinaryPrimitives.ReadInt64BigEndian(d) / 100m).ToString("0.00", CultureInfo.InvariantCulture);
                case 25 or 1043 or 1042 or 19 or 114 or 142 or 705 or 18: return Encoding.UTF8.GetString(d);
                case 3802: return Encoding.UTF8.GetString(d[1..]); // jsonb: version byte, then text
                case 17: return @"\x" + Convert.ToHexString(d).ToLowerInvariant();
                case 2950: return new Guid(d, bigEndian: true).ToString();
                case 1082: return Date(BinaryPrimitives.ReadInt32BigEndian(d));
                case 1083: return Time(BinaryPrimitives.ReadInt64BigEndian(d));
                case 1266: return Time(BinaryPrimitives.ReadInt64BigEndian(d)) + Offset(-BinaryPrimitives.ReadInt32BigEndian(d[8..]));
                case 1114: return Timestamp(BinaryPrimitives.ReadInt64BigEndian(d), null);
                case 1184: return Timestamp(BinaryPrimitives.ReadInt64BigEndian(d), "+00");
                case 1186: return Interval(BinaryPrimitives.ReadInt64BigEndian(d), BinaryPrimitives.ReadInt32BigEndian(d[8..]), BinaryPrimitives.ReadInt32BigEndian(d[12..]));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return Fallback(d);
        }
        // User types: a nested composite or array, or a text-shaped value such as an enum label or citext.
        if (TryComposite(d, out var c)) return c;
        if (TryArray(d, out var a)) return a;
        return Fallback(d);
    }

    static string Fallback(ReadOnlySpan<byte> d)
    {
        try
        {
            var s = StrictUtf8.GetString(d);
            if (!s.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))) return s;
        }
        catch (DecoderFallbackException) { }
        return @"\x" + Convert.ToHexString(d).ToLowerInvariant();
    }

    /// <summary>int32 field count, then per field: uint32 type OID, int32 length (-1 = NULL), bytes.</summary>
    public static bool TryComposite(ReadOnlySpan<byte> d, out string text)
    {
        text = "";
        if (d.Length < 4) return false;
        var n = BinaryPrimitives.ReadInt32BigEndian(d);
        if (n < 0 || n > 1664) return false; // 1664 = PostgreSQL's column limit
        var parts = new List<string?>(n);
        var pos = 4;
        for (var i = 0; i < n; i++)
        {
            if (pos + 8 > d.Length) return false;
            var oid = BinaryPrimitives.ReadUInt32BigEndian(d[pos..]);
            var len = BinaryPrimitives.ReadInt32BigEndian(d[(pos + 4)..]);
            pos += 8;
            if (len == -1) { parts.Add(null); continue; }
            if (len < 0 || pos + len > d.Length) return false;
            parts.Add(Value(oid, d.Slice(pos, len)));
            pos += len;
        }
        if (pos != d.Length) return false;
        text = "(" + string.Join(",", parts.Select(QuoteCompositeField)) + ")";
        return true;
    }

    /// <summary>int32 dims, int32 has-null flag, uint32 element OID, per dim (int32 size, int32 lower bound), then elements (int32 length, bytes).</summary>
    public static bool TryArray(ReadOnlySpan<byte> d, out string text)
    {
        text = "";
        if (d.Length < 12) return false;
        var dims = BinaryPrimitives.ReadInt32BigEndian(d);
        var flags = BinaryPrimitives.ReadInt32BigEndian(d[4..]);
        var elem = BinaryPrimitives.ReadUInt32BigEndian(d[8..]);
        if (dims < 0 || dims > 6 || flags is not (0 or 1) || d.Length < 12 + dims * 8) return false;
        if (dims == 0) { text = "{}"; return d.Length == 12; }
        var sizes = new int[dims];
        long total = 1;
        for (var i = 0; i < dims; i++)
        {
            sizes[i] = BinaryPrimitives.ReadInt32BigEndian(d[(12 + i * 8)..]);
            if (sizes[i] < 0) return false;
            total *= sizes[i];
        }
        var pos = 12 + dims * 8;
        var items = new List<string>((int)Math.Min(total, 100_000));
        for (long i = 0; i < total; i++)
        {
            if (pos + 4 > d.Length) return false;
            var len = BinaryPrimitives.ReadInt32BigEndian(d[pos..]);
            pos += 4;
            if (len == -1) { items.Add("NULL"); continue; }
            if (len < 0 || pos + len > d.Length) return false;
            items.Add(QuoteArrayElement(Value(elem, d.Slice(pos, len))));
            pos += len;
        }
        if (pos != d.Length) return false;
        var at = 0;
        text = Nest(items, sizes, 0, ref at);
        return true;
    }

    static string Nest(List<string> items, int[] sizes, int dim, ref int at)
    {
        var parts = new List<string>(sizes[dim]);
        for (var i = 0; i < sizes[dim]; i++)
            parts.Add(dim == sizes.Length - 1 ? items[at++] : Nest(items, sizes, dim + 1, ref at));
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>Composite output rules: NULL is empty; quote empty strings and values with <c>"\(),</c> or spaces.</summary>
    public static string QuoteCompositeField(string? v)
    {
        if (v == null) return "";
        if (v.Length > 0 && !v.Any(ch => ch is '"' or '\\' or '(' or ')' or ',' || char.IsWhiteSpace(ch))) return v;
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Array output rules: quote empty strings, "NULL", and values with <c>{}",\</c> or spaces.</summary>
    public static string QuoteArrayElement(string v)
    {
        if (v.Length > 0 && !v.Equals("NULL", StringComparison.OrdinalIgnoreCase) && !v.Any(ch => ch is '{' or '}' or '"' or ',' or '\\' || char.IsWhiteSpace(ch))) return v;
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static string Numeric(ReadOnlySpan<byte> d)
    {
        var ndigits = BinaryPrimitives.ReadInt16BigEndian(d);
        var weight = BinaryPrimitives.ReadInt16BigEndian(d[2..]);
        var sign = BinaryPrimitives.ReadUInt16BigEndian(d[4..]);
        var dscale = BinaryPrimitives.ReadInt16BigEndian(d[6..]);
        switch (sign)
        {
            case 0xC000: return "NaN";
            case 0xD000: return "Infinity";
            case 0xF000: return "-Infinity";
        }
        var digits = new short[ndigits];
        for (var i = 0; i < ndigits; i++) digits[i] = BinaryPrimitives.ReadInt16BigEndian(d[(8 + i * 2)..]);
        var sb = new StringBuilder();
        if (sign == 0x4000) sb.Append('-');
        // Integer part: base-10000 groups 0..weight.
        if (weight < 0) sb.Append('0');
        for (var g = 0; g <= weight; g++)
        {
            var v = g < ndigits ? digits[g] : 0;
            sb.Append(g == 0 ? v.ToString(CultureInfo.InvariantCulture) : v.ToString("D4", CultureInfo.InvariantCulture));
        }
        if (dscale > 0)
        {
            var frac = new StringBuilder();
            for (var g = weight + 1; frac.Length < dscale; g++)
            {
                var v = g >= 0 && g < ndigits ? digits[g] : 0;
                frac.Append(v.ToString("D4", CultureInfo.InvariantCulture));
            }
            sb.Append('.').Append(frac.ToString(0, dscale));
        }
        return sb.ToString();
    }

    static string Date(int days) => days switch
    {
        int.MaxValue => "infinity",
        int.MinValue => "-infinity",
        _ => PgEpoch.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    static string Time(long micros) => TimeSpan.FromTicks(micros * 10).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) + Fraction(micros);

    static string Timestamp(long micros, string? zone) => micros switch
    {
        long.MaxValue => "infinity",
        long.MinValue => "-infinity",
        _ => PgEpoch.AddTicks(micros * 10).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(micros) + zone,
    };

    static string Fraction(long micros)
    {
        var f = (int)(((micros % 1_000_000) + 1_000_000) % 1_000_000);
        return f == 0 ? "" : "." + f.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    static string Offset(int seconds)
    {
        var sign = seconds < 0 ? '-' : '+';
        var a = Math.Abs(seconds);
        var s = $"{sign}{a / 3600:D2}";
        if (a % 3600 != 0) s += $":{a % 3600 / 60:D2}";
        return s;
    }

    static string Interval(long micros, int days, int months)
    {
        var parts = new List<string>();
        if (months / 12 != 0) parts.Add($"{months / 12} year{(Math.Abs(months / 12) == 1 ? "" : "s")}");
        if (months % 12 != 0) parts.Add($"{months % 12} mon{(Math.Abs(months % 12) == 1 ? "" : "s")}");
        if (days != 0) parts.Add($"{days} day{(Math.Abs(days) == 1 ? "" : "s")}");
        if (micros != 0 || parts.Count == 0) parts.Add((micros < 0 ? "-" : "") + Time(Math.Abs(micros)));
        return string.Join(" ", parts);
    }
}
