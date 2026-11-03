using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Execution;

/// <summary>
/// Reads one result set from a DbDataReader into Arrow record batches (§5 type mapping, §7 step 6).
/// Columns are read strictly in ordinal order so CommandBehavior.SequentialAccess works.
/// </summary>
public sealed class ResultSetEncoder
{
    public const int MaxBatchRows = 10_000;
    public const long MaxBatchBytes = 4L * 1024 * 1024;
    public const int TextLimitChars = 1024 * 1024;
    public const int BinaryLimitBytes = 64 * 1024;

    readonly ColumnEncoder[] _encoders;
    public Schema Schema { get; }
    public ResultColumn[] Columns { get; }
    /// <summary>The table column behind each reader ordinal, as the provider reported it (null = unknown).</summary>
    public ColumnOrigin?[]? Origins { get; }
    public int PendingRows { get; private set; }

    public ResultSetEncoder(DbDataReader reader, IDbProvider provider, TruncatedCellStore cells, int resultIndex,
        Func<IReadOnlyList<DbColumn>, ColumnOrigin?[]?>? origins = null, SchemaSnapshot? snapshot = null)
    {
        var dbColumns = reader.GetColumnSchema();
        Origins = origins?.Invoke(dbColumns);
        var keys = Origins is { } o && snapshot != null ? new KeyLookup(snapshot, o) : null;
        var fields = new List<Field>();
        var columns = new List<ResultColumn>();
        var encoders = new List<ColumnEncoder>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var dbCol = dbColumns.Count > i ? dbColumns[i] : null;
            var name = Unique(usedNames, string.IsNullOrEmpty(reader.GetName(i)) ? $"(No column name)" : reader.GetName(i));
            var typeName = SafeTypeName(reader, i);
            var clr = reader.GetFieldType(i);
            var enc = ColumnEncoder.Create(name, i, clr, typeName, dbCol, provider, cells, resultIndex);
            encoders.Add(enc);
            var meta = new Dictionary<string, string> { ["q:typeName"] = typeName, ["q:ordinal"] = i.ToString(CultureInfo.InvariantCulture) };
            var key = keys?.At(i);
            // The driver's answer reflects outer joins; the table definition is the fallback (PostgreSQL reports none).
            var nullable = dbCol?.AllowDBNull ?? key?.Column.Nullable;
            if (nullable is { } n) meta["q:nullable"] = n ? "1" : "0";
            if (key != null)
            {
                meta["q:source"] = key.Source;
                if (key.Column.Pk) meta["q:pk"] = "1";
                if (key.Fk != null) meta["q:fk"] = key.Fk;
            }
            if (enc.HasClass) meta["q:dynamic"] = "1";
            // M11: a number sent as exact text (beyond Decimal128 or Int64: an unconstrained PostgreSQL numeric, DuckDB
            // HUGEINT / UBIGINT / DECIMAL, MySQL BIGINT UNSIGNED, precision > 38) is still a number: the webview aligns and
            // sorts it as one.
            if (enc.ArrowType is StringType && !enc.HasClass && ColumnEncoder.IsNumber(clr)) meta["q:numeric"] = "1";
            fields.Add(new Field(name, enc.ArrowType, nullable: true, meta));
            var col = new ResultColumn
            {
                Name = name, TypeName = typeName, ArrowType = Describe(enc.ArrowType), Nullable = nullable ?? true, NullableKnown = nullable != null, Ordinal = i,
                Source = key?.Source, Pk = key?.Column.Pk ?? false, Fk = key?.Fk,
            };
            // Hidden siblings, in the order the encoder builds its arrays: truncation flag, offset, storage class.
            var siblings = new List<ResultColumn>();
            if (enc.HasFlag)
            {
                var flag = Unique(usedNames, name + "__truncated");
                fields.Add(new Field(flag, BooleanType.Default, true, new Dictionary<string, string> { ["q:hidden"] = "flag", ["q:of"] = name }));
                col = col with { TruncatedFlagColumn = flag };
                siblings.Add(new ResultColumn { Name = flag, TypeName = "bit", ArrowType = "bool", Nullable = true, Ordinal = -1, Hidden = true });
            }
            if (enc.HasOffset)
            {
                var off = Unique(usedNames, name + "__offset");
                fields.Add(new Field(off, Int16Type.Default, true, new Dictionary<string, string> { ["q:hidden"] = "offset", ["q:of"] = name }));
                col = col with { OffsetColumn = off };
                siblings.Add(new ResultColumn { Name = off, TypeName = "smallint", ArrowType = "int16", Nullable = true, Ordinal = -1, Hidden = true });
            }
            if (enc.HasClass)
            {
                var cls = Unique(usedNames, name + "__class");
                fields.Add(new Field(cls, UInt8Type.Default, false, new Dictionary<string, string> { ["q:hidden"] = "class", ["q:of"] = name }));
                col = col with { Dynamic = true, ClassColumn = cls };
                siblings.Add(new ResultColumn { Name = cls, TypeName = "tinyint", ArrowType = "uint8", Nullable = false, Ordinal = -1, Hidden = true });
            }
            columns.Add(col);
            columns.AddRange(siblings);
        }
        _encoders = encoders.ToArray();
        Schema = new Schema(fields, null);
        Columns = columns.ToArray();
    }

    sealed record KeyInfo(SnapColumn Column, string Source, string? Fk);

    /// <summary>Resolves origins against the snapshot: the source column, its primary-key flag and the first foreign key it belongs to.</summary>
    sealed class KeyLookup(SchemaSnapshot snapshot, ColumnOrigin?[] origins)
    {
        readonly Dictionary<long, SnapObject> _objects = snapshot.Objects.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());

        public KeyInfo? At(int i)
        {
            if (i >= origins.Length || origins[i] is not { } o || !_objects.TryGetValue(o.ObjectId, out var obj) || obj.Columns == null) return null;
            var c = obj.Columns.FirstOrDefault(x => x.Name == o.Column) ?? obj.Columns.FirstOrDefault(x => string.Equals(x.Name, o.Column, StringComparison.OrdinalIgnoreCase));
            if (c == null) return null;
            string? fk = null;
            foreach (var f in snapshot.Fks)
            {
                if (f.Parent != obj.Id) continue;
                var pair = f.Columns.FirstOrDefault(x => string.Equals(x.Parent, c.Name, StringComparison.OrdinalIgnoreCase));
                if (pair == null) continue;
                fk = _objects.TryGetValue(f.Referenced, out var r) ? $"{r.Schema}.{r.Name}.{pair.Referenced}" : pair.Referenced;
                break;
            }
            return new KeyInfo(c, $"{obj.Schema}.{obj.Name}.{c.Name}", fk);
        }
    }

    static string Unique(HashSet<string> used, string name)
    {
        var n = name;
        for (var k = 1; !used.Add(n); k++) n = $"{name}_{k}";
        return n;
    }

    static string SafeTypeName(DbDataReader r, int i)
    {
        try { return r.GetDataTypeName(i); } catch { return "unknown"; }
    }

    public static string Describe(IArrowType t) => t switch
    {
        Decimal128Type d => $"decimal128({d.Precision},{d.Scale})",
        TimestampType ts => ts.Timezone is { } tz ? $"timestamp[us,{tz}]" : "timestamp[us]",
        Time64Type => "time64[us]",
        StringType => "utf8",
        BinaryType => "binary",
        BooleanType => "bool",
        _ => t.Name.ToLowerInvariant(),
    };

    public long EstimatedBytes => _encoders.Sum(e => e.Bytes);

    /// <summary>Appends the reader's current row.</summary>
    public void AppendRow(DbDataReader reader, long rowIndex)
    {
        foreach (var e in _encoders) e.Append(reader, rowIndex);
        PendingRows++;
    }

    public bool ShouldFlush => PendingRows >= MaxBatchRows || EstimatedBytes >= MaxBatchBytes;

    public RecordBatch Flush()
    {
        var arrays = new List<IArrowArray>(Schema.FieldsList.Count);
        foreach (var e in _encoders) e.BuildInto(arrays);
        var batch = new RecordBatch(Schema, arrays, PendingRows);
        PendingRows = 0;
        return batch;
    }

    /// <summary>Self-contained Arrow IPC stream: schema + at most one batch (§5 data plane).</summary>
    public static byte[] ToIpc(Schema schema, RecordBatch? batch)
    {
        using var ms = new MemoryStream(batch == null ? 512 : 64 * 1024);
        using (var w = new ArrowStreamWriter(ms, schema, leaveOpen: true))
        {
            if (batch == null) w.WriteStart(); else w.WriteRecordBatch(batch);
            w.WriteEnd();
        }
        return ms.ToArray();
    }
}

/// <summary>Full values of truncated cells for query.cell, bounded per execution (§16 deviation).</summary>
public sealed class TruncatedCellStore
{
    public const long BudgetBytes = 64L * 1024 * 1024;
    readonly Dictionary<(int, long, int), object> _cells = new();
    long _bytes;

    public bool TryAdd(int resultIndex, long row, int col, object value, long bytes)
    {
        lock (_cells)
        {
            if (_bytes + bytes > BudgetBytes) return false;
            _cells[(resultIndex, row, col)] = value;
            _bytes += bytes;
            return true;
        }
    }

    public object? Get(int resultIndex, long row, int col)
    {
        lock (_cells) return _cells.GetValueOrDefault((resultIndex, row, col));
    }

    public void Clear()
    {
        lock (_cells) { _cells.Clear(); _bytes = 0; }
    }
}

abstract class ColumnEncoder(int ordinal)
{
    protected readonly int Ordinal = ordinal;
    public abstract IArrowType ArrowType { get; }
    public virtual bool HasFlag => false;
    public virtual bool HasOffset => false;
    /// <summary>Phase D0f: builds a per-cell storage-class sibling (dynamically typed column).</summary>
    public virtual bool HasClass => false;
    public long Bytes { get; protected set; }
    public abstract void Append(DbDataReader r, long rowIndex);
    public abstract void BuildInto(List<IArrowArray> arrays);

    public static ColumnEncoder Create(string name, int ordinal, Type? clr, string typeName, DbColumn? col, IDbProvider provider,
        TruncatedCellStore cells, int resultIndex)
    {
        var hint = col == null ? ColumnHint.Default : provider.GetColumnHint(col);
        var tn = typeName.ToLowerInvariant();
        if (hint is ColumnHint.ProviderString or ColumnHint.ProviderBytes) return new ProviderStringEncoder(ordinal, provider);
        if (hint == ColumnHint.Dynamic) return new DynamicEncoder(ordinal, cells, resultIndex);
        if (clr == typeof(bool)) return new BoolEncoder(ordinal);
        if (clr == typeof(byte)) return new Prim<byte, UInt8Array, UInt8Array.Builder>(ordinal, UInt8Type.Default, (r, i) => r.GetByte(i), 1);
        if (clr == typeof(sbyte)) return new Prim<sbyte, Int8Array, Int8Array.Builder>(ordinal, Int8Type.Default, (r, i) => (sbyte)r.GetValue(i), 1);
        if (clr == typeof(short)) return new Prim<short, Int16Array, Int16Array.Builder>(ordinal, Int16Type.Default, (r, i) => r.GetInt16(i), 2);
        if (clr == typeof(int)) return new Prim<int, Int32Array, Int32Array.Builder>(ordinal, Int32Type.Default, (r, i) => r.GetInt32(i), 4);
        if (clr == typeof(long)) return new Prim<long, Int64Array, Int64Array.Builder>(ordinal, Int64Type.Default, (r, i) => r.GetInt64(i), 8);
        if (clr == typeof(uint)) return new Prim<long, Int64Array, Int64Array.Builder>(ordinal, Int64Type.Default, (r, i) => Convert.ToInt64(r.GetValue(i)), 8);
        if (clr == typeof(float)) return new Prim<float, FloatArray, FloatArray.Builder>(ordinal, FloatType.Default, (r, i) => r.GetFloat(i), 4);
        if (clr == typeof(double)) return new Prim<double, DoubleArray, DoubleArray.Builder>(ordinal, DoubleType.Default, (r, i) => r.GetDouble(i), 8);
        if (clr == typeof(decimal))
        {
            var (p, s) = DecimalShape(tn, col);
            if (p is > 0 and <= 38 && s is >= 0 && s <= p) return new DecimalEncoder(ordinal, p.Value, s.Value, provider);
            return new ProviderStringEncoder(ordinal, provider);
        }
        if (clr == typeof(DateTimeOffset)) return new DateTimeOffsetEncoder(ordinal);
        if (clr == typeof(DateTime))
        {
            if (tn == "date") return new DateEncoder(ordinal);
            var utc = tn.Contains("with time zone") || tn == "timestamptz";
            return new TimestampEncoder(ordinal, utc ? "UTC" : null);
        }
        if (clr == typeof(DateOnly)) return new DateEncoder(ordinal);
        if (clr == typeof(TimeSpan) && (tn == "time" || tn.StartsWith("time without"))) return new TimeEncoder(ordinal);
        if (clr == typeof(TimeOnly)) return new TimeEncoder(ordinal);
        if (clr == typeof(Guid)) return new TextEncoder(ordinal, false, cells, resultIndex, r => r.GetGuid(ordinal).ToString("D"));
        if (clr == typeof(string))
        {
            var size = col?.ColumnSize;
            var large = hint == ColumnHint.LargeText || tn is "xml" or "text" or "ntext" or "json" or "jsonb" || size is null or <= 0 or >= 8000;
            // Stream only genuine character types; drivers refuse GetTextReader on xml/json and friends.
            var streamable = tn is "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext" or "character varying" or "character" or "bpchar";
            return new TextEncoder(ordinal, large, cells, resultIndex, streamable ? null : r => r.GetString(ordinal));
        }
        // A driver that reads binary values as streams (DuckDB.NET reports BLOB as Stream) goes through GetStream as well.
        if (clr == typeof(byte[]) || (clr != null && typeof(Stream).IsAssignableFrom(clr))) return new BinaryEncoder(ordinal, cells, resultIndex);
        if (clr == typeof(char[]) || clr == typeof(char)) return new TextEncoder(ordinal, false, cells, resultIndex, r => Convert.ToString(r.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "");
        return new TextEncoder(ordinal, true, cells, resultIndex, r => FormatValue(r.GetValue(ordinal)));
    }

    /// <summary>CLR types whose values are numbers even when they travel as text (M11: <c>q:numeric</c>).</summary>
    public static bool IsNumber(Type? clr) =>
        clr == typeof(decimal) || clr == typeof(ulong) || clr == typeof(ushort) || clr == typeof(System.Numerics.BigInteger)
        || clr == typeof(Int128) || clr == typeof(UInt128);

    static (int? P, int? S) DecimalShape(string tn, DbColumn? col)
    {
        if (tn == "money") return (19, 4);
        if (tn == "smallmoney") return (10, 4);
        var p = col?.NumericPrecision;
        var s = col?.NumericScale;
        if (p is null or 0 or 255) return (null, null);
        return (p, s is null or 255 ? 0 : s);
    }

    public static string FormatValue(object? v) => v switch
    {
        null or DBNull => "",
        string s => s,
        byte[] b => "0x" + Convert.ToHexString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable e => "{" + string.Join(",", e.Cast<object?>().Select(FormatValue)) + "}",
        _ => v.ToString() ?? "",
    };
}

sealed class Prim<T, TArray, TBuilder>(int ordinal, IArrowType type, Func<DbDataReader, int, T> read, int width) : ColumnEncoder(ordinal)
    where T : struct
    where TArray : IArrowArray
    where TBuilder : PrimitiveArrayBuilder<T, TArray, TBuilder>, new()
{
    TBuilder _b = new();
    public override IArrowType ArrowType => type;

    public override void Append(DbDataReader r, long rowIndex)
    {
        if (r.IsDBNull(Ordinal)) _b.AppendNull(); else _b.Append(read(r, Ordinal));
        Bytes += width;
    }

    public override void BuildInto(List<IArrowArray> arrays)
    {
        arrays.Add(_b.Build());
        _b = new TBuilder();
        Bytes = 0;
    }
}

sealed class BoolEncoder(int ordinal) : ColumnEncoder(ordinal)
{
    BooleanArray.Builder _b = new();
    public override IArrowType ArrowType => BooleanType.Default;

    public override void Append(DbDataReader r, long rowIndex)
    {
        if (r.IsDBNull(Ordinal)) _b.AppendNull(); else _b.Append(r.GetBoolean(Ordinal));
        Bytes += 1;
    }

    public override void BuildInto(List<IArrowArray> arrays) { arrays.Add(_b.Build()); _b = new(); Bytes = 0; }
}

sealed class DecimalEncoder(int ordinal, int precision, int scale, IDbProvider provider) : ColumnEncoder(ordinal)
{
    readonly Decimal128Type _type = new(precision, scale);
    Decimal128Array.Builder? _b;
    public override IArrowType ArrowType => _type;

    public override void Append(DbDataReader r, long rowIndex)
    {
        _b ??= new Decimal128Array.Builder(_type);
        if (r.IsDBNull(Ordinal)) _b.AppendNull();
        else if (precision > 28) _b.Append(provider.ReadAsString(r, Ordinal) ?? "0"); // beyond System.Decimal: exact via string
        else _b.Append(r.GetDecimal(Ordinal));
        Bytes += 16;
    }

    public override void BuildInto(List<IArrowArray> arrays)
    {
        arrays.Add((_b ??= new Decimal128Array.Builder(_type)).Build());
        _b = null;
        Bytes = 0;
    }
}

sealed class TimestampEncoder(int ordinal, string? tz) : ColumnEncoder(ordinal)
{
    readonly TimestampType _type = new(TimeUnit.Microsecond, tz);
    TimestampArray.Builder? _b;
    public override IArrowType ArrowType => _type;

    public override void Append(DbDataReader r, long rowIndex)
    {
        _b ??= new TimestampArray.Builder(_type);
        if (r.IsDBNull(Ordinal)) _b.AppendNull();
        else
        {
            var dt = r.GetDateTime(Ordinal);
            // Naive types keep wall-clock ticks; timestamptz arrives as UTC.
            _b.Append(new DateTimeOffset(DateTime.SpecifyKind(dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt, DateTimeKind.Unspecified), TimeSpan.Zero));
        }
        Bytes += 8;
    }

    public override void BuildInto(List<IArrowArray> arrays) { arrays.Add((_b ??= new(_type)).Build()); _b = null; Bytes = 0; }
}

sealed class DateTimeOffsetEncoder(int ordinal) : ColumnEncoder(ordinal)
{
    readonly TimestampType _type = new(TimeUnit.Microsecond, "UTC");
    TimestampArray.Builder? _b;
    Int16Array.Builder _off = new();
    public override IArrowType ArrowType => _type;
    public override bool HasOffset => true;

    public override void Append(DbDataReader r, long rowIndex)
    {
        _b ??= new TimestampArray.Builder(_type);
        if (r.IsDBNull(Ordinal)) { _b.AppendNull(); _off.AppendNull(); }
        else
        {
            var v = r.GetFieldValue<DateTimeOffset>(Ordinal);
            _b.Append(v.ToUniversalTime());
            _off.Append((short)v.Offset.TotalMinutes);
        }
        Bytes += 10;
    }

    public override void BuildInto(List<IArrowArray> arrays)
    {
        arrays.Add((_b ??= new(_type)).Build());
        arrays.Add(_off.Build());
        _b = null; _off = new(); Bytes = 0;
    }
}

sealed class DateEncoder(int ordinal) : ColumnEncoder(ordinal)
{
    Date32Array.Builder _b = new();
    public override IArrowType ArrowType => Date32Type.Default;

    public override void Append(DbDataReader r, long rowIndex)
    {
        if (r.IsDBNull(Ordinal)) _b.AppendNull();
        else
        {
            var v = r.GetValue(Ordinal);
            if (v is DateOnly d) _b.Append(d); else _b.Append(Convert.ToDateTime(v, CultureInfo.InvariantCulture).Date);
        }
        Bytes += 4;
    }

    public override void BuildInto(List<IArrowArray> arrays) { arrays.Add(_b.Build()); _b = new(); Bytes = 0; }
}

sealed class TimeEncoder(int ordinal) : ColumnEncoder(ordinal)
{
    static readonly Time64Type Type = new(TimeUnit.Microsecond);
    Time64Array.Builder _b = new(Type);
    public override IArrowType ArrowType => Type;

    public override void Append(DbDataReader r, long rowIndex)
    {
        if (r.IsDBNull(Ordinal)) _b.AppendNull();
        else
        {
            var v = r.GetValue(Ordinal);
            var ticks = v switch { TimeSpan ts => ts.Ticks, TimeOnly t => t.Ticks, _ => 0L };
            _b.Append(ticks / 10);
        }
        Bytes += 8;
    }

    public override void BuildInto(List<IArrowArray> arrays) { arrays.Add(_b.Build()); _b = new(Type); Bytes = 0; }
}

/// <summary>Utf8; unbounded types are truncated at 1 MB with a sibling flag column (§5).</summary>
sealed class TextEncoder(int ordinal, bool large, TruncatedCellStore cells, int resultIndex, Func<DbDataReader, string>? read) : ColumnEncoder(ordinal)
{
    StringArray.Builder _b = new();
    BooleanArray.Builder _flag = new();
    readonly char[] _buf = new char[64 * 1024];
    public override IArrowType ArrowType => StringType.Default;
    public override bool HasFlag => large;

    public override void Append(DbDataReader r, long rowIndex)
    {
        if (r.IsDBNull(Ordinal))
        {
            _b.AppendNull();
            if (large) _flag.Append(false);
            return;
        }
        if (!large)
        {
            var s = read != null ? read(r) : r.GetString(Ordinal);
            _b.Append(s);
            Bytes += s.Length * 2 + 4;
            return;
        }
        string value;
        bool truncated;
        if (read != null)
        {
            var full = read(r);
            truncated = full.Length > ResultSetEncoder.TextLimitChars;
            value = truncated ? full[..ResultSetEncoder.TextLimitChars] : full;
            if (truncated) cells.TryAdd(resultIndex, rowIndex, Ordinal, full, full.Length * 2L);
        }
        else
        {
            using var tr = r.GetTextReader(Ordinal);
            var sb = new StringBuilder();
            int n;
            while (sb.Length <= ResultSetEncoder.TextLimitChars && (n = tr.Read(_buf, 0, _buf.Length)) > 0) sb.Append(_buf, 0, n);
            truncated = sb.Length > ResultSetEncoder.TextLimitChars;
            if (truncated)
            {
                // Keep reading for query.cell while the per-execution budget allows; otherwise drain.
                while ((n = tr.Read(_buf, 0, _buf.Length)) > 0)
                    if (sb.Length < TruncatedCellStore.BudgetBytes / 2) sb.Append(_buf, 0, n);
                var full = sb.ToString();
                cells.TryAdd(resultIndex, rowIndex, Ordinal, full, full.Length * 2L);
                value = full[..ResultSetEncoder.TextLimitChars];
            }
            else value = sb.ToString();
        }
        _b.Append(value);
        _flag.Append(truncated);
        Bytes += value.Length * 2 + 5;
    }

    public override void BuildInto(List<IArrowArray> arrays)
    {
        arrays.Add(_b.Build());
        if (large) arrays.Add(_flag.Build());
        _b = new(); _flag = new(); Bytes = 0;
    }
}

/// <summary>Binary truncated at 64 KB with a sibling flag column (§5).</summary>
sealed class BinaryEncoder(int ordinal, TruncatedCellStore cells, int resultIndex) : ColumnEncoder(ordinal)
{
    BinaryArray.Builder _b = new();
    BooleanArray.Builder _flag = new();
    public override IArrowType ArrowType => BinaryType.Default;
    public override bool HasFlag => true;

    public override void Append(DbDataReader r, long rowIndex)
    {
        if (r.IsDBNull(Ordinal)) { _b.AppendNull(); _flag.Append(false); return; }
        using var s = r.GetStream(Ordinal);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length < TruncatedCellStore.BudgetBytes / 2) ms.Write(buf, 0, n);
            else if (ms.Length > ResultSetEncoder.BinaryLimitBytes) continue;
        }
        var all = ms.ToArray();
        var truncated = all.Length > ResultSetEncoder.BinaryLimitBytes;
        if (truncated) cells.TryAdd(resultIndex, rowIndex, Ordinal, all, all.Length);
        var value = truncated ? all.AsSpan(0, ResultSetEncoder.BinaryLimitBytes) : all;
        _b.Append(value);
        _flag.Append(truncated);
        Bytes += value.Length + 5;
    }

    public override void BuildInto(List<IArrowArray> arrays)
    {
        arrays.Add(_b.Build());
        arrays.Add(_flag.Build());
        _b = new(); _flag = new(); Bytes = 0;
    }
}

sealed class ProviderStringEncoder(int ordinal, IDbProvider provider) : ColumnEncoder(ordinal)
{
    StringArray.Builder _b = new();
    public override IArrowType ArrowType => StringType.Default;

    public override void Append(DbDataReader r, long rowIndex)
    {
        if (r.IsDBNull(Ordinal)) { _b.AppendNull(); return; }
        var s = provider.ReadAsString(r, Ordinal) ?? "";
        if (s.Length > ResultSetEncoder.TextLimitChars) s = s[..ResultSetEncoder.TextLimitChars];
        _b.Append(s);
        Bytes += s.Length * 2 + 4;
    }

    public override void BuildInto(List<IArrowArray> arrays) { arrays.Add(_b.Build()); _b = new(); Bytes = 0; }
}

/// <summary>
/// Phase D0f: a column whose values may be of any storage class (SQLite). One Utf8 array with lossless text — integers in
/// invariant decimal, reals in round-trip form ("NaN", "Infinity", "-Infinity" included), text as is, blobs in lowercase hex
/// — plus the class of each cell. Text and hex over the 1 MB limit are truncated, flagged, and kept for query.cell. The
/// schema never changes after the first row, whatever later rows hold.
/// </summary>
sealed class DynamicEncoder(int ordinal, TruncatedCellStore cells, int resultIndex) : ColumnEncoder(ordinal)
{
    StringArray.Builder _b = new();
    UInt8Array.Builder _c = new();
    BooleanArray.Builder _f = new();
    public override IArrowType ArrowType => StringType.Default;
    public override bool HasFlag => true;
    public override bool HasClass => true;

    public static (StorageClass Class, string? Text) Encode(object? v) => v switch
    {
        null or DBNull => (StorageClass.Null, null),
        long or int or short or sbyte or byte or ulong or uint or ushort => (StorageClass.Integer, Convert.ToString(v, CultureInfo.InvariantCulture)),
        double d => (StorageClass.Real, Real(d)),
        float f => (StorageClass.Real, Real(f)),
        decimal m => (StorageClass.Real, m.ToString(CultureInfo.InvariantCulture)),
        string s => (StorageClass.Text, s),
        byte[] b => (StorageClass.Blob, Convert.ToHexStringLower(b)),
        _ => (StorageClass.Text, ColumnEncoder.FormatValue(v)),
    };

    static string Real(double d) =>
        double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : d.ToString("R", CultureInfo.InvariantCulture);

    public override void Append(DbDataReader r, long rowIndex)
    {
        var (cls, text) = Encode(r.IsDBNull(Ordinal) ? null : r.GetValue(Ordinal));
        _c.Append((byte)cls);
        if (text == null) { _b.AppendNull(); _f.Append(false); Bytes += 2; return; }
        if (text.Length > ResultSetEncoder.TextLimitChars)
        {
            cells.TryAdd(resultIndex, rowIndex, Ordinal, text, text.Length * 2L);
            text = text[..ResultSetEncoder.TextLimitChars];
            _f.Append(true);
        }
        else _f.Append(false);
        _b.Append(text);
        Bytes += text.Length + 2;
    }

    public override void BuildInto(List<IArrowArray> arrays)
    {
        arrays.Add(_b.Build());
        arrays.Add(_f.Build());
        arrays.Add(_c.Build());
        _b = new(); _f = new(); _c = new();
        Bytes = 0;
    }
}
