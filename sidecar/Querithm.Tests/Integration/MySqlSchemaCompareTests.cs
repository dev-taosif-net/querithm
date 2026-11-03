using MySqlConnector;
using Querithm.Contract;
using static Querithm.Tests.Integration.MySqlIntegrationTests;

namespace Querithm.Tests.Integration;

/// <summary>
/// Phase D2 schema compare on MySQL 8.4, MySQL 9.7 and MariaDB 11.8 (QUERITHM_IT=1): two databases compare by object name,
/// the migration script (DDL commits on its own, routines under a DELIMITER) runs, and comparing again finds nothing.
/// </summary>
public sealed class MySqlSchemaCompareTests
{
    static string Src(string product) => $"q_sc_src_{product.Replace('-', '_')}";
    static string Tgt(string product) => $"q_sc_tgt_{product.Replace('-', '_')}";

    static async Task ExecAsync(string product, string sql, string? database = null)
    {
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False{(database != null ? $";Database={database}" : "")}");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    const string Source = """
        CREATE TABLE customer (
          id INT AUTO_INCREMENT PRIMARY KEY, email VARCHAR(100) NOT NULL, note TEXT,
          created DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
          status ENUM('new','gold') NOT NULL DEFAULT 'new', score DECIMAL(5,2) DEFAULT 0.00, tag VARCHAR(20) DEFAULT 'it''s',
          UNIQUE KEY uq_email (email), KEY ix_created (created DESC), KEY ix_note (note(20)),
          CONSTRAINT ck_score CHECK (score >= 0));
        CREATE TABLE orders (
          id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY, customer_id INT NOT NULL, total DECIMAL(10,2), qty INT,
          line_total DECIMAL(12,2) GENERATED ALWAYS AS (total * qty) STORED,
          CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id) ON DELETE CASCADE);
        CREATE VIEW v_big AS SELECT id, total FROM orders WHERE total > 100;
        CREATE FUNCTION f_double(x INT) RETURNS INT DETERMINISTIC RETURN x * 2;
        CREATE PROCEDURE p_count(OUT n INT) BEGIN SELECT COUNT(*) INTO n FROM orders; END;
        CREATE TRIGGER trg_orders BEFORE INSERT ON orders FOR EACH ROW SET NEW.qty = IFNULL(NEW.qty, 1);
        """;

    const string Target = """
        CREATE TABLE customer (id INT AUTO_INCREMENT PRIMARY KEY, email VARCHAR(50) NOT NULL, created DATETIME(3) NULL, status ENUM('new','gold') NOT NULL DEFAULT 'gold', score DECIMAL(5,2));
        CREATE TABLE legacy (id INT PRIMARY KEY);
        CREATE VIEW v_big AS SELECT id FROM customer;
        CREATE FUNCTION f_double(x INT) RETURNS INT DETERMINISTIC RETURN x + x;
        """;

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Two_databases_compare_by_name_and_the_migration_makes_them_identical(string product)
    {
        await EnsureDatabaseAsync(product);
        await ExecAsync(product, $"DROP DATABASE IF EXISTS {Src(product)}; DROP DATABASE IF EXISTS {Tgt(product)}; CREATE DATABASE {Src(product)}; CREATE DATABASE {Tgt(product)};");
        // The routine bodies hold ';': MySqlConnector sends this as one multi-statement text, so each object goes on its own.
        foreach (var statement in Source.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) await ExecAsync(product, statement, Src(product));
        foreach (var statement in Target.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) await ExecAsync(product, statement, Tgt(product));

        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        Assert.True((await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid })).SchemaCompare.Available);
        var cmp = await h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = sid, Database = Src(product) }, Target = new CompareSide { SessionId = sid, Database = Tgt(product) },
        });
        Assert.False(cmp.TransactionalDdl);
        CompareItem Item(string name) => cmp.Items.Single(i => i.Name == name);
        Assert.Equal(CompareClass.Different, Item("customer").Class);
        Assert.Equal(CompareClass.SourceOnly, Item("orders").Class);
        Assert.Equal(CompareClass.TargetOnly, Item("legacy").Class);
        Assert.Equal(CompareClass.Different, Item("v_big").Class);
        Assert.Equal(CompareClass.Different, Item("f_double").Class);
        Assert.Equal(CompareClass.SourceOnly, Item("p_count").Class);
        Assert.Equal(CompareClass.SourceOnly, Item("trg_orders").Class);
        // Objects live under the target's name after the database-to-database mapping.
        Assert.All(cmp.Items, i => Assert.Equal(Tgt(product), i.Schema));

        var migration = await h.Rpc.SchemaCompareMigrationSql(new SchemaMigrationParams { CompareId = cmp.CompareId, Keys = cmp.Items.Select(i => i.Key).ToArray(), IncludeDataLoss = true });
        Assert.False(migration.Transactional);
        Assert.Contains("not one transaction", migration.Sql);
        Assert.Contains("DELIMITER $$", migration.Sql);
        Assert.DoesNotContain("DEFINER=", migration.Sql); // SQL SECURITY DEFINER stays: it is the view's behaviour, not an account
        Assert.DoesNotContain("`root`", migration.Sql);
        Assert.DoesNotContain(Src(product), string.Join("\n", migration.Sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--")))); // statements name the target only
        var (done, _, id) = await Run(h, sid, migration.Sql);
        Assert.True(done.Error == null, $"{done.Error?.Message}\n{string.Join("\n", h.Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == id).Select(m => m.Message))}\n{migration.Sql}");

        var again = await h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = sid, Database = Src(product) }, Target = new CompareSide { SessionId = sid, Database = Tgt(product) },
        });
        var left = again.Items.Where(i => i.Class != CompareClass.Identical).ToList();
        Assert.True(left.Count == 0, string.Join("\n", left.Select(i => $"{i.Class} {i.Name}: {string.Join("; ", i.Changes)}")) + "\n" + migration.Sql);
    }
}
