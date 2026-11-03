using System.Globalization;
using System.Numerics;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Import;

/// <summary>HyperLogLog (2^12 registers, FNV-1a 64) for distinct counts in bounded memory.</summary>
public sealed class HyperLogLog
{
    const int P = 12;
    const int M = 1 << P;
    readonly byte[] _reg = new byte[M];

    static ulong Hash(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in s)
        {
            h ^= ch;
            h *= 1099511628211UL;
        }
        // Final avalanche (FNV alone clusters the low bits used for the register index).
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return h;
    }

    public void Add(string value)
    {
        var h = Hash(value);
        var idx = (int)(h & (M - 1));
        var w = h >> P;
        var rank = (byte)(BitOperations.LeadingZeroCount(w) - P + 1);
        if (rank > _reg[idx]) _reg[idx] = rank;
    }

    public long Estimate()
    {
        double sum = 0;
        var zeros = 0;
        foreach (var r in _reg)
        {
            sum += Math.Pow(2, -r);
            if (r == 0) zeros++;
        }
        var alpha = 0.7213 / (1 + 1.079 / M);
        var e = alpha * M * M / sum;
        if (e <= 2.5 * M && zeros > 0) e = M * Math.Log(M / (double)zeros);
        return (long)Math.Round(e);
    }
}

/// <summary>Joins value shapes into a column type over the whole file, plus nulls, length and distinct estimate.</summary>
public sealed class ColumnProfiler(string name, int ordinal)
{
    ImportKind _kind = ImportKind.Empty;
    int _intDigits;
    int _scale;
    int _fraction;
    int _maxLen;
    long _nulls;
    long _values;
    long _blanks; // whitespace-only cells: NULLs unless the column is text
    readonly List<long> _firstNulls = [];
    readonly List<long> _firstBlanks = [];
    readonly HyperLogLog _hll = new();
    BigInteger? _min;
    BigInteger? _max;

    public string Name { get; } = name;
    public int Ordinal { get; } = ordinal;
    public ImportKind Kind => _kind;

    public void Add(string? value, long row)
    {
        if (value == null)
        {
            _nulls++;
            if (_firstNulls.Count < 5) _firstNulls.Add(row);
            return;
        }
        _values++;
        if (value.Length > _maxLen) _maxLen = value.Length;
        _hll.Add(value);
        if (_kind == ImportKind.Text) return; // nothing narrows text again
        var shape = ValueClassifier.Classify(value);
        if (shape.Kind == ImportKind.Empty)
        {
            // Whitespace only: keeps the type, and loads as NULL into a non-text column (IImportDialect.BlankIsNull).
            _blanks++;
            if (_firstBlanks.Count < 5) _firstBlanks.Add(row);
            return;
        }
        _kind = Join(_kind, shape.Kind);
        _intDigits = Math.Max(_intDigits, shape.IntDigits);
        _scale = Math.Max(_scale, shape.Scale);
        _fraction = Math.Max(_fraction, shape.Fraction);
        if (shape.Kind is ImportKind.Int32 or ImportKind.Int64)
        {
            if (_min == null || shape.Int < _min) _min = shape.Int;
            if (_max == null || shape.Int > _max) _max = shape.Int;
        }
        if (_kind == ImportKind.Decimal && _intDigits + _scale > 38) _kind = ImportKind.Double;
    }

    /// <summary>
    /// Rows 1..<paramref name="count"/> had no value (a JSON key first seen after them): what <paramref name="count"/>
    /// calls of <c>Add(null, row)</c> record, in O(1) (L8: a key per line used to replay every earlier row per new key).
    /// </summary>
    public void AddNulls(long count)
    {
        _nulls += count;
        for (long row = 1; row <= count && _firstNulls.Count < 5; row++) _firstNulls.Add(row);
    }

    /// <summary>The type lattice: numbers widen Int32 → Int64 → Decimal → Double; Date widens to DateTime; anything else mixed is Text.</summary>
    public static ImportKind Join(ImportKind a, ImportKind b)
    {
        if (a == ImportKind.Empty) return b;
        if (b == ImportKind.Empty || a == b) return a;
        static int Rank(ImportKind k) => k switch { ImportKind.Int32 => 1, ImportKind.Int64 => 2, ImportKind.Decimal => 3, ImportKind.Double => 4, _ => 0 };
        if (Rank(a) > 0 && Rank(b) > 0) return Rank(a) >= Rank(b) ? a : b;
        if (a is ImportKind.Date or ImportKind.DateTime && b is ImportKind.Date or ImportKind.DateTime) return ImportKind.DateTime;
        return ImportKind.Text;
    }

    public ImportColumnProfile Build()
    {
        var distinct = _hll.Estimate();
        var kind = _kind;
        // Blank cells count as NULLs unless the column is text (where they are kept as they are).
        var blanksAreNull = kind != ImportKind.Text;
        var nulls = _nulls + (blanksAreNull ? _blanks : 0);
        var values = _values - (blanksAreNull ? _blanks : 0);
        long[] firstNulls = blanksAreNull ? [.. _firstNulls.Concat(_firstBlanks).Order().Take(5)] : [.. _firstNulls];
        return new ImportColumnProfile
        {
            Name = Name, Ordinal = Ordinal, Kind = kind,
            Precision = kind == ImportKind.Decimal ? Math.Max(1, _intDigits + _scale) : kind is ImportKind.DateTime or ImportKind.DateTimeOffset or ImportKind.Time ? _fraction : 0,
            Scale = kind == ImportKind.Decimal ? _scale : 0,
            MaxLength = _maxLen, NullCount = nulls, FirstNullRows = firstNulls, DistinctEstimate = distinct,
            Min = kind is ImportKind.Int32 or ImportKind.Int64 ? _min?.ToString(CultureInfo.InvariantCulture) : null,
            Max = kind is ImportKind.Int32 or ImportKind.Int64 ? _max?.ToString(CultureInfo.InvariantCulture) : null,
            // HLL is ±2 %: "looks unique" within that error, and no NULLs.
            CandidateKey = nulls == 0 && values > 0 && distinct >= values * 0.98,
        };
    }
}
