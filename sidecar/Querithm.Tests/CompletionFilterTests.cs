using System.Diagnostics;
using System.Text.Json;
using Querithm.Contract;
using Querithm.Core.Metadata;
using Querithm.Intelligence.TSql.Completion;
using Xunit.Abstractions;

namespace Querithm.Tests;

public class CompletionFilterTests(ITestOutputHelper output)
{
    static CompletionItem I(string label, CompletionKind kind = CompletionKind.Table, string? sort = null) => new() { Label = label, Kind = kind, SortText = sort };

    [Fact]
    public void Keeps_prefix_hump_and_substring_matches_of_the_word_being_typed()
    {
        CompletionItem[] items = [I("Orders"), I("OrderItems"), I("tblMenu"), I("Customers"), I("[Order Details]"), I("sales.Products"), I("(all columns)", CompletionKind.Snippet)];
        string[] Labels(string text) => CompletionFilter.Apply(items, text, text.Length).Select(i => i.Label).ToArray();
        Assert.Equal(["Orders", "OrderItems", "[Order Details]", "(all columns)"], Labels("SELECT * FROM ord"));
        Assert.Contains("OrderItems", Labels("SELECT * FROM oi"));      // camel hump
        Assert.Contains("tblMenu", Labels("SELECT * FROM menu"));      // substring
        Assert.Contains("sales.Products", Labels("SELECT * FROM prod")); // last dotted part
        Assert.Contains("[Order Details]", Labels("SELECT * FROM [Order")); // an opened bracket ends the word
        Assert.Equal(items.Length, Labels("SELECT * FROM ").Length);     // nothing typed: everything (under the cap)
    }

    [Fact]
    public void Caps_the_answer_at_tier_1s_limit_in_rank_order()
    {
        var items = Enumerable.Range(0, 2000).Select(n => I($"t{n:D4}", sort: $"2{n:D4}")).Reverse().ToArray();
        var kept = CompletionFilter.Apply(items, "SELECT * FROM t", 15);
        Assert.Equal(CompletionFilter.MaxItems, kept.Length);
        Assert.Equal("t0000", kept[0].Label);
        Assert.Equal($"t{CompletionFilter.MaxItems - 1:D4}", kept[^1].Label);
    }

    /// <summary>§14 #5b: tier 2 on a 10,000-table schema — payload and time, unfiltered vs filtered.</summary>
    [Fact]
    [Trait("Category", "Measure")]
    public void On_10000_tables_the_answer_is_capped_and_much_smaller()
    {
        var cols = Enumerable.Range(0, 30).Select(c => new SnapColumn { Name = $"col{c}", Type = "int", Ordinal = c + 1 }).ToArray();
        var snap = new SchemaSnapshot
        {
            ProfileId = "p1", Database = "SalesDB", Status = SnapshotStatus.Fresh, Fks = [],
            Objects = [.. Enumerable.Range(0, 10_000).Select(n => new SnapObject { Id = n + 1, Schema = "dbo", Name = $"Table{n:D5}", Kind = ObjectKind.Table, Columns = cols })],
        };
        var schema = new FakeSchema(snap, "SalesDB");
        const string text = "SELECT * FROM Table01";
        CompletionItem[] Raw() => TSqlCompletion.Complete(new CompleteParams { DocId = "m10k", Text = text, Offset = text.Length, Dialect = Dialect.Tsql, ProfileId = "p1", Database = "SalesDB" }, schema);
        Raw(); // warm-up
        var raw = Raw();
        var filtered = CompletionFilter.Apply(raw, text, text.Length);
        var rawBytes = JsonSerializer.SerializeToUtf8Bytes(raw).Length;
        var filteredBytes = JsonSerializer.SerializeToUtf8Bytes(filtered).Length;
        double P95(bool filter)
        {
            var times = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                var r = Raw();
                if (filter) r = CompletionFilter.Apply(r, text, text.Length);
                JsonSerializer.SerializeToUtf8Bytes(r);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return times[(int)(times.Count * 0.95)];
        }
        var before = P95(false);
        var after = P95(true);
        output.WriteLine($"10k tables, 'Table01': {raw.Length} items / {rawBytes / 1024.0:F0} KiB unfiltered → {filtered.Length} items / {filteredBytes / 1024.0:F1} KiB; complete+serialize p95 {before:F1} ms unfiltered, {after:F1} ms filtered");
        Assert.True(filtered.Length <= CompletionFilter.MaxItems);
        Assert.All(filtered.Where(i => i.Kind == CompletionKind.Table), i => Assert.Contains("Table01", i.Label));
        Assert.True(filteredBytes * 10 < rawBytes, $"{filteredBytes} vs {rawBytes}");
    }
}
