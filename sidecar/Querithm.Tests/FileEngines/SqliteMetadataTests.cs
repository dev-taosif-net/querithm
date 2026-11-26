using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Querithm.Contract.Providers;
using Querithm.Providers.Sqlite;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// SQLite catalog reads (§8) against temp-dir files, completion audit 2026-11-25: a view over a dropped table no longer
/// fails every refresh of the file (H14), a foreign key written without a column list names the referenced primary key
/// (M26), and an incremental read of one table does not read every table's columns (M23).
/// </summary>
[Collection("timing")] // two wall-clock ratios
public sealed class SqliteMetadataTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("querithm-sqlite-meta-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* a handle the OS still closes */ }
    }

    async Task<SqliteConnection> Open(string setup)
    {
        var c = new SqliteConnection($"Data Source={Path.Combine(_dir, Guid.NewGuid() + ".db")};Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = setup;
        await cmd.ExecuteNonQueryAsync();
        return c;
    }

    static readonly SqliteMetadata Meta = new();

    static async Task<MetadataBundle> DetailsOfAll(SqliteConnection c)
    {
        var stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        return await Meta.GetDetailsAsync(c, stamps.Select(s => s.Id).ToList(), CancellationToken.None);
    }

    static long Id(IReadOnlyList<ObjectStamp> stamps, string name) => stamps.Single(s => s.Name == name && s.Kind != Contract.ObjectKind.Trigger).Id;

    [Fact]
    public async Task A_view_over_a_dropped_table_leaves_the_rest_of_the_refresh_intact()
    {
        await using var c = await Open("""
            CREATE TABLE t (a INTEGER, b TEXT);
            CREATE VIEW v AS SELECT a, b FROM t;
            CREATE TABLE u (x INTEGER PRIMARY KEY, y TEXT);
            CREATE VIEW w AS SELECT x FROM u;
            DROP TABLE t;
            """);
        var stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        var bundle = await DetailsOfAll(c);
        Assert.Equal(["x", "y"], bundle.Columns.Where(col => col.ObjectId == Id(stamps, "u")).Select(col => col.Name));
        Assert.Equal(["x"], bundle.Columns.Where(col => col.ObjectId == Id(stamps, "w")).Select(col => col.Name));
        Assert.DoesNotContain(bundle.Columns, col => col.ObjectId == Id(stamps, "v")); // it has no readable columns
    }

    [Fact]
    public async Task A_foreign_key_without_a_column_list_references_the_parents_primary_key()
    {
        await using var c = await Open("""
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers);
            CREATE TABLE pair (a INTEGER, b INTEGER, PRIMARY KEY (b, a));
            CREATE TABLE child (x INTEGER, y INTEGER, FOREIGN KEY (x, y) REFERENCES pair);
            CREATE TABLE keyless (v TEXT);
            CREATE TABLE dangling (k INTEGER REFERENCES keyless);
            """);
        var stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        var bundle = await DetailsOfAll(c);
        var orders = bundle.Fks.Single(f => f.Parent == Id(stamps, "orders"));
        Assert.Equal(Id(stamps, "customers"), orders.Referenced);
        Assert.Equal([("customer_id", "id")], orders.Columns.Select(p => (p.Parent, p.Referenced)));
        // A composite key in its declared order (PRIMARY KEY (b, a)): x → b, y → a.
        var child = bundle.Fks.Single(f => f.Parent == Id(stamps, "child"));
        Assert.Equal([("x", "b"), ("y", "a")], child.Columns.Select(p => (p.Parent, p.Referenced)));
        // A parent without a primary key cannot be referenced this way (SQLite reports a mismatch on use): no join pair is made up.
        Assert.DoesNotContain(bundle.Fks, f => f.Parent == Id(stamps, "dangling"));
        Assert.DoesNotContain(bundle.Fks, f => f.Columns.Any(p => p.Referenced.Length == 0));
    }

    [Fact]
    public async Task A_view_that_cannot_be_described_says_why()
    {
        // M11: it was listed without columns and without a word; the explorer now shows the reason next to it.
        await using var c = await Open("CREATE TABLE t (a INTEGER); CREATE VIEW v AS SELECT a FROM t; DROP TABLE t;");
        var stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        var bundle = await DetailsOfAll(c);
        var note = Assert.Single(bundle.Notes ?? [], n => n.ObjectId == Id(stamps, "v"));
        Assert.StartsWith("invalid: its columns cannot be read (", note.Note);
        Assert.Contains("no such table", note.Note);
    }

    [Fact]
    public async Task A_view_is_read_again_once_the_table_it_reads_is_created_again()
    {
        // M11: a view's marker was its own SQL only, so after its table came back the incremental refresh never read it
        // again and it kept no columns until a full refresh.
        await using var c = await Open("CREATE TABLE t (a INTEGER, b TEXT); CREATE VIEW v AS SELECT a, b FROM t; DROP TABLE t;");
        var before = await Meta.GetStampsAsync(c, CancellationToken.None);
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE t (a INTEGER, b TEXT);";
            await cmd.ExecuteNonQueryAsync();
        }
        var after = await Meta.GetStampsAsync(c, CancellationToken.None);
        var v = Id(after, "v");
        Assert.NotEqual(before.Single(s => s.Id == v).Marker, after.Single(s => s.Id == v).Marker);
        var bundle = await Meta.GetDetailsAsync(c, [v], CancellationToken.None);
        Assert.Equal(["a", "b"], bundle.Columns.Where(col => col.ObjectId == v).Select(col => col.Name));
        Assert.Empty(bundle.Notes ?? []);
        // Nothing changed: the marker stays (no view is read again on every refresh).
        Assert.Equal(after.Single(s => s.Id == v).Marker, (await Meta.GetStampsAsync(c, CancellationToken.None)).Single(s => s.Id == v).Marker);
    }

    [Fact]
    public async Task A_foreign_key_to_a_parent_that_cannot_be_described_is_left_out_and_the_read_goes_on()
    {
        // G#11: the parent of an FK written without a column list was described without a guard; a view over a dropped
        // table there failed every refresh of the file.
        await using var c = await Open("""
            CREATE TABLE p (x INTEGER PRIMARY KEY);
            CREATE VIEW pv AS SELECT x FROM p;
            CREATE TABLE child (a INTEGER REFERENCES pv);
            CREATE TABLE customers (id INTEGER PRIMARY KEY);
            CREATE TABLE orders (customer_id INTEGER REFERENCES customers);
            DROP TABLE p;
            """);
        var stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        var bundle = await DetailsOfAll(c);
        Assert.DoesNotContain(bundle.Fks, f => f.Parent == Id(stamps, "child"));
        Assert.Single(bundle.Fks, f => f.Parent == Id(stamps, "orders"));
    }

    /// <summary>
    /// Code audit 2026-11-25 (H7 / L14): a view's marker hashed every table and view of main and temp, but a view of main
    /// can read only main (SQLite refuses a reference to temp there). A tab's TEMP table changed every main view's marker
    /// in the shared store, and the next tab's refresh changed it back — every view read again on each refresh.
    /// </summary>
    [Fact]
    public async Task A_temp_table_leaves_the_markers_of_main_views_as_they_were()
    {
        await using var c = await Open("CREATE TABLE t (a INTEGER); CREATE VIEW v AS SELECT a FROM t; CREATE TEMP VIEW tv AS SELECT a FROM t;");
        var before = await Meta.GetStampsAsync(c, CancellationToken.None);
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TEMP TABLE scratch (x INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        }
        var after = await Meta.GetStampsAsync(c, CancellationToken.None);
        long Marker(IReadOnlyList<ObjectStamp> s, string schema, string name) => s.Single(x => x.Schema == schema && x.Name == name).Marker;
        Assert.Equal(Marker(before, "main", "v"), Marker(after, "main", "v"));
        // A temp view can read temp tables: its marker covers them.
        Assert.NotEqual(Marker(before, "temp", "tv"), Marker(after, "temp", "tv"));
    }

    /// <summary>
    /// Fourth round (L5): REFERENCES parent without columns names the parent's key; a parent created (or changed) after the
    /// child changes the child's foreign key, so it changes the child's marker and an incremental refresh reads it again.
    /// </summary>
    [Fact]
    public async Task A_keyless_reference_follows_its_parent()
    {
        await using var c = await Open("CREATE TABLE child (id INTEGER PRIMARY KEY, p INTEGER REFERENCES parent);");
        var before = await Meta.GetStampsAsync(c, CancellationToken.None);
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE parent (pid INTEGER PRIMARY KEY);";
            await cmd.ExecuteNonQueryAsync();
        }
        var after = await Meta.GetStampsAsync(c, CancellationToken.None);
        Assert.NotEqual(before.Single(s => s.Name == "child").Marker, after.Single(s => s.Name == "child").Marker);
        var bundle = await Meta.GetDetailsAsync(c, [Id(after, "child")], CancellationToken.None);
        Assert.Equal([("p", "pid")], bundle.Fks.Single().Columns.Select(x => (x.Parent, x.Referenced)));
        // A table without such a reference keeps its marker.
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE other (x INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(after.Single(s => s.Name == "child").Marker, (await Meta.GetStampsAsync(c, CancellationToken.None)).Single(s => s.Name == "child").Marker);
    }

    /// <summary>Views of main and temp are described in one query (audit M6), and one view that fails still leaves the rest.</summary>
    [Fact]
    public async Task Views_of_both_schemas_get_their_columns_and_a_broken_one_its_note()
    {
        await using var c = await Open("""
            CREATE TABLE t (a INTEGER, b TEXT);
            CREATE VIEW v1 AS SELECT a FROM t;
            CREATE VIEW v2 AS SELECT b, a FROM t;
            CREATE TEMP VIEW tv AS SELECT b FROM t;
            """);
        var stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        var bundle = await DetailsOfAll(c);
        Assert.Equal(["a"], bundle.Columns.Where(col => col.ObjectId == Id(stamps, "v1")).Select(col => col.Name));
        Assert.Equal(["b", "a"], bundle.Columns.Where(col => col.ObjectId == Id(stamps, "v2")).Select(col => col.Name));
        Assert.Equal(["b"], bundle.Columns.Where(col => col.ObjectId == Id(stamps, "tv")).Select(col => col.Name));
        Assert.Empty(bundle.Notes ?? []);

        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE u (x INTEGER); CREATE VIEW bad AS SELECT x FROM u; DROP TABLE u;";
            await cmd.ExecuteNonQueryAsync();
        }
        stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        bundle = await DetailsOfAll(c);
        Assert.Equal(["b", "a"], bundle.Columns.Where(col => col.ObjectId == Id(stamps, "v2")).Select(col => col.Name));
        Assert.Single(bundle.Notes ?? [], n => n.ObjectId == Id(stamps, "bad"));
    }

    /// <summary>
    /// Code audit 2026-11-25 (L15) feared an extended result code (SQLITE_ERROR_MISSING_COLLSEQ, 257) would fail the read;
    /// Microsoft.Data.Sqlite reports the primary code in SqliteErrorCode, so a view naming a collation this connection
    /// lacks is a broken view like any other, not a failed read.
    /// </summary>
    [Fact]
    public async Task A_view_with_a_missing_collation_is_listed_with_a_note()
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".db");
        await using (var setup = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await setup.OpenAsync();
            setup.CreateCollation("querithm_test", string.CompareOrdinal);
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (a TEXT); CREATE VIEW v AS SELECT a COLLATE querithm_test AS a FROM t; CREATE VIEW w AS SELECT a FROM t;";
            await cmd.ExecuteNonQueryAsync();
        }
        await using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        await c.OpenAsync();
        var stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        var bundle = await DetailsOfAll(c);
        Assert.Equal(["a"], bundle.Columns.Where(col => col.ObjectId == Id(stamps, "w")).Select(col => col.Name));
        // Described in the batch, or alone with the reason when SQLite cannot: never a failed read.
        Assert.True(bundle.Columns.Any(col => col.ObjectId == Id(stamps, "v")) || (bundle.Notes ?? []).Any(n => n.ObjectId == Id(stamps, "v")));
        // Alone (the path a single asked view takes), it says why.
        var alone = await Meta.GetDetailsAsync(c, [Id(stamps, "v")], CancellationToken.None);
        Assert.True(alone.Columns.Count > 0 || (alone.Notes ?? []).Any(n => n.ObjectId == Id(stamps, "v")));
    }

    /// <summary>A file of <paramref name="tables"/> tables of 20 columns, each with an index.</summary>
    static string Wide(int tables) => "BEGIN;\n" + string.Join("\n", Enumerable.Range(0, tables).Select(i =>
        $"CREATE TABLE t{i} ({string.Join(", ", Enumerable.Range(0, 20).Select(k => $"c{k} INTEGER"))}); CREATE INDEX ix{i} ON t{i} (c1);")) + "\nCOMMIT;";

    static double Ms(Func<Task> work)
    {
        work().GetAwaiter().GetResult(); // warm
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            work().GetAwaiter().GetResult();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public async Task An_incremental_read_of_one_table_does_not_read_every_tables_columns()
    {
        await using var c = await Open(Wide(2000));
        var stamps = await Meta.GetStampsAsync(c, CancellationToken.None);
        var all = stamps.Select(s => s.Id).ToList();
        var one = new List<long> { Id(stamps, "t1234") };
        var allMs = Ms(() => Meta.GetDetailsAsync(c, all, CancellationToken.None));
        var oneMs = Ms(() => Meta.GetDetailsAsync(c, one, CancellationToken.None));
        output.WriteLine($"details: one table {oneMs:F1} ms, all 2,000 {allMs:F1} ms");
        Assert.True(oneMs * 10 < allMs, $"one table {oneMs:F1} ms, all 2,000 {allMs:F1} ms");
        var bundle = await Meta.GetDetailsAsync(c, one, CancellationToken.None);
        Assert.Equal(20, bundle.Columns.Count);
        Assert.Single(bundle.Indexes);
    }

    [Fact]
    public async Task Stamps_grow_linearly_with_the_number_of_tables()
    {
        await using var small = await Open(Wide(500));
        await using var big = await Open(Wide(4000));
        var smallMs = Ms(() => Meta.GetStampsAsync(small, CancellationToken.None));
        var bigMs = Ms(() => Meta.GetStampsAsync(big, CancellationToken.None));
        output.WriteLine($"stamps: 500 tables {smallMs:F1} ms, 4,000 tables {bigMs:F1} ms");
        // 8× the tables: linear work is about 8×, the earlier scan of every entry per table about 64×.
        Assert.True(bigMs < smallMs * 20, $"500 tables {smallMs:F1} ms, 4,000 tables {bigMs:F1} ms");
    }
}
