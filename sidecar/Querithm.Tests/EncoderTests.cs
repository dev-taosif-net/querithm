using System.Data;
using Apache.Arrow;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Store;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

public class EncoderTests
{
    static DataTableReader Reader()
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(long));
        t.Columns.Add("Name", typeof(string)).MaxLength = 50;
        t.Columns.Add("Amount", typeof(decimal));
        t.Columns.Add("When", typeof(DateTime));
        t.Columns.Add("Flag", typeof(bool));
        t.Columns.Add("Blob", typeof(byte[]));
        t.Columns.Add("Uid", typeof(Guid));
        t.Rows.Add(1L, "Acme", 14420.50m, new DateTime(2026, 10, 19, 14, 22, 1, 402), true, new byte[] { 1, 2, 3 }, Guid.Empty);
        t.Rows.Add(2L, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, new byte[70_000], Guid.Empty);
        return t.CreateDataReader();
    }

    [Fact]
    public void Encodes_rows_to_self_contained_ipc_streams()
    {
        using var r = Reader();
        var cells = new TruncatedCellStore();
        var enc = new ResultSetEncoder(r, new SqlServerProvider(), cells, 0);
        long row = 0;
        while (r.Read()) enc.AppendRow(r, row++);
        using var batch = enc.Flush();
        var ipc = ResultSetEncoder.ToIpc(enc.Schema, batch);
        var back = TestHost.ReadBatch(ipc);

        Assert.Equal(2, back.Length);
        Assert.Equal(1L, ((Int64Array)back.Column("Id")).GetValue(0));
        Assert.Equal("Acme", ((StringArray)back.Column("Name")).GetString(0));
        Assert.True(((StringArray)back.Column("Name")).IsNull(1));
        // A DataTable decimal has no declared precision, so it is carried exactly as text (§5: unknown precision → Utf8).
        Assert.Equal("14420.50", ((StringArray)back.Column("Amount")).GetString(0));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 14, 22, 1, 402, TimeSpan.Zero), ((TimestampArray)back.Column("When")).GetTimestamp(0));
        Assert.True(((BooleanArray)back.Column("Flag")).GetValue(0));

        // Binary over 64 KB is truncated, flagged, and the full value kept for query.cell.
        var blob = (BinaryArray)back.Column("Blob");
        Assert.Equal(64 * 1024, blob.GetBytes(1).Length);
        Assert.True(((BooleanArray)back.Column("Blob__truncated")).GetValue(1));
        Assert.Equal(70_000, ((byte[])cells.Get(0, 1, 5)!).Length);
        Assert.Contains(enc.Columns, c => c.Name == "Blob__truncated" && c.Hidden);
    }

    [Fact]
    public void Schema_frame_decodes_without_rows()
    {
        using var r = Reader();
        var enc = new ResultSetEncoder(r, new SqlServerProvider(), new TruncatedCellStore(), 0);
        var ipc = ResultSetEncoder.ToIpc(enc.Schema, null);
        using var reader = new Apache.Arrow.Ipc.ArrowStreamReader(new MemoryStream(ipc));
        Assert.Null(reader.ReadNextRecordBatch());
        Assert.Equal("Id", reader.Schema.FieldsList[0].Name);
        Assert.IsType<Apache.Arrow.Types.Int64Type>(reader.Schema.FieldsList[0].DataType);
    }

    [Fact]
    public async Task Synthetic_result_streams_in_batches_of_ten_thousand()
    {
        var frames = new List<(byte Kind, byte[] Payload)>();
        await foreach (var f in SyntheticResult.Generate(25_000, 40, CancellationToken.None)) frames.Add(f);
        Assert.Equal(FrameKind.Schema, frames[0].Kind);
        var batches = frames.Where(f => f.Kind == FrameKind.Batch).Select(f => TestHost.ReadBatch(f.Payload)).ToList();
        Assert.Equal([10_000, 10_000, 5_000], batches.Select(b => b.Length));
        Assert.Equal(40, batches[0].ColumnCount);
        Assert.Equal(FrameKind.EndOfResultSet, frames[^1].Kind);
    }

    [Fact]
    public void Ulid_roundtrips()
    {
        var u = Ulid.New();
        var s = u.ToString();
        Assert.Equal(26, s.Length);
        Assert.Equal(u.Bytes, Ulid.Parse(s).Bytes);
    }

    [Fact]
    public void Statistics_messages_parse()
    {
        var io = StatisticsParser.Parse("Table 'Orders'. Scan count 1, logical reads 152410, physical reads 0, page server reads 0, read-ahead reads 12, lob logical reads 0.", 0)!;
        Assert.Equal("io", io.Kind);
        Assert.Equal("Orders", io.Table);
        Assert.Equal(152410, io.LogicalReads);
        Assert.Equal(12, io.ReadAheadReads);
        var time = StatisticsParser.Parse("\n SQL Server Execution Times:\n   CPU time = 15 ms,  elapsed time = 118 ms.", 0)!;
        Assert.Equal("time", time.Kind);
        Assert.Equal(118, time.ElapsedMs);
        Assert.Equal("compile", StatisticsParser.Parse("SQL Server parse and compile time: \n   CPU time = 0 ms, elapsed time = 2 ms.", 0)!.Kind);
    }

    [Fact]
    public void History_search_treats_input_as_text_not_fts_syntax()
    {
        var store = new HistoryStore(LocalStore.InMemory());
        store.Add("p", "db", "SELECT * FROM dbo.Orders WHERE Status = 'open'", 10, 5, "done", null);
        store.Add("p", "db", "EXEC dbo.usp_GetCustomerOrders @CustomerId = 42", 10, 5, "done", null);
        Assert.Single(store.Search(new Contract.HistorySearchParams { Text = "usp_GetCust" }));
        Assert.Single(store.Search(new Contract.HistorySearchParams { Text = "orders status" }));
        Assert.Empty(store.Search(new Contract.HistorySearchParams { Text = "\" OR NEAR(" }));
    }
}
