using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

/// <summary>
/// L5: SQL the classifier cannot see as plain statements — dynamic SQL, prepared statements, MERGE deletes — counts for the
/// Prod destructive confirm on every engine. Literal text is classified as a script of its own; text built at run time
/// cannot be checked and counts as destructive (<c>dynamicSql</c>). Tokens / AST only. Each destructive case passed the
/// guard silently before the fix.
/// </summary>
public class DynamicSqlClassifierTests
{
    static string[] Rules(IDialectService language, string sql) => language.Classify(sql).Destructive.Select(f => f.Rule).ToArray();

    [Theory]
    [InlineData("EXEC('DROP TABLE dbo.Orders')", "drop")]
    [InlineData("EXECUTE ('DELETE FROM dbo.Orders')", "deleteWithoutWhere")]
    [InlineData("EXEC('TRUNC' + 'ATE TABLE dbo.Orders')", "truncate")]
    [InlineData("DECLARE @sql nvarchar(max) = N'DROP TABLE t'; EXEC (@sql)", "dynamicSql")]
    [InlineData("EXEC('DELETE FROM ' + @table)", "dynamicSql")]
    [InlineData("EXEC sp_executesql N'DELETE FROM dbo.Orders'", "deleteWithoutWhere")]
    [InlineData("EXEC sys.sp_executesql @stmt = N'UPDATE dbo.Orders SET Status = 0'", "updateWithoutWhere")]
    [InlineData("EXEC sp_executesql @sql, N'@id int', @id = 1", "dynamicSql")]
    [InlineData("INSERT INTO dbo.Log EXEC('DROP TABLE dbo.Orders')", "drop")]
    [InlineData("EXEC @proc", "dynamicSql")]
    [InlineData("EXEC('EXEC(''DROP TABLE t'')')", "drop")]
    [InlineData("MERGE dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;", "mergeDelete")]
    [InlineData("MERGE dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN NOT MATCHED BY SOURCE THEN DELETE;", "mergeDelete")]
    // 8a review: the undocumented loops and the legacy sp_sqlexec run SQL text too.
    [InlineData("EXEC sp_MSforeachtable 'DELETE FROM ?'", "dynamicSql")]
    [InlineData("EXEC sys.sp_MSforeachtable @command1 = 'SELECT COUNT(*) FROM ?'", "dynamicSql")]
    [InlineData("EXEC master..sp_MSforeachdb 'USE [?]; DROP TABLE dbo.Log'", "dynamicSql")]
    [InlineData("EXEC sp_sqlexec 'DROP TABLE dbo.Orders'", "drop")]
    [InlineData("EXEC sp_sqlexec @p", "dynamicSql")]
    public void TSql_dynamic_sql_and_merge_deletes_are_destructive(string sql, string rule) =>
        Assert.Contains(rule, Rules(new TSqlLanguageService(), sql));

    [Theory]
    [InlineData("EXEC dbo.usp_Report @id = 1")]
    [InlineData("EXEC sp_executesql N'SELECT * FROM dbo.Orders WHERE Id = @id', N'@id int', @id = 1")]
    [InlineData("EXEC('SELECT 1')")]
    [InlineData("MERGE dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.Qty = s.Qty WHEN NOT MATCHED THEN INSERT (Id, Qty) VALUES (s.Id, s.Qty);")]
    [InlineData("CREATE PROCEDURE dbo.p AS EXEC('DROP TABLE t')")] // runs later, not now
    public void TSql_safe_calls_stay_safe(string sql) => Assert.Empty(Rules(new TSqlLanguageService(), sql));

    [Theory]
    [InlineData("PREPARE wipe AS DELETE FROM orders", "deleteWithoutWhere")]
    [InlineData("PREPARE p (int) AS UPDATE orders SET status = $1", "updateWithoutWhere")]
    [InlineData("EXECUTE wipe", "dynamicSql")]
    [InlineData("EXPLAIN ANALYZE EXECUTE wipe", "dynamicSql")]
    [InlineData("DO $$ BEGIN EXECUTE 'DROP TABLE t'; END $$", "drop")]
    [InlineData("DO $$ BEGIN EXECUTE format('DROP TABLE %I', 't'); END $$", "dynamicSql")]
    [InlineData("DO $$ DECLARE q text := 'x'; BEGIN EXECUTE q; END $$", "dynamicSql")]
    [InlineData("DO $$ BEGIN MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE; END $$", "mergeDelete")]
    [InlineData("MERGE INTO orders o USING staging s ON o.id = s.id WHEN MATCHED THEN DELETE", "mergeDelete")]
    [InlineData("WITH s AS (SELECT 1 AS id) MERGE INTO orders o USING s ON o.id = s.id WHEN MATCHED AND o.qty = 0 THEN DELETE", "mergeDelete")]
    public void Postgres_prepared_dynamic_and_merge_deletes_are_destructive(string sql, string rule) =>
        Assert.Contains(rule, Rules(new PgLanguageService(), sql));

    [Theory]
    [InlineData("PREPARE p AS SELECT * FROM orders WHERE id = $1; EXECUTE p(1)")]
    [InlineData("MERGE INTO orders o USING staging s ON o.id = s.id WHEN MATCHED THEN UPDATE SET qty = s.qty WHEN NOT MATCHED THEN INSERT VALUES (s.id, s.qty)")]
    [InlineData("DO $$ BEGIN EXECUTE 'SELECT 1'; END $$")]
    [InlineData("PREPARE TRANSACTION 'tx1'")]
    public void Postgres_safe_prepared_statements_stay_safe(string sql) => Assert.Empty(Rules(new PgLanguageService(), sql));

    [Theory]
    [InlineData("PREPARE s FROM 'DELETE FROM orders'", "deleteWithoutWhere")]
    [InlineData("PREPARE s FROM 'DROP TABLE orders'", "drop")]
    [InlineData("SET @q = 'DROP TABLE orders'; PREPARE s FROM @q; EXECUTE s", "dynamicSql")]
    [InlineData("EXECUTE s", "dynamicSql")]
    [InlineData("PREPARE s FROM \"UPDATE orders SET qty = 0\"", "updateWithoutWhere")]
    // 8a review: a literal with a charset introducer is still a literal; MariaDB's EXECUTE IMMEDIATE runs its literal now.
    [InlineData("PREPARE s FROM _utf8mb4'DELETE FROM orders'", "deleteWithoutWhere")]
    [InlineData("PREPARE s FROM _latin1 'DROP TABLE orders'", "drop")]
    [InlineData("EXECUTE IMMEDIATE 'DROP TABLE orders'", "drop")]
    [InlineData("EXECUTE IMMEDIATE 'DELETE FROM orders WHERE id > ?' USING 0; EXECUTE IMMEDIATE 'TRUNCATE TABLE logs'", "truncate")]
    [InlineData("EXECUTE IMMEDIATE @q", "dynamicSql")]
    [InlineData("EXECUTE IMMEDIATE CONCAT('DROP TABLE ', @t)", "dynamicSql")]
    public void MySql_prepared_statements_are_classified(string sql, string rule) =>
        Assert.Contains(rule, Rules(new MySqlLanguageService(), sql));

    [Theory]
    [InlineData("EXECUTE IMMEDIATE 'SELECT 1'")]
    [InlineData("EXECUTE IMMEDIATE 'SELECT * FROM orders WHERE id = ?' USING 1")]
    [InlineData("PREPARE s FROM _utf8mb4'SELECT 1'; EXECUTE s")]
    public void MySql_literal_dynamic_sql_that_only_reads_stays_safe(string sql) => Assert.Empty(Rules(new MySqlLanguageService(), sql));

    [Theory]
    [InlineData("EXEC sp_sqlexec 'SELECT 1'")]
    public void TSql_a_literal_that_only_reads_stays_safe(string sql) => Assert.Empty(Rules(new TSqlLanguageService(), sql));

    [Fact]
    public void MySql_a_prepared_select_and_its_execute_stay_safe() =>
        Assert.Empty(Rules(new MySqlLanguageService(), "PREPARE s FROM 'SELECT * FROM orders WHERE id = ?'; SET @id = 1; EXECUTE s USING @id; DEALLOCATE PREPARE s"));

    [Theory]
    [InlineData("PREPARE wipe AS DELETE FROM orders", "deleteWithoutWhere")]
    [InlineData("EXECUTE wipe", "dynamicSql")]
    [InlineData("MERGE INTO orders o USING staging s ON o.id = s.id WHEN MATCHED THEN DELETE", "mergeDelete")]
    public void DuckDb_prepared_statements_and_merge_deletes_are_destructive(string sql, string rule) =>
        Assert.Contains(rule, Rules(new DuckDbLanguageService(), sql));

    [Fact]
    public void DuckDb_a_prepared_statement_cannot_hide_what_the_file_policy_refuses()
    {
        var c = new DuckDbLanguageService().Classify("PREPARE leak AS COPY orders TO '/tmp/orders.csv'");
        Assert.NotNull(c.Refusal);
        Assert.Empty(Rules(new DuckDbLanguageService(), "PREPARE p AS SELECT * FROM orders WHERE id = $1; EXECUTE p(1)"));
    }

    /// <summary>SQLite has no SQL-level dynamic SQL, prepared statements or MERGE: the words are no statement of its own.</summary>
    [Theory]
    [InlineData("DELETE FROM t WHERE id IN (SELECT id FROM s)")]
    [InlineData("REPLACE INTO t (id, v) VALUES (1, 'a')")]
    public void Sqlite_has_no_dynamic_sql_to_hide_a_statement(string sql) => Assert.Empty(Rules(new SqliteLanguageService(), sql));
}
