using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// Data compare on both fixture engines and across them: classes, normalised values, the sync script run on
/// the target followed by a compare that finds nothing, keys that sort differently per collation, queries, the cap.
/// </summary>
[Collection("datacompare")]
public class DataCompareIntegrationTests
{
    const string Db = "querithm_edit";

    public static TheoryData<string> Engines => TestEngines.Names;

    static async Task Ok(TestHost h, string sid, string sql)
    {
        var (done, _, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)) + done.Error?.Message + "\n" + sql);
    }

    static async Task<(Profile Profile, string Session)> OpenAsync(TestHost h, string engine)
    {
        if (engine == "mssql")
        {
            await h.EnsureDatabaseAsync(Engine.Mssql, Db);
            return await h.OpenMssqlAsync(Db);
        }
        await h.EnsureDatabaseAsync(Engine.Pg, Db);
        return await h.OpenPgAsync(Db);
    }

    static async Task RefreshAsync(TestHost h, Profile p)
    {
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = Db, Full = true });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, Db);
    }

    static async Task<DataCompareProgressEvent> WaitAsync(TestHost h, string compareId)
    {
        for (var i = 0; i < 600; i++)
        {
            var done = h.Notifier.Of<DataCompareProgressEvent>().FirstOrDefault(e => e.CompareId == compareId && e.Done);
            if (done != null) return done;
            await Task.Delay(50);
        }
        throw new TimeoutException("data compare did not finish");
    }

    static List<RecordBatch> Diffs(TestHost h, string executionId) =>
        h.Frames.For(executionId).Where(f => f.Kind == 2).Select(f => TestHost.ReadBatch(f.Payload)).ToList();

    static string S(string engine, string table) => engine == "mssql" ? $"dbo.{table}" : table;

    static string Setup(string engine, string table) => engine == "mssql"
        ? $"""
          DROP TABLE IF EXISTS dbo.{table};
          CREATE TABLE dbo.{table} (id int NOT NULL PRIMARY KEY, name nvarchar(50) NULL, price decimal(10,2) NULL, ok bit NULL, at datetime2(0) NULL,
            g uniqueidentifier NULL, fl float NULL, code char(5) NULL, total AS (id * 2));
          """
        : $"""
          DROP TABLE IF EXISTS {table};
          CREATE TABLE {table} (id int PRIMARY KEY, name text, price numeric(10,2), ok boolean, at timestamp(0), g uuid, fl float8, code char(5),
            total int GENERATED ALWAYS AS (id * 2) STORED);
          """;

    static string Rows(string engine, string table, params string[] rows)
    {
        var t = S(engine, table);
        var cols = "(id, name, price, ok, at, g, fl, code)";
        return string.Join("\n", rows.Select(r => $"INSERT INTO {t} {cols} VALUES {(engine == "mssql" ? r.Replace("true", "1").Replace("false", "0") : r)};"));
    }

    static DataCompareSide Table(string sid, string engine, string table) =>
        new() { SessionId = sid, Database = Db, Schema = engine == "mssql" ? "dbo" : "public", Table = table };

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Compare_sync_and_compare_again_finds_nothing(string engine)
    {
        await using var h = new TestHost();
        var (p, sid) = await OpenAsync(h, engine);
        await Ok(h, sid, Setup(engine, "q_dc_src") + "\n" + Setup(engine, "q_dc_tgt"));
        await Ok(h, sid, Rows(engine, "q_dc_src",
            "(1, 'one', 1.50, true, '2026-10-21 10:00:00', '6F9619FF-8B86-D011-B42D-00C04FC964FF', 0.1, 'ab')",
            "(2, 'two', 2.00, false, '2026-10-21 11:00:00', NULL, 1e10, NULL)",
            "(3, 'three', 3.00, NULL, NULL, NULL, NULL, 'x')",
            "(4, 'four', 4.00, true, NULL, NULL, NULL, NULL)"));
        await Ok(h, sid, Rows(engine, "q_dc_tgt",
            // Equal after normalisation: scale, GUID case, char padding, float within epsilon.
            "(1, 'one', 1.5, true, '2026-10-21 10:00:00', '6f9619ff-8b86-d011-b42d-00c04fc964ff', 0.1000000000000001, 'ab   ')",
            "(2, 'TWO', 2.00, false, '2026-10-21 11:00:00', NULL, 1e10, NULL)",
            "(3, NULL, 3.00, NULL, NULL, NULL, NULL, 'x')",
            "(5, 'five', 5.00, false, NULL, NULL, NULL, NULL)"));
        await RefreshAsync(h, p);

        var start = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(sid, engine, "q_dc_src"), Target = Table(sid, engine, "q_dc_tgt") });
        Assert.Equal(["id"], start.Key.Select(k => k.Source));
        Assert.Contains(start.Columns, c => c.Source == "total");
        var done = await WaitAsync(h, start.CompareId);
        Assert.Null(done.Error);
        Assert.Equal((1L, 2L, 1L, 1L, 4L, 4L), (done.Identical, done.Different, done.SourceOnly, done.TargetOnly, done.SourceRows, done.TargetRows));

        var batch = Assert.Single(Diffs(h, start.ExecutionId));
        Assert.Equal(4, batch.Length);
        var kinds = (UInt8Array)batch.Column("_diff_kind");
        var ids = (StringArray)batch.Column("id");
        var row2 = Enumerable.Range(0, batch.Length).Single(i => ids.GetString(i) == "2");
        Assert.Equal((byte)1, kinds.GetValue(row2));
        Assert.Equal("two", ((StringArray)batch.Column("name")).GetString(row2));
        Assert.Equal("TWO", ((StringArray)batch.Column("name__target")).GetString(row2));
        var mask = ((BinaryArray)batch.Column("_diff_mask")).GetBytes(row2).ToArray();
        var nameBit = start.Columns.ToList().FindIndex(c => c.Source == "name");
        Assert.True((mask[nameBit >> 3] & (1 << (nameBit & 7))) != 0);
        var priceBit = start.Columns.ToList().FindIndex(c => c.Source == "price");
        Assert.True((mask[priceBit >> 3] & (1 << (priceBit & 7))) == 0);

        var sync = await h.Rpc.DataCompareSyncSql(new DataSyncSqlParams { CompareId = start.CompareId, Insert = true, Update = true, Delete = true });
        Assert.Equal(4, sync.Rows);
        Assert.DoesNotContain("total", sync.Sql.Split('\n').Where(l => l.StartsWith("INSERT") || l.StartsWith("UPDATE")).Aggregate("", (a, b) => a + b));
        await Ok(h, sid, sync.Sql);

        var again = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(sid, engine, "q_dc_src"), Target = Table(sid, engine, "q_dc_tgt") });
        var after = await WaitAsync(h, again.CompareId);
        Assert.True(after.Different + after.SourceOnly + after.TargetOnly == 0, $"{after.Different}/{after.SourceOnly}/{after.TargetOnly}\n{sync.Sql}");
        Assert.Equal(4, after.Identical);
    }

    [IntegrationFact]
    public async Task The_same_table_on_both_engines_compares_equal_including_mixed_case_and_guid_keys()
    {
        await using var h = new TestHost();
        var (mp, ms) = await OpenAsync(h, "mssql");
        var (pp, pg) = await OpenAsync(h, "pg");
        var words = new[] { "a", "B", "c", "Zeta", "alpha", "Ärger", "zz", "_x", "0", "€" };
        var guids = Enumerable.Range(0, 200).Select(i => new Guid(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i))[..16])).ToArray();
        await Ok(h, ms, $"""
            DROP TABLE IF EXISTS dbo.q_dc_words; DROP TABLE IF EXISTS dbo.q_dc_guids; DROP TABLE IF EXISTS dbo.q_dc_x;
            CREATE TABLE dbo.q_dc_words (w nvarchar(20) COLLATE Latin1_General_CI_AS NOT NULL PRIMARY KEY, n int NOT NULL);
            CREATE TABLE dbo.q_dc_guids (g uniqueidentifier NOT NULL PRIMARY KEY, n int NOT NULL);
            CREATE TABLE dbo.q_dc_x (id int NOT NULL, part nvarchar(10) NOT NULL, price decimal(10,2) NULL, ok bit NULL, at datetime2(0) NULL, PRIMARY KEY (id, part));
            INSERT dbo.q_dc_words VALUES {string.Join(", ", words.Select((w, i) => $"(N'{w}', {i})"))};
            INSERT dbo.q_dc_guids VALUES {string.Join(", ", guids.Select((g, i) => $"('{g}', {i})"))};
            INSERT dbo.q_dc_x VALUES (1, N'a', 1.50, 1, '2026-10-21 10:00:00'), (1, N'B', 2.00, 0, NULL), (2, N'a', NULL, NULL, '2026-01-01 00:00:00');
            """);
        await Ok(h, pg, $"""
            DROP TABLE IF EXISTS q_dc_words; DROP TABLE IF EXISTS q_dc_guids; DROP TABLE IF EXISTS q_dc_x;
            CREATE TABLE q_dc_words (w text PRIMARY KEY, n int NOT NULL);
            CREATE TABLE q_dc_guids (g uuid PRIMARY KEY, n int NOT NULL);
            CREATE TABLE q_dc_x (id int, part text, price numeric(10,2), ok boolean, at timestamp(0), PRIMARY KEY (id, part));
            INSERT INTO q_dc_words VALUES {string.Join(", ", words.Select((w, i) => $"('{w}', {i})"))};
            INSERT INTO q_dc_guids VALUES {string.Join(", ", guids.Select((g, i) => $"('{g}', {i})"))};
            INSERT INTO q_dc_x VALUES (1, 'a', 1.5, true, '2026-10-21 10:00:00'), (1, 'B', 2, false, NULL), (2, 'a', NULL, NULL, '2026-01-01 00:00:00');
            """);
        await RefreshAsync(h, mp);
        await RefreshAsync(h, pp);
        foreach (var t in new[] { "q_dc_words", "q_dc_guids", "q_dc_x" })
        {
            var start = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(ms, "mssql", t), Target = Table(pg, "pg", t) });
            var done = await WaitAsync(h, start.CompareId);
            Assert.True(done.Error == null && done.Different + done.SourceOnly + done.TargetOnly == 0,
                $"{t}: {done.Error} {done.Different}/{done.SourceOnly}/{done.TargetOnly}");
            Assert.True(done.Identical > 0);
        }
        // One changed value is one difference, in either direction.
        await Ok(h, pg, "UPDATE q_dc_x SET price = 9.99 WHERE id = 1 AND part = 'B'");
        var back = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(pg, "pg", "q_dc_x"), Target = Table(ms, "mssql", "q_dc_x") });
        var d = await WaitAsync(h, back.CompareId);
        Assert.Equal((2L, 1L, 0L, 0L), (d.Identical, d.Different, d.SourceOnly, d.TargetOnly));
        var sync = await h.Rpc.DataCompareSyncSql(new DataSyncSqlParams { CompareId = back.CompareId });
        Assert.Contains("UPDATE [dbo].[q_dc_x] SET [price] = ", sync.Sql);
        Assert.Contains("WHERE [id] = ", sync.Sql);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Queries_need_a_key_are_read_only_and_keys_must_be_unique(string engine)
    {
        await using var h = new TestHost();
        var (p, sid) = await OpenAsync(h, engine);
        await Ok(h, sid, Setup(engine, "q_dc_src") + "\n" + Setup(engine, "q_dc_tgt"));
        await Ok(h, sid, Rows(engine, "q_dc_src", "(1, 'one', 1, true, NULL, NULL, NULL, NULL)", "(2, 'two', 2, true, NULL, NULL, NULL, NULL)"));
        await Ok(h, sid, Rows(engine, "q_dc_tgt", "(1, 'one', 1, true, NULL, NULL, NULL, NULL)"));
        await RefreshAsync(h, p);
        var query = new DataCompareSide { SessionId = sid, Database = Db, Query = $"SELECT id, name FROM {S(engine, "q_dc_src")} WHERE id < 10" };

        var noKey = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.DataCompareStart(new DataCompareParams { Source = query, Target = query }));
        Assert.Equal("compareKey", noKey.Code);

        var start = await h.Rpc.DataCompareStart(new DataCompareParams { Source = query, Target = Table(sid, engine, "q_dc_tgt"), Key = ["id"] });
        Assert.Equal(["name"], start.Columns.Select(c => c.Source));
        Assert.Contains(start.Notes, n => n.Contains("Only in the target"));
        var done = await WaitAsync(h, start.CompareId);
        Assert.Equal((1L, 1L), (done.Identical, done.SourceOnly));
        var reverse = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(sid, engine, "q_dc_tgt"), Target = query, Key = ["id"] });
        await WaitAsync(h, reverse.CompareId);
        var intoQuery = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.DataCompareSyncSql(new DataSyncSqlParams { CompareId = reverse.CompareId }));
        Assert.Equal("compareTarget", intoQuery.Code);

        // Writes are refused: by the AST on SQL Server, by a READ ONLY transaction on PostgreSQL.
        var write = engine == "mssql" ? "DELETE FROM dbo.q_dc_src" : "WITH d AS (DELETE FROM q_dc_src RETURNING *) SELECT * FROM d";
        await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.DataCompareStart(new DataCompareParams
        {
            Source = query with { Query = write }, Target = Table(sid, engine, "q_dc_tgt"), Key = ["id"],
        }));
        if (engine == "mssql")
            Assert.Equal("compareQuery", (await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.DataCompareStart(new DataCompareParams
            {
                Source = query with { Query = "SELECT id INTO dbo.q_dc_copy FROM dbo.q_dc_src" }, Target = Table(sid, engine, "q_dc_tgt"), Key = ["id"],
            }))).Code);
        var (_, count, _) = await h.RunAsync(sid, $"SELECT COUNT(*) AS n FROM {S(engine, "q_dc_src")}");
        Assert.Equal(2L, count[0].Column(0) is Int32Array a ? a.GetValue(0) : ((Int64Array)count[0].Column(0)).GetValue(0));

        var dup = await h.Rpc.DataCompareStart(new DataCompareParams
        {
            Source = query with { Query = "SELECT 1 AS id, 'a' AS name UNION ALL SELECT 1, 'b'" }, Target = Table(sid, engine, "q_dc_tgt"), Key = ["id"],
        });
        var failed = await WaitAsync(h, dup.CompareId);
        Assert.Contains("not unique", failed.Error);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Only_the_first_differences_are_kept_and_the_script_says_so(string engine)
    {
        await using var h = new TestHost();
        var (p, sid) = await OpenAsync(h, engine);
        await Ok(h, sid, Setup(engine, "q_dc_src") + "\n" + Setup(engine, "q_dc_tgt"));
        await Ok(h, sid, Rows(engine, "q_dc_src", Enumerable.Range(1, 5).Select(i => $"({i}, 'n{i}', {i}, true, NULL, NULL, NULL, NULL)").ToArray()));
        await RefreshAsync(h, p);
        var start = await h.Rpc.DataCompareStart(new DataCompareParams
        {
            Source = Table(sid, engine, "q_dc_src"), Target = Table(sid, engine, "q_dc_tgt"), Options = new DataCompareOptions { MaxDiffRows = 2 },
        });
        var done = await WaitAsync(h, start.CompareId);
        Assert.True(done.Truncated);
        Assert.Equal(5, done.SourceOnly);
        Assert.Equal(2, Diffs(h, start.ExecutionId).Sum(b => b.Length));
        var sync = await h.Rpc.DataCompareSyncSql(new DataSyncSqlParams { CompareId = start.CompareId });
        Assert.Equal(2, sync.Rows);
        Assert.Contains(sync.Notes, n => n.Contains("first 2"));
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Queries_with_ctes_order_by_and_trailing_comments_compare(string engine)
    {
        await using var h = new TestHost();
        var (p, sid) = await OpenAsync(h, engine);
        await Ok(h, sid, Setup(engine, "q_dc_src") + "\n" + Setup(engine, "q_dc_tgt"));
        await Ok(h, sid, Rows(engine, "q_dc_src", "(1, 'one', 1, true, NULL, NULL, NULL, NULL)", "(2, 'two', 2, true, NULL, NULL, NULL, NULL)"));
        await Ok(h, sid, Rows(engine, "q_dc_tgt", "(1, 'one', 1, true, NULL, NULL, NULL, NULL)"));
        await RefreshAsync(h, p);
        string[] queries =
        [
            $"WITH c AS (SELECT id, name FROM {S(engine, "q_dc_src")}) SELECT id, name FROM c;",
            $"SELECT id, name FROM {S(engine, "q_dc_src")} ORDER BY name DESC",
            $"SELECT id, name FROM {S(engine, "q_dc_src")} -- every row",
        ];
        foreach (var q in queries)
        {
            var start = await h.Rpc.DataCompareStart(new DataCompareParams
            {
                Source = new DataCompareSide { SessionId = sid, Database = Db, Query = q }, Target = Table(sid, engine, "q_dc_tgt"), Key = ["id"],
            });
            var done = await WaitAsync(h, start.CompareId);
            Assert.True(done.Error == null, $"{q}: {done.Error}");
            Assert.Equal((1L, 1L), (done.Identical, done.SourceOnly));
        }
    }
}
