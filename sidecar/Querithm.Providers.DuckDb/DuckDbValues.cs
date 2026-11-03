using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using DuckDB.NET.Data;
using DuckDB.NET.Native;
using Querithm.Contract.Providers;

namespace Querithm.Providers.DuckDb;

/// <summary>
/// DuckDB values the driver cannot hand over as a .NET value without loss (Phase D3, measured on DuckDB.NET 1.5.6.1): it
/// rounds DECIMAL beyond 28 digits and reports no precision, refuses INTERVAL with months, gives TIMETZ a date, and fails on
/// a NULL inside a list of numbers. These columns travel as text built from the driver's raw values, in DuckDB's own
/// literal forms: exact decimals, <c>1 year 2 months 3 days 04:05:06.789</c>, <c>10:20:30.5+06:30</c>,
/// <c>[1, NULL]</c>, <c>{'a': 1}</c>, <c>{'k'=1}</c>.
/// </summary>
public static class DuckDbValues
{
    /// <summary>
    /// By the CLR type the driver reports (its column schema leaves DataTypeName empty): decimal (DECIMAL), TimeSpan
    /// (INTERVAL; TIME is TimeOnly), DateTimeOffset (TIMETZ; TIMESTAMPTZ is DateTime), and lists, structs and maps.
    /// </summary>
    public static ColumnHint Hint(DbColumn column) => column.DataType is { } t && (t == typeof(decimal) || t == typeof(TimeSpan) || t == typeof(DateTimeOffset)
        || (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(Dictionary<,>))))
        ? ColumnHint.ProviderString : ColumnHint.Default;

    public static string? Text(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var r = (DuckDBDataReader)reader;
        switch (r.GetDataTypeName(ordinal))
        {
            case "Decimal":
                return r.GetProviderSpecificValue(ordinal) is DuckDBDecimal d ? Decimal(d.Value.ToBigInteger(), d.Scale) : Format(r.GetValue(ordinal));
            case "Interval":
                return Interval(r.GetFieldValue<DuckDBInterval>(ordinal));
            case "TimeTz":
                return TimeTz((DuckDBTimeTz)r.GetProviderSpecificValue(ordinal));
            case "List" or "Array" or "Struct" or "Map":
                return Format(Nested(r, ordinal));
            default:
                return Format(r.GetValue(ordinal));
        }
    }

    /// <summary>An unscaled integer and a scale as exact decimal text (scale digits kept, as DuckDB prints DECIMAL).</summary>
    public static string Decimal(BigInteger unscaled, int scale)
    {
        var digits = BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture);
        if (scale > 0)
        {
            digits = digits.PadLeft(scale + 1, '0');
            digits = digits[..^scale] + "." + digits[^scale..];
        }
        return unscaled.Sign < 0 ? "-" + digits : digits;
    }

    public static string Interval(DuckDBInterval v) => Interval(v.Months, v.Days, unchecked((long)v.Micros));

    public static string Interval(int totalMonths, int days, long micros)
    {
        var parts = new List<string>();
        var months = totalMonths;
        var years = months / 12;
        months -= years * 12;
        static string Unit(long n, string unit) => $"{n.ToString(CultureInfo.InvariantCulture)} {unit}{(Math.Abs(n) == 1 ? "" : "s")}";
        if (years != 0) parts.Add(Unit(years, "year"));
        if (months != 0) parts.Add(Unit(months, "month"));
        if (days != 0) parts.Add(Unit(days, "day"));
        if (micros != 0 || parts.Count == 0) parts.Add((micros < 0 ? "-" : "") + Clock(micros == long.MinValue ? long.MaxValue : Math.Abs(micros)));
        return string.Join(' ', parts);
    }

    /// <summary>HH:MM:SS[.ffffff] with trailing zeros of the fraction dropped; hours may exceed 24.</summary>
    static string Clock(long micros)
    {
        var s = new StringBuilder();
        var hours = micros / 3_600_000_000L;
        micros -= hours * 3_600_000_000L;
        s.Append(hours.ToString("00", CultureInfo.InvariantCulture)).Append(':')
         .Append((micros / 60_000_000L).ToString("00", CultureInfo.InvariantCulture)).Append(':');
        micros %= 60_000_000L;
        s.Append((micros / 1_000_000L).ToString("00", CultureInfo.InvariantCulture));
        var frac = micros % 1_000_000L;
        if (frac != 0) s.Append('.').Append(frac.ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0'));
        return s.ToString();
    }

    public static string TimeTz(DuckDBTimeTz v)
    {
        var t = v.Time;
        var clock = Clock(((t.Hour * 60L + t.Min) * 60L + t.Sec) * 1_000_000L + t.Microsecond);
        var offset = Math.Abs(v.Offset);
        var sign = v.Offset < 0 ? "-" : "+";
        var hh = (offset / 3600).ToString("00", CultureInfo.InvariantCulture);
        var mm = offset / 60 % 60;
        var ss = offset % 60;
        return clock + sign + hh + (mm != 0 || ss != 0 ? ":" + mm.ToString("00", CultureInfo.InvariantCulture) : "") + (ss != 0 ? ":" + ss.ToString("00", CultureInfo.InvariantCulture) : "");
    }

    /// <summary>
    /// Per reader and column, the type a nested column is read as once a plain read failed on a NULL inside it (a list of
    /// numbers is read as a list of nullable numbers).
    /// </summary>
    static readonly ConditionalWeakTable<DbDataReader, Dictionary<int, Type>> NullableAs = new();

    static object? Nested(DuckDBDataReader r, int ordinal)
    {
        var known = NullableAs.GetOrCreateValue(r);
        if (known.TryGetValue(ordinal, out var asType)) return Read(r, ordinal, asType);
        try { return r.GetValue(ordinal); }
        catch (InvalidCastException e) when (e.Message.Contains("null", StringComparison.OrdinalIgnoreCase))
        {
            var t = WithNullableValues(r.GetFieldType(ordinal));
            if (t == null) throw Unreadable(r, ordinal, e);
            try
            {
                var v = Read(r, ordinal, t);
                known[ordinal] = t;
                return v;
            }
            catch (Exception inner) when (inner is InvalidCastException or ArgumentException or InvalidOperationException) { throw Unreadable(r, ordinal, inner); }
        }
        catch (InvalidOperationException e) when (e.Message.Contains("null", StringComparison.OrdinalIgnoreCase))
        {
            var t = WithNullableValues(r.GetFieldType(ordinal));
            if (t == null) throw Unreadable(r, ordinal, e);
            known[ordinal] = t;
            return Read(r, ordinal, t);
        }
    }

    static Exception Unreadable(DuckDBDataReader r, int ordinal, Exception e) => new InvalidOperationException(
        $"The DuckDB driver cannot read the NULL inside column {r.GetName(ordinal)} ({r.GetDataTypeName(ordinal)}). Cast that column to VARCHAR in the query to see it as text.", e);

    static readonly System.Reflection.MethodInfo GetFieldValue = typeof(DuckDBDataReader).GetMethod(nameof(DuckDBDataReader.GetFieldValue))!;

    static object? Read(DuckDBDataReader r, int ordinal, Type t)
    {
        try { return GetFieldValue.MakeGenericMethod(t).Invoke(r, [ordinal]); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    /// <summary>List&lt;int&gt; → List&lt;int?&gt; and Dictionary&lt;K, V&gt; → Dictionary&lt;K, V?&gt;, recursively; null when nothing changes.</summary>
    internal static Type? WithNullableValues(Type t)
    {
        if (t.IsValueType) return Nullable.GetUnderlyingType(t) == null ? typeof(Nullable<>).MakeGenericType(t) : null;
        if (!t.IsGenericType) return null;
        var def = t.GetGenericTypeDefinition();
        var args = t.GetGenericArguments();
        if (def == typeof(List<>)) return WithNullableValues(args[0]) is { } e ? typeof(List<>).MakeGenericType(e) : null;
        if (def == typeof(Dictionary<,>) && args[1] != typeof(object)) return WithNullableValues(args[1]) is { } v ? typeof(Dictionary<,>).MakeGenericType(args[0], v) : null;
        return null;
    }

    /// <summary>A value as DuckDB literal text: strings quoted inside lists, structs and maps; NULL spelled out there.</summary>
    public static string Format(object? v) => Format(v, top: true);

    static string Format(object? v, bool top) => v switch
    {
        null or DBNull => top ? "" : "NULL",
        string s => top ? s : "'" + s.Replace("'", "''") + "'",
        byte[] b => "0x" + Convert.ToHexString(b),
        Stream st => Hex(st),
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
        TimeSpan ts => Interval(0, ts.Days, ts.Ticks % TimeSpan.TicksPerDay / 10),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IDictionary dict when IsStruct(dict) => "{" + string.Join(", ", dict.Keys.Cast<object>().Select(k => $"'{Convert.ToString(k, CultureInfo.InvariantCulture)?.Replace("'", "''")}': {Format(dict[k], false)}")) + "}",
        IDictionary dict => "{" + string.Join(", ", dict.Keys.Cast<object>().Select(k => $"{Format(k, false)}={Format(dict[k], false)}")) + "}",
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(x => Format(x, false))) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>DuckDB.NET reads a STRUCT as Dictionary&lt;string, object&gt;; a MAP keeps its key and value types.</summary>
    static bool IsStruct(IDictionary d) => d.GetType() == typeof(Dictionary<string, object>) || d.GetType() == typeof(Dictionary<string, object?>);

    static string Hex(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return "0x" + Convert.ToHexString(ms.ToArray());
    }
}
