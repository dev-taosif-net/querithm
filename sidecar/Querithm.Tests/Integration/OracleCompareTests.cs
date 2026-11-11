using Apache.Arrow;
using Querithm.Contract;
using static Querithm.Tests.Integration.OracleScriptIntegrationTests;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S11 data compare against the Oracle Free fixture (QUERITHM_IT=1), through the sidecar: keys read in the order the
/// merge reproduces whatever the session's NLS_SORT (mixed case, accents, a CHAR key's padding), 38-digit NUMBER keys that
/// differ only in their last digit (a 28-digit decimal would see one key twice), TIMESTAMP(9) and DATE values, a CLOB left
/// out with the reason; the sync script is one PL/SQL block that runs and leaves the two sides identical, and a failure in
/// it leaves nothing changed. Custom queries: one SELECT read-only, other text refused.
/// </summary>
public sealed class OracleCompareTests
{
    const string Schema = "QUERITHM_APP";

    static async Task SetupAsync()
    {
        await using var c = await OpenAsync();
        foreach (var t in new[] { "q_s11d_src", "q_s11d_tgt", "q_s11d_ksrc", "q_s11d_ktgt" }) await DropAsync(c, "TABLE", t);
        await RunScriptAsync(c, """
            CREATE TABLE q_s11d_src (id NUMBER(38) PRIMARY KEY, name VARCHAR2(40), price NUMBER(12,2), at TIMESTAMP(9), d DATE, doc CLOB, flag CHAR(3));
            CREATE TABLE q_s11d_tgt (id NUMBER(38) PRIMARY KEY, name VARCHAR2(40), price NUMBER(12,2), at TIMESTAMP(9), d DATE, doc CLOB, flag CHAR(3));
            INSERT INTO q_s11d_src VALUES (12345678901234567890123456789012345671, 'same', 1.50, TIMESTAMP '2026-01-02 03:04:05.123456789', DATE '2026-01-02', 'a', 'ab');
            INSERT INTO q_s11d_src VALUES (12345678901234567890123456789012345672, 'changed', 2, TIMESTAMP '2026-01-02 03:04:05.123456789', DATE '2026-01-02', 'b', 'x');
            INSERT INTO q_s11d_src VALUES (3, 'source only', NULL, NULL, NULL, NULL, NULL);
            INSERT INTO q_s11d_src VALUES (5, 'O''Brien', 9.99, NULL, DATE '1999-12-31', NULL, NULL);
            INSERT INTO q_s11d_tgt VALUES (12345678901234567890123456789012345671, 'same', 1.5, TIMESTAMP '2026-01-02 03:04:05.123456789', DATE '2026-01-02', 'different doc', 'ab ');
            INSERT INTO q_s11d_tgt VALUES (12345678901234567890123456789012345672, 'was', 2, TIMESTAMP '2026-01-02 03:04:05.123456780', DATE '2026-01-03', 'b', 'x');
            INSERT INTO q_s11d_tgt VALUES (4, 'target only', NULL, NULL, NULL, NULL, NULL);
            INSERT INTO q_s11d_tgt VALUES (5, 'O''Brien', 9.99, NULL, DATE '1999-12-31', NULL, NULL);
            CREATE TABLE q_s11d_ksrc (k VARCHAR2(10) PRIMARY KEY, v NUMBER);
            CREATE TABLE q_s11d_ktgt (k VARCHAR2(10) PRIMARY KEY, v NUMBER);
            INSERT INTO q_s11d_ksrc SELECT column_value, 1 FROM TABLE(sys.odcivarchar2list('a', 'B', 'b', 'Z', 'é', 'e', '_x', 'A1'));
            INSERT INTO q_s11d_ktgt SELECT column_value, CASE column_value WHEN 'é' THEN 2 ELSE 1 END FROM TABLE(sys.odcivarchar2list('a', 'B', 'b', 'Z', 'é', 'e', '_x', 'A0'));
            COMMIT;
            """);
    }

    static DataCompareSide Table(OracleS11Host h, string table) => new() { SessionId = h.Sid, Database = OracleS11Host.Db, Schema = Schema, Table = table.ToUpperInvariant() };

    static async Task<DataCompareProgressEvent> WaitAsync(OracleS11Host h, string compareId)
    {
        for (var i = 0; i < 1200; i++)
        {
            var done = h.Notifier.Of<DataCompareProgressEvent>().FirstOrDefault(e => e.CompareId == compareId && e.Done);
            if (done != null) return done;
            await Task.Delay(50);
        }
        throw new TimeoutException("data compare did not finish");
    }

    [IntegrationFact]
    public async Task Exact_keys_binary_text_order_and_a_sync_block_that_leaves_both_sides_identical()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = h.Sid });
        Assert.True(caps.DataCompare.Available);
        var start = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(h, "q_s11d_src"), Target = Table(h, "q_s11d_tgt") });
        Assert.Contains(start.Notes, n => n.Contains("Not compared: DOC (CLOB", StringComparison.Ordinal));
        Assert.Contains(start.Notes, n => n.Contains("Oracle stores '' as NULL", StringComparison.Ordinal));
        Assert.DoesNotContain(start.Columns, x => x.Source == "DOC");
        var done = await WaitAsync(h, start.CompareId);
        Assert.Null(done.Error);
        Assert.Equal(1, done.SourceOnly);
        Assert.Equal(1, done.TargetOnly);
        Assert.Equal(1, done.Different); // the 38-digit key ...72: name, timestamp nanoseconds' tolerance aside, the date
        Assert.Equal(2, done.Identical); // ...71 (1.50 = 1.5, CHAR padding, the CLOB not compared) and 5

        var sync = await h.Rpc.DataCompareSyncSql(new DataSyncSqlParams { CompareId = start.CompareId, Insert = true, Update = true, Delete = true });
        Assert.Contains("\nBEGIN\n", sync.Sql);
        Assert.EndsWith("END;\n/\n", sync.Sql);
        Assert.Contains("12345678901234567890123456789012345672", sync.Sql);
        Assert.Contains("TO_DATE('2026-01-02 00:00:00', 'YYYY-MM-DD HH24:MI:SS')", sync.Sql);
        var ran = await h.RunAsync(sync.Sql);
        Assert.Equal(ExecState.Done, ran.State);

        var again = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(h, "q_s11d_src"), Target = Table(h, "q_s11d_tgt") });
        var after = await WaitAsync(h, again.CompareId);
        Assert.Null(after.Error);
        Assert.Equal(0, after.Different + after.SourceOnly + after.TargetOnly);
        Assert.Equal(4, after.Identical);

        // Text keys in binary order: 'A1' / 'A0' and 'é' (differs in v) line up whatever this computer's language.
        var keys = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(h, "q_s11d_ksrc"), Target = Table(h, "q_s11d_ktgt") });
        var k = await WaitAsync(h, keys.CompareId);
        Assert.Null(k.Error);
        Assert.Equal(1, k.SourceOnly);
        Assert.Equal(1, k.TargetOnly);
        Assert.Equal(1, k.Different);
        Assert.Equal(6, k.Identical);
    }

    [IntegrationFact]
    public async Task A_sync_block_that_fails_changes_nothing()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        var start = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(h, "q_s11d_src"), Target = Table(h, "q_s11d_tgt") });
        await WaitAsync(h, start.CompareId);
        var sync = await h.Rpc.DataCompareSyncSql(new DataSyncSqlParams { CompareId = start.CompareId, Insert = true, Update = true, Delete = true });
        // A row the target gained since the compare makes the INSERT of id 3 fail (ORA-00001) after the DELETE and UPDATE ran.
        await using (var c = await OpenAsync()) await RunScriptAsync(c, "INSERT INTO q_s11d_tgt (id, name) VALUES (3, 'raced'); COMMIT;");
        var ran = await h.RunAsync(sync.Sql);
        Assert.Equal(ExecState.Failed, ran.State);
        await using var check = await OpenAsync();
        Assert.Equal(1m, Convert.ToDecimal(await ScalarAsync(check, "SELECT COUNT(*) FROM q_s11d_tgt WHERE id = 4"))); // the DELETE was undone
        Assert.Equal("was", await ScalarAsync(check, "SELECT name FROM q_s11d_tgt WHERE id = 12345678901234567890123456789012345672")); // and the UPDATE
        var state = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = h.Sid, Sql = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual" });
        var done = await h.Notifier.WaitDone(state.ExecutionId!, 60_000);
        Assert.Equal(ExecState.Done, done.State);
    }

    [IntegrationFact]
    public async Task A_custom_query_side_is_one_read_only_SELECT_and_other_text_is_refused()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        var query = new DataCompareSide { SessionId = h.Sid, Database = OracleS11Host.Db, Query = "WITH s AS (SELECT id, name, price FROM q_s11d_src) SELECT * FROM s ORDER BY id;" };
        var start = await h.Rpc.DataCompareStart(new DataCompareParams { Source = query, Target = Table(h, "q_s11d_tgt"), Key = ["ID"] });
        var done = await WaitAsync(h, start.CompareId);
        Assert.Null(done.Error);
        Assert.Equal(4, done.SourceRows);
        foreach (var bad in new[] { "DELETE FROM q_s11d_src", "SELECT * FROM q_s11d_src FOR UPDATE", "BEGIN NULL; END;", "SELECT 1 FROM dual; SELECT 2 FROM dual" })
        {
            var ex = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.DataCompareStart(new DataCompareParams
            {
                Source = query with { Query = bad }, Target = Table(h, "q_s11d_tgt"), Key = ["ID"],
            }));
            Assert.Equal("compareQuery", ex.Code);
        }
        // A CLOB cannot be a key.
        var clob = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.DataCompareStart(new DataCompareParams { Source = Table(h, "q_s11d_src"), Target = Table(h, "q_s11d_tgt"), Key = ["DOC"] }));
        Assert.Equal("compareKey", clob.Code);
    }
}
