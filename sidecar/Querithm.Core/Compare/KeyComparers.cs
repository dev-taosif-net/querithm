using System.Globalization;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Compare;

/// <summary>
/// Orders and compares values read as the grid's text form. Key order must equal the servers' ORDER BY
/// (code point for text under a binary collation, canonical text for GUIDs, numbers and instants by value), or the
/// merge join pairs the wrong rows — the comparer checks that each side really arrives in this order.
/// </summary>
public static class KeyComparers
{
    /// <summary>Kinds whose server order and .NET order agree, so they can be compared as keys with each other.</summary>
    public static string Family(ValueKind k) => k switch
    {
        ValueKind.Integer or ValueKind.Decimal or ValueKind.Float => "number",
        ValueKind.Text or ValueKind.PaddedText => "text",
        ValueKind.Date or ValueKind.DateTime => "datetime",
        _ => k.ToString(),
    };

    /// <summary>Unicode code point order (UTF-16 ordinal differs above U+FFFF: surrogates sort before U+E000).</summary>
    public static int CodePoints(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x == y) continue;
            if (char.IsSurrogate(x) || char.IsSurrogate(y))
            {
                var cx = char.IsSurrogatePair(a, i) ? char.ConvertToUtf32(a, i) : x;
                var cy = char.IsSurrogatePair(b, i) ? char.ConvertToUtf32(b, i) : y;
                return cx.CompareTo(cy);
            }
            return x.CompareTo(y);
        }
        return a.Length.CompareTo(b.Length);
    }

    static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd",
    ];

    public static bool TryDateTime(string s, out DateTime v) =>
        DateTime.TryParseExact(s.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out v) ||
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out v);

    /// <summary>"… +00:00" (SQL Server 121) and "…+00" / "…+05:30" (PostgreSQL) to an instant.</summary>
    public static bool TryOffset(string s, out DateTimeOffset v)
    {
        var t = s.Trim();
        // PostgreSQL prints "+00" without minutes; add them so the parser accepts it.
        var sign = Math.Max(t.LastIndexOf('+'), t.LastIndexOf('-'));
        if (sign > 10 && t.Length - sign == 3) t += ":00";
        if (DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out v)) return true;
        if (TryDateTime(s, out var dt)) { v = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)); return true; }
        return false;
    }

    static bool TryBool(string s, out bool v)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "t" or "yes" or "y" or "on": v = true; return true;
            case "0" or "false" or "f" or "no" or "n" or "off": v = false; return true;
            default: v = false; return false;
        }
    }

    static string Hex(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("\\x", StringComparison.Ordinal)) t = t[2..];
        return t.ToLowerInvariant();
    }

    static bool TryNumber(string s, out decimal v) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>A key part normalised once per row, so the merge compares values, not text.</summary>
    public static IComparable Normalize(ValueKind kind, string text, bool padded, string column)
    {
        switch (kind)
        {
            case ValueKind.Text or ValueKind.PaddedText:
                return new CodePointString(padded || kind == ValueKind.PaddedText ? text.TrimEnd(' ') : text);
            case ValueKind.Integer or ValueKind.Decimal:
                return TryNumber(text, out var d) ? d : throw Bad(column, text, "a number");
            case ValueKind.Float:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : throw Bad(column, text, "a number");
            case ValueKind.Bool:
                return TryBool(text, out var b) ? b : throw Bad(column, text, "a boolean");
            case ValueKind.Guid:
                return Guid.TryParse(text, out var g) ? new CodePointString(g.ToString("D")) : throw Bad(column, text, "a GUID");
            case ValueKind.Date or ValueKind.DateTime:
                return TryDateTime(text, out var dt) ? dt.Ticks : throw Bad(column, text, "a date/time");
            case ValueKind.DateTimeOffset:
                return TryOffset(text, out var o) ? o.UtcTicks : throw Bad(column, text, "a date/time with offset");
            case ValueKind.Time:
                return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) ? ts.Ticks : throw Bad(column, text, "a time");
            case ValueKind.Binary:
                return new CodePointString(Hex(text));
            default:
                throw new QuerithmException("compareKey", $"{column} cannot be a key: its type has no order both sides agree on.");
        }
    }

    static QuerithmException Bad(string column, string text, string what) =>
        new("compareKey", $"Key column {column}: '{(text.Length > 40 ? text[..40] + "…" : text)}' is not {what}.");

    public static int CompareKeys(IComparable[] a, IComparable[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var c = Compare(a[i], b[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    static int Compare(IComparable x, IComparable y)
    {
        if (x.GetType() == y.GetType()) return x.CompareTo(y);
        // Mixed numeric representations of one family (decimal vs double).
        if (x is decimal dx && y is double dy) return ((double)dx).CompareTo(dy);
        if (x is double ex && y is decimal ey) return ex.CompareTo((double)ey);
        return string.CompareOrdinal(x.ToString(), y.ToString());
    }

    /// <summary>Non-key values: equal after normalising the type's text form (scale, case of hex, padding, tolerance).</summary>
    public static bool ValuesEqual(ValueKind ks, ValueKind kt, string? a, string? b, DataCompareOptions o)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a == b) return true;
        var fs = Family(ks);
        var ft = Family(kt);
        if (fs == "number" && ft == "number")
        {
            if (ks == ValueKind.Float || kt == ValueKind.Float)
            {
                if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    return false;
                if (x == y) return true;
                return Math.Abs(x - y) <= o.FloatEpsilon * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
            }
            return TryNumber(a, out var p) && TryNumber(b, out var q) && p == q;
        }
        if (ks == ValueKind.Bool || kt == ValueKind.Bool)
            return TryBool(a, out var p) && TryBool(b, out var q) ? p == q : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (ks == ValueKind.Guid || kt == ValueKind.Guid)
            return Guid.TryParse(a, out var p) && Guid.TryParse(b, out var q) ? p == q : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (ks == ValueKind.Binary || kt == ValueKind.Binary) return Hex(a) == Hex(b);
        if (ks == ValueKind.DateTimeOffset || kt == ValueKind.DateTimeOffset)
            return TryOffset(a, out var p) && TryOffset(b, out var q) && Math.Abs((p.UtcDateTime - q.UtcDateTime).TotalMilliseconds) <= o.TimeToleranceMs;
        if (fs == "datetime" && ft == "datetime")
            return TryDateTime(a, out var p) && TryDateTime(b, out var q) && Math.Abs((p - q).TotalMilliseconds) <= o.TimeToleranceMs;
        if (ks == ValueKind.Time && kt == ValueKind.Time)
            return TimeSpan.TryParse(a, CultureInfo.InvariantCulture, out var p) && TimeSpan.TryParse(b, CultureInfo.InvariantCulture, out var q) &&
                   Math.Abs((p - q).TotalMilliseconds) <= o.TimeToleranceMs;
        if (o.TrimCharPadding && (ks == ValueKind.PaddedText || kt == ValueKind.PaddedText)) { a = a.TrimEnd(' '); b = b.TrimEnd(' '); }
        return string.Equals(a, b, o.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>A string ordered by Unicode code point (the order of BIN2 and COLLATE "C").</summary>
public readonly record struct CodePointString(string Value) : IComparable
{
    public int CompareTo(object? obj) => obj is CodePointString o ? KeyComparers.CodePoints(Value, o.Value) : 1;
    public override string ToString() => Value;
}
