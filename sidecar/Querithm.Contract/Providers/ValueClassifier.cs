using System.Globalization;
using System.Numerics;

namespace Querithm.Contract.Providers;

/// <summary>What one text value looks like: the narrowest kind it fits and its size facts.</summary>
public readonly record struct ValueShape(ImportKind Kind, int IntDigits = 0, int Scale = 0, int Fraction = 0, BigInteger Int = default);

/// <summary>
/// Classifies a single value. Whole numbers with leading zeros stay text (ZIP codes, account numbers), "1"/"0" are
/// numbers not booleans, and only exact ISO forms count as dates, so nothing culture-specific is guessed.
/// </summary>
public static class ValueClassifier
{
    public static ValueShape Classify(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return new ValueShape(ImportKind.Empty);
        if (IsBool(s)) return new ValueShape(ImportKind.Bool);
        if (TryNumber(s, out var shape)) return shape;
        if (TryTemporal(s, out var t)) return t;
        if (IsUuid(s)) return new ValueShape(ImportKind.Uuid);
        return new ValueShape(ImportKind.Text);
    }

    public static bool IsBool(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase);

    static bool AllDigits(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (c is < '0' or > '9') return false;
        return true;
    }

    static bool TryNumber(string s, out ValueShape shape)
    {
        shape = default;
        var body = s.AsSpan();
        if (body[0] is '-' or '+') body = body[1..];
        if (body.Length == 0) return false;
        var e = body.IndexOfAny('e', 'E');
        if (e >= 0)
        {
            var exp = body[(e + 1)..];
            if (exp.Length > 0 && exp[0] is '-' or '+') exp = exp[1..];
            if (!AllDigits(exp) || !IsPlainDecimal(body[..e], out _, out _)) return false;
            shape = new ValueShape(ImportKind.Double);
            return true;
        }
        if (!IsPlainDecimal(body, out var intDigits, out var scale)) return false;
        if (scale < 0)
        {
            // Whole number: a leading zero (other than "0" itself) means an identifier kept as text.
            if (body.Length > 1 && body[0] == '0') return false;
            var v = BigInteger.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var kind = v >= int.MinValue && v <= int.MaxValue ? ImportKind.Int32 : v >= long.MinValue && v <= long.MaxValue ? ImportKind.Int64 : ImportKind.Decimal;
            shape = new ValueShape(kind, IntDigits: intDigits, Int: v);
            return true;
        }
        if (intDigits > 1 && body[0] == '0') return false;
        shape = intDigits + scale > 38 ? new ValueShape(ImportKind.Double) : new ValueShape(ImportKind.Decimal, IntDigits: intDigits, Scale: scale);
        return true;
    }

    /// <summary>digits[.digits] or .digits; scale = digits after the point (-1 when there is no point).</summary>
    static bool IsPlainDecimal(ReadOnlySpan<char> s, out int intDigits, out int scale)
    {
        intDigits = 0;
        scale = -1;
        var dot = s.IndexOf('.');
        var whole = dot < 0 ? s : s[..dot];
        var frac = dot < 0 ? ReadOnlySpan<char>.Empty : s[(dot + 1)..];
        if (whole.Length == 0 && frac.Length == 0) return false;
        if (whole.Length > 0 && !AllDigits(whole)) return false;
        if (dot >= 0 && frac.Length > 0 && !AllDigits(frac)) return false;
        if (dot >= 0 && frac.Length == 0 && whole.Length == 0) return false;
        intDigits = whole.TrimStart('0').Length;
        scale = dot < 0 ? -1 : frac.Length;
        return true;
    }

    static bool IsDate(ReadOnlySpan<char> s) =>
        s.Length == 10 && s[4] == '-' && s[7] == '-' && AllDigits(s[..4]) && AllDigits(s[5..7]) && AllDigits(s[8..10])
        && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>hh:mm[:ss[.f…]] → fractional digits, or -1 when not a time.</summary>
    static int TimeFraction(ReadOnlySpan<char> s)
    {
        if (s.Length < 5 || s[2] != ':' || !AllDigits(s[..2]) || !AllDigits(s[3..5])) return -1;
        if (int.Parse(s[..2], CultureInfo.InvariantCulture) > 23 || int.Parse(s[3..5], CultureInfo.InvariantCulture) > 59) return -1;
        if (s.Length == 5) return 0;
        if (s[5] != ':' || s.Length < 8 || !AllDigits(s[6..8]) || int.Parse(s[6..8], CultureInfo.InvariantCulture) > 59) return -1;
        if (s.Length == 8) return 0;
        if (s[8] != '.' || !AllDigits(s[9..])) return -1;
        return s.Length - 9;
    }

    static bool TryTemporal(string s, out ValueShape shape)
    {
        shape = default;
        var span = s.AsSpan();
        if (span.Length == 10 && IsDate(span))
        {
            shape = new ValueShape(ImportKind.Date);
            return true;
        }
        if (span.Length >= 5 && span[2] == ':' && TimeFraction(span) is var tf and >= 0)
        {
            shape = new ValueShape(ImportKind.Time, Fraction: tf);
            return true;
        }
        if (span.Length < 16 || !IsDate(span[..10]) || span[10] is not (' ' or 'T')) return false;
        var rest = span[11..];
        var offset = false;
        if (rest.EndsWith("Z")) { offset = true; rest = rest[..^1]; }
        else
        {
            var cut = rest.LastIndexOfAny('+', '-');
            if (cut > 0)
            {
                var off = rest[cut..].TrimStart('+').TrimStart('-');
                if (off.Length is 5 && off[2] == ':' && AllDigits(off[..2]) && AllDigits(off[3..]) || off.Length is 4 && AllDigits(off) || off.Length == 2 && AllDigits(off))
                {
                    offset = true;
                    rest = rest[..cut].TrimEnd();
                }
            }
        }
        var frac = TimeFraction(rest);
        if (frac < 0) return false;
        shape = new ValueShape(offset ? ImportKind.DateTimeOffset : ImportKind.DateTime, Fraction: frac);
        return true;
    }

    static bool IsUuid(string s)
    {
        var t = s.Length == 38 && s[0] == '{' && s[^1] == '}' ? s[1..^1] : s;
        return t.Length == 36 && Guid.TryParseExact(t, "D", out _) || t.Length == 32 && Guid.TryParseExact(t, "N", out _);
    }
}
