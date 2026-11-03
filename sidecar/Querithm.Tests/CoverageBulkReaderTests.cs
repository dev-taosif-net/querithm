using System.Data.Common;
using System.Reflection;
using Querithm.Contract.Providers;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>The reader SqlBulkCopy pulls rows from: typed values, per-row rejection of values the column cannot hold, chunking.</summary>
public class SqlServerBulkReaderTests
{
    sealed class Rows(params string?[][] data) : IBulkRows
    {
        int _i = -1;
        public List<(long Row, string Reason)> Rejected { get; } = [];
        public bool Read() => ++_i < data.Length;
        public IReadOnlyList<string?> Current => data[_i];
        public long Row => _i + 1;
        public void Reject(string reason) => Rejected.Add((Row, reason));
        public void Loaded(long totalRows, bool committed) { }
    }

    static readonly BulkTarget Target = new("dbo", "t",
    [
        new BulkColumn("id", "int"), new BulkColumn("name", "nvarchar(5)"), new BulkColumn("raw", "varbinary(max)"),
        new BulkColumn("born", "date"), new BulkColumn("uid", "uniqueidentifier"), new BulkColumn("amt", "decimal(10,2)"), new BulkColumn("big", "bigint"),
        new BulkColumn("short", "smallint"), new BulkColumn("ratio", "float"), new BulkColumn("flag", "bit"), new BulkColumn("untyped", null),
    ]);

    static DbDataReader Create(IBulkRows rows)
    {
        var type = typeof(SqlServerBulkLoader).GetNestedType("ConvertingReader", BindingFlags.NonPublic)!;
        return (DbDataReader)Activator.CreateInstance(type, Target, rows)!;
    }

    static string?[] Row(string? id = "7", string? name = "abc", string? raw = "0x0102", string? born = "2026-05-01", string? uid = "8c4a2d0b-1f2e-4c3d-9a1b-0123456789ab",
        string? amt = "12.50", string? big = "9000000000", string? shrt = "12", string? ratio = "1.5", string? flag = "true", string? untyped = "free text") =>
        [id, name, raw, born, uid, amt, big, shrt, ratio, flag, untyped];

    [Fact]
    public void Values_arrive_as_typed_clr_values_and_nulls_as_DBNull()
    {
        var rows = new Rows(Row(), Row(name: null));
        using var r = Create(rows);
        Assert.Equal(11, r.FieldCount);
        Assert.True(r.Read());
        Assert.Equal(7, r.GetInt32(0));
        Assert.Equal(7L, r.GetInt64(0));
        Assert.Equal((short)7, r.GetInt16(0));
        Assert.Equal("abc", r.GetString(1));
        Assert.Equal(new byte[] { 1, 2 }, (byte[])r.GetValue(2));
        Assert.Equal(new DateTime(2026, 5, 1), r.GetDateTime(3));
        Assert.Equal(Guid.Parse("8c4a2d0b-1f2e-4c3d-9a1b-0123456789ab"), r.GetGuid(4));
        Assert.Equal(12.5m, Convert.ToDecimal(r.GetValue(5).ToString(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(9_000_000_000L, r.GetInt64(6));
        Assert.Equal(1.5, r.GetDouble(8));
        Assert.Equal(1.5f, r.GetFloat(8));
        Assert.True(r.GetBoolean(9));
        Assert.Equal("free text", r.GetString(10));
        Assert.False(r.IsDBNull(1));
        var all = new object[11];
        Assert.Equal(11, r.GetValues(all));
        Assert.Equal(7, all[0]);
        Assert.Equal(3, r.GetValues(new object[3]));
        Assert.True(r.Read());
        Assert.True(r.IsDBNull(1));
        Assert.Equal(DBNull.Value, r.GetValue(1));
        Assert.False(r.Read());
        Assert.Empty(rows.Rejected);
    }

    [Fact]
    public void Column_metadata_comes_from_the_target()
    {
        using var r = Create(new Rows(Row()));
        Assert.Equal("name", r.GetName(1));
        Assert.Equal(1, r.GetOrdinal("NAME"));
        Assert.Throws<IndexOutOfRangeException>(() => r.GetOrdinal("nope"));
        Assert.Equal("nvarchar(5)", r.GetDataTypeName(1));
        Assert.Equal("nvarchar", r.GetDataTypeName(10));
        r.Read();
        Assert.Equal(typeof(int), r.GetFieldType(0));
        Assert.Equal(7, r[0]);
        Assert.Equal("abc", r["name"]);
        Assert.True(r.HasRows);
        Assert.False(r.IsClosed);
        Assert.Equal(-1, r.RecordsAffected);
        Assert.Equal(0, r.Depth);
        Assert.False(r.NextResult());
        Assert.Throws<NotSupportedException>(() => r.GetEnumerator());
    }

    [Fact]
    public void Chars_and_bytes_can_be_read_in_pieces()
    {
        using var r = Create(new Rows(Row()));
        r.Read();
        Assert.Equal('a', r.GetChar(1));
        Assert.Equal(3, r.GetChars(1, 0, null, 0, 0));
        var chars = new char[2];
        Assert.Equal(2, r.GetChars(1, 1, chars, 0, 2));
        Assert.Equal("bc", new string(chars));
        Assert.Equal(0, r.GetChars(1, 3, chars, 0, 2));
        Assert.Equal(2, r.GetBytes(2, 0, null, 0, 0));
        var bytes = new byte[4];
        Assert.Equal(1, r.GetBytes(2, 1, bytes, 1, 4));
        Assert.Equal(new byte[] { 0, 2, 0, 0 }, bytes);
        Assert.Equal(0, r.GetBytes(2, 2, bytes, 0, 4));
        Assert.Equal((byte)1, ((byte[])r.GetValue(2))[0]);
    }

    [Fact]
    public async Task Rows_with_unconvertible_or_too_long_values_are_rejected_and_the_rest_still_load()
    {
        var rows = new Rows(Row(id: "1"), Row(id: "not a number"), Row(id: "3", name: "too long for five"), Row(id: "4", born: "31/12/2026"), Row(id: "5"));
        using var r = Create(rows);
        var ids = new List<int>();
        while (await r.ReadAsync(CancellationToken.None)) ids.Add(r.GetInt32(0));
        Assert.Equal([1, 5], ids);
        Assert.Equal([2L, 3L, 4L], rows.Rejected.Select(x => x.Row));
        Assert.Contains("id:", rows.Rejected[0].Reason);
        Assert.Contains("17 characters, the column holds 5", rows.Rejected[1].Reason);
        Assert.Contains("born:", rows.Rejected[2].Reason);
    }

    [Fact]
    public void A_chunk_stops_at_its_limit_and_reports_that_more_may_follow()
    {
        var rows = new Rows(Row(id: "1"), Row(id: "2"), Row(id: "3"));
        using var r = Create(rows);
        var type = r.GetType();
        void Start(int n) => type.GetMethod("StartChunk")!.Invoke(r, [n]);
        bool More() => (bool)type.GetProperty("More")!.GetValue(r)!;
        long Sent() => (long)type.GetProperty("ChunkSent")!.GetValue(r)!;

        Start(2);
        Assert.True(r.Read());
        Assert.True(r.Read());
        Assert.False(r.Read());
        Assert.True(More());
        Assert.Equal(2, Sent());
        Start(10);
        Assert.True(r.Read());
        Assert.Equal(3, r.GetInt32(0));
        Assert.False(r.Read());
        Assert.False(More());
        Assert.Equal(1, Sent());
    }
}
