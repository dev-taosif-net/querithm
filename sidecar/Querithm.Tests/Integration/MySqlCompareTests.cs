using Apache.Arrow;
using MySqlConnector;
using Querithm.Contract;
using static Querithm.Tests.Integration.MySqlIntegrationTests;
using MyHost = Querithm.Tests.Integration.MySqlIntegrationTests.Host;

namespace Querithm.Tests.Integration;

/// <summary>Phase D2 data compare on MySQL 8.4, MySQL 9.7 and MariaDB 11.8 (QUERITHM_IT=1).</summary>
public sealed class MySqlCompareTests
{
    static async Task ExecAsync(string product, string sql)
    {
        await EnsureDatabaseAsync(product);
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False;Database={Db(product)}");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task RefreshAsync(MyHost h, Profile p, string sid, string product, params string[] tables)
    {
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = Db(product), SessionId = sid, Full = true });
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = Db(product), SessionId = sid });
            if (tables.All(t => snap.Objects.Any(o => o.Name == t && o.Columns is { Length: > 0 }))) return;
            await Task.Delay(50);
        }
    }

    static async Task<DataCompareProgressEvent> WaitAsync(MyHost h, string compareId)
    {
        for (var i = 0; i < 600; i++)
        {
            if (h.Notifier.Of<DataCompareProgressEvent>().FirstOrDefault(e => e.CompareId == compareId && e.Done) is { } done) return done;
            await Task.Delay(50);
        }
        throw new TimeoutException("data compare did not finish");
    }

    static DataCompareSide Side(string sid, string product, string table) => new() { SessionId = sid, Database = Db(product), Schema = Db(product), Table = table };

    static string Table(string name) => $"""
        DROP TABLE IF EXISTS {name};
        CREATE TABLE {name} (id INT PRIMARY KEY, name VARCHAR(50), price DECIMAL(10,2), ok TINYINT(1), at DATETIME, fl DOUBLE, code CHAR(5),
          total INT GENERATED ALWAYS AS (id * 2) STORED);
        """;

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Compare_sync_and_compare_again_finds_nothing(string product)
    {
        await ExecAsync(product, Table("c_src") + Table("c_tgt") + """
            INSERT INTO c_src (id, name, price, ok, at, fl, code) VALUES
              (1, 'one', 1.50, 1, '2026-10-21 10:00:00', 0.1, 'ab'), (2, 'two', 2.00, 0, '2026-10-21 11:00:00', 1e10, NULL),
              (3, 'three', 3.00, NULL, NULL, NULL, 'x'), (4, 'four', 4.00, 1, NULL, NULL, NULL);
            INSERT INTO c_tgt (id, name, price, ok, at, fl, code) VALUES
              (1, 'one', 1.5, 1, '2026-10-21 10:00:00', 0.1000000000000001, 'ab'), (2, 'TWO', 2.00, 0, '2026-10-21 11:00:00', 1e10, NULL),
              (3, NULL, 3.00, NULL, NULL, NULL, 'x'), (5, 'five', 5.00, 0, NULL, NULL, NULL);
            """);
        var (h, p, sid) = await OpenAsync(product);
        await using var _ = h;
        await RefreshAsync(h, p, sid, product, "c_src", "c_tgt");
        Assert.True((await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid })).DataCompare.Available);

        var start = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Side(sid, product, "c_src"), Target = Side(sid, product, "c_tgt") });
        Assert.Equal(["id"], start.Key.Select(k => k.Source));
        var done = await WaitAsync(h, start.CompareId);
        Assert.Null(done.Error);
        // 'TWO' vs 'two' differs although the column's collation folds case: the compare reads the exact text.
        Assert.Equal((1L, 2L, 1L, 1L), (done.Identical, done.Different, done.SourceOnly, done.TargetOnly));

        var sync = await h.Rpc.DataCompareSyncSql(new DataSyncSqlParams { CompareId = start.CompareId, Insert = true, Update = true, Delete = true });
        Assert.Equal(4, sync.Rows);
        Assert.StartsWith("START TRANSACTION;", sync.Sql.Split('\n').First(l => l.Length > 0 && !l.StartsWith("--")));
        Assert.DoesNotContain("`total`", string.Concat(sync.Sql.Split('\n').Where(l => l.StartsWith("INSERT") || l.StartsWith("UPDATE"))));
        var (applied, _, _) = await Run(h, sid, sync.Sql);
        Assert.Null(applied.Error);

        var again = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Side(sid, product, "c_src"), Target = Side(sid, product, "c_tgt") });
        var after = await WaitAsync(h, again.CompareId);
        Assert.True(after.Different + after.SourceOnly + after.TargetOnly == 0, $"{after.Different}/{after.SourceOnly}/{after.TargetOnly}\n{sync.Sql}");
        Assert.Equal(4, after.Identical);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Text_keys_merge_in_the_same_order_on_the_server_and_in_the_compare(string product)
    {
        // Under the column's case- and accent-insensitive collation the server would sort 'a' < 'B' < 'm' < 'Ö' < 'z'; the compare's
        // ORDER BY reads UTF-8 bytes instead ('B' < 'a' < 'm' < 'z' < 'Ö'), the order its merge expects.
        await ExecAsync(product, """
            DROP TABLE IF EXISTS k_src; DROP TABLE IF EXISTS k_tgt;
            CREATE TABLE k_src (k VARCHAR(10) PRIMARY KEY, v INT); CREATE TABLE k_tgt (k VARCHAR(10) PRIMARY KEY, v INT);
            INSERT INTO k_src VALUES ('a', 1), ('B', 2), ('Ö', 3), ('z', 4), ('m', 5);
            INSERT INTO k_tgt VALUES ('a', 1), ('B', 2), ('Ö', 30), ('z', 4), ('n', 6);
            """);
        var (h, p, sid) = await OpenAsync(product);
        await using var _ = h;
        await RefreshAsync(h, p, sid, product, "k_src", "k_tgt");
        var start = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Side(sid, product, "k_src"), Target = Side(sid, product, "k_tgt") });
        var done = await WaitAsync(h, start.CompareId);
        Assert.Null(done.Error);
        Assert.Equal((3L, 1L, 1L, 1L), (done.Identical, done.Different, done.SourceOnly, done.TargetOnly));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task A_compared_query_must_be_one_read(string product)
    {
        await ExecAsync(product, Table("c_src"));
        var (h, p, sid) = await OpenAsync(product);
        await using var _ = h;
        await RefreshAsync(h, p, sid, product, "c_src");
        foreach (var bad in new[] { "SELECT 1; SELECT 2", "UPDATE c_src SET name = 'x'", "SELECT * FROM c_src INTO OUTFILE '/tmp/x'", "WITH q AS (SELECT 1) DELETE FROM c_src" })
        {
            var ex = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.DataCompareStart(new DataCompareParams
            {
                Source = new DataCompareSide { SessionId = sid, Query = bad }, Target = Side(sid, product, "c_src"), Key = ["id"],
            }));
            Assert.Equal("compareQuery", ex.Code);
        }
        var ok = await h.Rpc.DataCompareStart(new DataCompareParams
        {
            Source = new DataCompareSide { SessionId = sid, Query = "WITH q AS (SELECT id, name FROM c_src) SELECT * FROM q ORDER BY id" }, Target = Side(sid, product, "c_src"), Key = ["id"],
        });
        Assert.Null((await WaitAsync(h, ok.CompareId)).Error);
    }
}
