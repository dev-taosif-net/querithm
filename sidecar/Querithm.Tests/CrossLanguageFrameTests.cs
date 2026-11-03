using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using Querithm.Contract.Providers;
using Querithm.Core.Execution;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// The data plane across languages (§5): frames the sidecar's encoder writes, as a committed golden file the webview's
/// own reader decodes in vitest (src/results/__tests__/frames.golden.test.ts, apache-arrow JS). One test owns the file: when the
/// encoder or the Arrow library changes the bytes, it rewrites the file and fails, so the change is re-checked by the JS
/// reader (run vitest) before it is committed — an Arrow upgrade (Phase D3) cannot change the wire format silently.
/// </summary>
public sealed class CrossLanguageFrameTests
{
    static string GoldenPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "package.json")) && Directory.Exists(Path.Combine(d.FullName, "src", "results")))
                return Path.Combine(d.FullName, "src", "results", "__tests__", "fixtures", "sidecar-frames.bin");
        throw new DirectoryNotFoundException("repository root");
    }

    /// <summary>Every column shape the encoder writes: typed columns through the generic paths, and one dynamic column.</summary>
    static DbDataReader Reader()
    {
        var t = new DataTable();
        t.Columns.Add("id", typeof(long));
        t.Columns.Add("i8", typeof(sbyte));
        t.Columns.Add("i16", typeof(short));
        t.Columns.Add("i32", typeof(int));
        t.Columns.Add("u64", typeof(ulong));
        t.Columns.Add("f32", typeof(float));
        t.Columns.Add("f64", typeof(double));
        t.Columns.Add("dec", typeof(decimal));
        t.Columns.Add("txt", typeof(string));
        t.Columns.Add("bin", typeof(byte[]));
        t.Columns.Add("b", typeof(bool));
        t.Columns.Add("dt", typeof(DateTime));
        t.Columns.Add("dto", typeof(DateTimeOffset));
        t.Columns.Add("tm", typeof(TimeSpan));
        t.Columns.Add("g", typeof(Guid));
        t.Columns.Add("dyn", typeof(object));
        t.Rows.Add(1L, (sbyte)-128, (short)-32768, int.MinValue, 0UL, 1.5f, 0.1, 12345.6789m, "héllo ✓", new byte[] { 0x00, 0xFF }, true,
            new DateTime(2026, 10, 31, 10, 20, 30, DateTimeKind.Unspecified).AddTicks(1234560), new DateTimeOffset(2026, 10, 31, 10, 20, 30, 123, TimeSpan.FromHours(6)),
            new TimeSpan(0, 10, 20, 30, 123), Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), 1L);
        t.Rows.Add(2L, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value,
            DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        t.Rows.Add(3L, (sbyte)127, (short)32767, int.MaxValue, ulong.MaxValue, -0.25f, 1e300, -0.0001m, "", Array.Empty<byte>(), false,
            new DateTime(1, 1, 1), new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.FromMinutes(-150)), new TimeSpan(0, 23, 59, 59, 999), Guid.Empty, "x");
        return new PreciseReader(t);
    }

    /// <summary>
    /// A DataTable carries no numeric precision: this reader delegates to one and declares "dec" decimal(18,4), as a server
    /// reports it, so it travels as Decimal128.
    /// </summary>
    sealed class PreciseReader(DataTable t) : DbDataReader, IDbColumnSchemaGenerator
    {
        readonly DataTableReader _r = t.CreateDataReader();

        sealed class Col : DbColumn
        {
            public Col(DbColumn c, int? precision, int? scale)
            {
                ColumnName = c.ColumnName; ColumnOrdinal = c.ColumnOrdinal; DataType = c.DataType; DataTypeName = c.DataTypeName;
                AllowDBNull = c.AllowDBNull; NumericPrecision = precision ?? c.NumericPrecision; NumericScale = scale ?? c.NumericScale;
            }
        }

        public ReadOnlyCollection<DbColumn> GetColumnSchema() =>
            _r.GetColumnSchema().Select(c => (DbColumn)(c.ColumnName == "dec" ? new Col(c, 18, 4) : new Col(c, null, null))).ToList().AsReadOnly();

        public override object this[int ordinal] => _r[ordinal];
        public override object this[string name] => _r[name];
        public override int Depth => _r.Depth;
        public override int FieldCount => _r.FieldCount;
        public override bool HasRows => _r.HasRows;
        public override bool IsClosed => _r.IsClosed;
        public override int RecordsAffected => _r.RecordsAffected;
        public override bool GetBoolean(int ordinal) => _r.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => _r.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => _r.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => _r.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => _r.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => _r.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => _r.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => _r.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => _r.GetDouble(ordinal);
        public override System.Collections.IEnumerator GetEnumerator() => _r.GetEnumerator();
        public override Type GetFieldType(int ordinal) => _r.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => _r.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => _r.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => _r.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => _r.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => _r.GetInt64(ordinal);
        public override string GetName(int ordinal) => _r.GetName(ordinal);
        public override int GetOrdinal(string name) => _r.GetOrdinal(name);
        public override string GetString(int ordinal) => _r.GetString(ordinal);
        public override object GetValue(int ordinal) => _r.GetValue(ordinal);
        public override int GetValues(object[] values) => _r.GetValues(values);
        public override bool IsDBNull(int ordinal) => _r.IsDBNull(ordinal);
        public override bool NextResult() => _r.NextResult();
        public override bool Read() => _r.Read();
    }

    /// <summary>The frames: [kind byte][int32 LE length][payload], a schema frame (0x01) then one batch frame (0x02).</summary>
    static byte[] Frames()
    {
        using var r = Reader();
        var enc = new ResultSetEncoder(r, new FakeDbProvider { Hint = c => c.ColumnName == "dyn" ? ColumnHint.Dynamic : ColumnHint.Default }, new TruncatedCellStore(), 0);
        long row = 0;
        while (r.Read()) enc.AppendRow(r, row++);
        using var ms = new MemoryStream();
        void Frame(byte kind, byte[] payload)
        {
            ms.WriteByte(kind);
            ms.Write(BitConverter.GetBytes(payload.Length));
            ms.Write(payload);
        }
        Frame(0x01, ResultSetEncoder.ToIpc(enc.Schema, null));
        using var batch = enc.Flush();
        Frame(0x02, ResultSetEncoder.ToIpc(enc.Schema, batch));
        return ms.ToArray();
    }

    [Fact]
    public void The_golden_frames_match_what_the_encoder_writes_today()
    {
        var path = GoldenPath();
        var now = Frames();
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (before != null && before.AsSpan().SequenceEqual(now)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, now);
        Assert.Fail($"The encoder's frames changed ({before?.Length ?? 0} → {now.Length} bytes): {path} was rewritten. " +
                    "Run the webview's golden test (npx vitest run src/results/__tests__/frames.golden.test.ts) against it, then commit the file.");
    }
}
