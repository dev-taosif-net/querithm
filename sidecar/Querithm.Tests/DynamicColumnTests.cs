using System.Data;
using Apache.Arrow;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Execution;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Phase D0f: a column the engine does not type reliably (SQLite) gets one stable schema — Utf8 with lossless text and a
/// per-cell storage class in a hidden sibling — whatever the rows hold, before or after the first batch.
/// </summary>
public class DynamicColumnTests
{
    static readonly FakeDbProvider Untrusting = new() { Hint = c => c.ColumnName == "v" ? ColumnHint.Dynamic : ColumnHint.Default };

    static DataTableReader Reader(params object?[] values)
    {
        var t = new DataTable();
        t.Columns.Add("id", typeof(long));
        t.Columns.Add("v", typeof(object));
        long i = 0;
        foreach (var v in values) t.Rows.Add(++i, v ?? DBNull.Value);
        return t.CreateDataReader();
    }

    static (List<RecordBatch> Batches, ResultSetEncoder Encoder, TruncatedCellStore Cells) Encode(DataTableReader r)
    {
        var cells = new TruncatedCellStore();
        var enc = new ResultSetEncoder(r, Untrusting, cells, 0);
        var batches = new List<RecordBatch>();
        long row = 0;
        while (r.Read())
        {
            enc.AppendRow(r, row++);
            if (enc.ShouldFlush) batches.Add(TestHost.ReadBatch(ResultSetEncoder.ToIpc(enc.Schema, enc.Flush())));
        }
        if (enc.PendingRows > 0) batches.Add(TestHost.ReadBatch(ResultSetEncoder.ToIpc(enc.Schema, enc.Flush())));
        return (batches, enc, cells);
    }

    [Fact]
    public void Mixed_storage_classes_travel_as_lossless_text_with_each_cells_class()
    {
        var (batches, enc, _) = Encode(Reader(null, 2L, 10L, "2", 0.1 + 0.2, double.NaN, new byte[] { 0x0A, 0xFF }, ""));
        var b = Assert.Single(batches);
        var v = (StringArray)b.Column("v");
        var cls = (UInt8Array)b.Column("v__class");
        Assert.True(v.IsNull(0));
        Assert.Equal(["2", "10", "2", "0.30000000000000004", "NaN", "0aff", ""], Enumerable.Range(1, 7).Select(i => v.GetString(i)));
        Assert.Equal([0, 1, 1, 3, 2, 2, 4, 3], Enumerable.Range(0, 8).Select(i => (int)cls.GetValue(i)!.Value));
        var col = enc.Columns.Single(c => c.Name == "v");
        Assert.Equal((true, "v__class", "utf8"), (col.Dynamic, col.ClassColumn, col.ArrowType));
        Assert.Contains(enc.Columns, c => c.Name == "v__class" && c.Hidden);
        Assert.Equal("1", enc.Schema.GetFieldByName("v").Metadata["q:dynamic"]);
        // The trusted column keeps its native type.
        Assert.IsType<Int64Array>(b.Column("id"));
    }

    [Fact]
    public void An_empty_result_still_has_the_stable_schema()
    {
        var (batches, enc, _) = Encode(Reader());
        Assert.Empty(batches);
        using var reader = new Apache.Arrow.Ipc.ArrowStreamReader(new MemoryStream(ResultSetEncoder.ToIpc(enc.Schema, null)));
        var schema = reader.Schema;
        Assert.Equal(["id", "v", "v__truncated", "v__class"], schema.FieldsList.Select(f => f.Name));
    }

    [Fact]
    public void A_type_change_after_the_first_batch_keeps_the_schema()
    {
        var values = Enumerable.Range(0, ResultSetEncoder.MaxBatchRows + 5).Select(i => (object?)(long)i).Concat(["text after", new byte[] { 1 }]).ToArray();
        var (batches, _, _) = Encode(Reader(values));
        Assert.Equal(2, batches.Count);
        Assert.Equal(batches[0].Schema.FieldsList.Select(f => (f.Name, f.DataType.TypeId)), batches[1].Schema.FieldsList.Select(f => (f.Name, f.DataType.TypeId)));
        var last = batches[1];
        var v = (StringArray)last.Column("v");
        var cls = (UInt8Array)last.Column("v__class");
        Assert.Equal(("text after", (byte)StorageClass.Text), (v.GetString(last.Length - 2), cls.GetValue(last.Length - 2)!.Value));
        Assert.Equal(("01", (byte)StorageClass.Blob), (v.GetString(last.Length - 1), cls.GetValue(last.Length - 1)!.Value));
    }

    [Fact]
    public void Large_values_are_truncated_flagged_and_kept_whole_for_the_cell_viewer()
    {
        var big = new string('x', ResultSetEncoder.TextLimitChars + 10);
        var (batches, _, cells) = Encode(Reader(big, "small"));
        var b = Assert.Single(batches);
        Assert.Equal(ResultSetEncoder.TextLimitChars, ((StringArray)b.Column("v")).GetString(0).Length);
        Assert.True(((BooleanArray)b.Column("v__truncated")).GetValue(0));
        Assert.False(((BooleanArray)b.Column("v__truncated")).GetValue(1));
        Assert.Equal(big, cells.Get(0, 0, 1));
    }

    [Theory]
    [InlineData(-9223372036854775808L, "-9223372036854775808")]
    [InlineData(9223372036854775807L, "9223372036854775807")]
    public void Integers_round_trip_exactly(long value, string text) =>
        Assert.Equal((StorageClass.Integer, text), DynamicEncoder.Encode(value));

    [Fact]
    public void Reals_round_trip_exactly()
    {
        foreach (var d in new[] { 0.1, 1e-300, double.MaxValue, -0.0, 1.0 })
        {
            var (cls, text) = DynamicEncoder.Encode(d);
            Assert.Equal(StorageClass.Real, cls);
            Assert.Equal(d, double.Parse(text!, System.Globalization.CultureInfo.InvariantCulture));
        }
        Assert.Equal("Infinity", DynamicEncoder.Encode(double.PositiveInfinity).Text);
        Assert.Equal("-Infinity", DynamicEncoder.Encode(double.NegativeInfinity).Text);
    }

    // ---------------------------------------------------------------------------------------------- editing

    static SchemaSnapshot Snap() => new()
    {
        ProfileId = "p", Database = "main", Status = SnapshotStatus.Fresh, Fks = [],
        Objects =
        [
            new SnapObject
            {
                Id = 1, Schema = "main", Name = "t", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "id", Type = "int", Pk = true, Ordinal = 1 }, new SnapColumn { Name = "v", Type = "nvarchar(50)", Nullable = true, Ordinal = 2 }],
            },
        ],
    };

    [Fact]
    public void A_dynamic_column_is_locked_and_a_dynamic_key_makes_the_result_read_only()
    {
        var dialect = new SqlServerDataEdit();
        ResultColumn Col(string name, int ordinal, bool dynamic) => new() { Name = name, TypeName = "x", ArrowType = "utf8", Ordinal = ordinal, Dynamic = dynamic };
        ColumnOrigin?[] origins = [new ColumnOrigin(1, "id"), new ColumnOrigin(1, "v")];
        var edit = EditabilityAnalyzer.Analyze(dialect, [Col("id", 0, false), Col("v", 1, true)], origins, Snap(), _ => []);
        Assert.True(edit.Editable);
        var v = edit.Columns.Single(c => c.Column == "v");
        Assert.True(v.Locked);
        Assert.Contains("no fixed type", v.LockReason);
        var keyDynamic = EditabilityAnalyzer.Analyze(dialect, [Col("id", 0, true), Col("v", 1, false)], origins, Snap(), _ => []);
        Assert.False(keyDynamic.Editable);
        Assert.Contains("key column id has no fixed type", keyDynamic.Reason);
    }
}
