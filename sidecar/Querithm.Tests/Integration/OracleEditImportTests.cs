using System.Data.Common;
using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S10 grid edits and imports on the Oracle Free fixture (QUERITHM_IT=1), as the least-privileged app user: keys proven
/// by the catalog (primary key, a NOT NULL unique constraint; a nullable one is no key), the ROWID fallback only for a heap
/// table with row movement off, original-value conflict checks where '' and NULL are one value, a concurrent change that
/// rolls the whole patch back, LOB columns read-only, the array-bound import in its three transaction modes (a key
/// violation leaves nothing, a cancel mid-import leaves nothing), and the Prod guard enforced in the sidecar.
/// </summary>
public sealed class OracleEditImportTests
{
    const string App = OracleIntegrationTests.AppUser;
    const string Schema = "QUERITHM_APP";
    static readonly string Db = OracleIntegrationTests.Service;

    static GridValue V(string? text) => text == null ? new GridValue { IsNull = true } : new GridValue { Text = text };

    static Profile ProfileOf(EnvironmentKind environment) => new()
    {
        Id = "", Name = $"ora-edit-{environment}", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password, Username = App,
        DatabaseDefault = Db, TlsMode = TlsMode.Disable, TimeoutS = 30, Environment = environment,
    };

    static CancellationToken Timeout(int seconds = 120) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    /// <summary>A connection of the test's own, outside Querithm (setup and checks from another session).</summary>
    static async Task<OracleConnection> OutsideAsync()
    {
        var c = new OracleConnection(OracleIntegrationTests.ConnectionString(App));
        await c.OpenAsync(Timeout());
        return c;
    }

    static async Task ExecAsync(params string[] statements)
    {
        await using var c = await OutsideAsync();
        foreach (var sql in statements)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(Timeout());
        }
    }

    static async Task<object?> ScalarAsync(string sql)
    {
        await using var c = await OutsideAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(Timeout());
        return v is DBNull ? null : v;
    }

    static async Task<long> CountAsync(string table) => Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM {table}"), CultureInfo.InvariantCulture);

    static string Drop(string table) => $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {table} CASCADE CONSTRAINTS PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE <> -942 THEN RAISE; END IF; END;";

    sealed record Opened(TestHost H, Profile P, string Sid) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    /// <summary>A host with a session and a fresh snapshot (editability resolves result columns against it).</summary>
    static async Task<Opened> OpenAsync(EnvironmentKind environment = EnvironmentKind.Dev)
    {
        var h = new TestHost(TestOracle.Supported(Directory.CreateTempSubdirectory("qora-edit-").FullName));
        var p = await h.Rpc.ProfileSave(ProfileOf(environment));
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = FixtureSecrets.Password("oracle") })).SessionId;
        await RefreshAsync(h, p);
        return new Opened(h, p, sid);
    }

    static async Task RefreshAsync(TestHost h, Profile p)
    {
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = Db, Full = true });
        await h.Services.Metadata.WaitAsync(p.Id, Db);
    }

    static async Task<(ResultEditInfo Edit, string ExecutionId, List<Apache.Arrow.RecordBatch> Batches)> SelectAsync(Opened o, string sql)
    {
        // A Prod profile's first run needs its confirmation (query.execute's own guard); a read is all these tests run.
        var started = await o.H.Rpc.QueryExecute(new QueryExecuteParams { SessionId = o.Sid, Sql = sql, Options = new ExecuteOptions { ConfirmedProd = o.P.Environment == EnvironmentKind.Prod } });
        var id = started.ExecutionId!;
        var done = await o.H.Notifier.WaitDone(id);
        var batches = o.H.Frames.For(id).Where(f => f.Kind == FrameKind.Batch).Select(f => TestHost.ReadBatch(f.Payload)).ToList();
        Assert.True(done.Error == null, done.Error?.Message);
        return (o.H.Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == id).Edit!, id, batches);
    }

    static string? Cell(Apache.Arrow.RecordBatch b, string column, int row = 0) => b.Column(b.Schema.GetFieldIndex(column)) switch
    {
        Apache.Arrow.StringArray s => s.GetString(row),
        Apache.Arrow.Decimal128Array d => d.GetValue(row)?.ToString(CultureInfo.InvariantCulture),
        var a => a.ToString(),
    };

    // ---------------------------------------------------------------------------------------------------- keys

    const string Orders = "Q_S10_ORDERS";

    static Task OrdersAsync() => ExecAsync(Drop(Orders),
        $"""
         CREATE TABLE {Orders} (
           id NUMBER(10) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, name VARCHAR2(40 CHAR) NOT NULL, qty NUMBER(5), price NUMBER(12,4),
           shipped TIMESTAMP(3), born DATE, code CHAR(3), raw_bytes RAW(16), note CLOB, pic BLOB, score BINARY_DOUBLE, twice NUMBER GENERATED ALWAYS AS (qty * 2) VIRTUAL)
         """,
        $"INSERT INTO {Orders} (id, name, qty, price, shipped, born, code, raw_bytes, note, score) VALUES (1, 'one', 1, 1.5, TIMESTAMP '2026-01-02 03:04:05.120', DATE '2026-01-02', 'ab', HEXTORAW('0A0B'), 'long text', 0.1)",
        $"INSERT INTO {Orders} (id, name, qty) VALUES (2, 'two', 2)",
        $"INSERT INTO {Orders} (id, name, qty) VALUES (3, 'three', 3)",
        $"ALTER TABLE {Orders} MODIFY id GENERATED BY DEFAULT AS IDENTITY (START WITH 100)");

    [IntegrationFact]
    public async Task A_primary_key_table_is_editable_and_lob_virtual_and_identity_columns_are_read_only_with_the_reason()
    {
        await OrdersAsync();
        await using var o = await OpenAsync();
        var (edit, _, _) = await SelectAsync(o, $"SELECT id, name, qty, price, shipped, born, code, raw_bytes, note, pic, score, twice FROM {Orders} ORDER BY id");
        Assert.True(edit.Editable, edit.Reason);
        Assert.Equal(["ID"], edit.KeyColumns);
        EditColumn C(string n) => edit.Columns.Single(c => c.Column == n);
        Assert.Contains("CLOB values are read in part", C("NOTE").LockReason);
        Assert.Contains("BLOB values are read in part", C("PIC").LockReason);
        Assert.Equal("computed column", C("TWICE").LockReason);
        Assert.Equal("identity values are generated by the server", C("ID").LockReason);
        Assert.False(C("SCORE").Comparable);
        Assert.True(C("RAW_BYTES").Comparable);
        Assert.Contains("ORA_ROWSCN", edit.ConcurrencyNote);
    }

    [IntegrationFact]
    public async Task Updates_inserts_and_deletes_are_written_with_their_types_and_the_new_key_is_read_back()
    {
        await OrdersAsync();
        await using var o = await OpenAsync();
        var (_, exec, _) = await SelectAsync(o, $"SELECT id, name, qty, price, shipped, born, code, raw_bytes FROM {Orders} ORDER BY id");
        var r = await o.H.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = o.Sid, ExecutionId = exec,
            Updates =
            [
                new GridRowUpdate
                {
                    RowIndex = 0, Key = [V("1")],
                    Changes = new() { ["PRICE"] = V("12345678.1234"), ["SHIPPED"] = V("2026-05-06 07:08:09.5"), ["RAW_BYTES"] = V("0xFF00"), ["CODE"] = V("xyz"), ["BORN"] = V("1999-12-31") },
                    Original = new() { ["PRICE"] = V("1.5000"), ["SHIPPED"] = V("2026-01-02 03:04:05.12"), ["RAW_BYTES"] = V("0x0A0B"), ["CODE"] = V("ab "), ["BORN"] = V("2026-01-02 00:00:00") },
                },
            ],
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["NAME"] = V("four"), ["QTY"] = V("4e2"), ["BORN"] = V("2026-07-08T09:10:11") } }],
            Deletes = [new GridRowDelete { RowIndex = 1, Key = [V("2")], Original = new() { ["NAME"] = V("two"), ["QTY"] = V("2"), ["PRICE"] = V(null) } }],
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal((1, 1, 1), (r.AppliedUpdates, r.AppliedInserts, r.AppliedDeletes));
        Assert.Equal("100", Assert.Single(r.InsertedKeys!["0"]).Text); // the identity value, through the new row's ROWID
        Assert.Equal("12345678.1234", await ScalarAsync($"SELECT TO_CHAR(price, 'TM9') FROM {Orders} WHERE id = 1"));
        Assert.Equal("2026-05-06 07:08:09.500", await ScalarAsync($"SELECT TO_CHAR(shipped, 'YYYY-MM-DD HH24:MI:SS.FF3') FROM {Orders} WHERE id = 1"));
        Assert.Equal("FF00", await ScalarAsync($"SELECT RAWTOHEX(raw_bytes) FROM {Orders} WHERE id = 1"));
        Assert.Equal("1999-12-31", await ScalarAsync($"SELECT TO_CHAR(born, 'YYYY-MM-DD') FROM {Orders} WHERE id = 1"));
        Assert.Equal("400 2026-07-08 09:10:11", await ScalarAsync($"SELECT qty || ' ' || TO_CHAR(born, 'YYYY-MM-DD HH24:MI:SS') FROM {Orders} WHERE id = 100"));
        Assert.Equal(0L, await CountAsync($"{Orders} WHERE id = 2"));
        Assert.Equal(TxState.None, (await o.H.Rpc.SessionStateGet(new SessionRef { SessionId = o.Sid })).TxState);
    }

    [IntegrationFact]
    public async Task A_not_null_unique_key_is_a_key_and_a_nullable_one_is_not()
    {
        await ExecAsync(Drop("Q_S10_UQ"), Drop("Q_S10_NUQ"),
            "CREATE TABLE Q_S10_UQ (code VARCHAR2(10) NOT NULL CONSTRAINT q_s10_uq_code UNIQUE, v NUMBER)", "INSERT INTO Q_S10_UQ VALUES ('a', 1)",
            "CREATE TABLE Q_S10_NUQ (code VARCHAR2(10) CONSTRAINT q_s10_nuq_code UNIQUE, v NUMBER)", "INSERT INTO Q_S10_NUQ VALUES (NULL, 1)", "INSERT INTO Q_S10_NUQ VALUES (NULL, 2)");
        await using var o = await OpenAsync();
        var (uq, exec, _) = await SelectAsync(o, "SELECT code, v FROM q_s10_uq");
        Assert.Equal(["CODE"], uq.KeyColumns);
        var r = await o.H.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = o.Sid, ExecutionId = exec,
            Updates = [new GridRowUpdate { RowIndex = 0, Key = [V("a")], Changes = new() { ["V"] = V("5") }, Original = new() { ["V"] = V("1") } }],
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["CODE"] = V("b"), ["V"] = V("6") } }],
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("b", Assert.Single(r.InsertedKeys!["0"]).Text);
        Assert.Equal(5m, await ScalarAsync("SELECT v FROM q_s10_uq WHERE code = 'a'"));

        var (nuq, _, _) = await SelectAsync(o, "SELECT code, v FROM q_s10_nuq");
        Assert.False(nuq.Editable);
        Assert.Equal($"Read-only: {Schema}.Q_S10_NUQ has no primary key or NOT NULL unique index", nuq.Reason);
    }

    // ---------------------------------------------------------------------------------------------------- ROWID

    [IntegrationFact]
    public async Task Without_a_key_the_rowid_of_a_heap_table_stands_in_and_row_movement_or_an_iot_refuses_it()
    {
        await ExecAsync(Drop("Q_S10_HEAP"), Drop("Q_S10_MOVES"), Drop("Q_S10_IOT"),
            "CREATE TABLE Q_S10_HEAP (msg VARCHAR2(20), n NUMBER)", "INSERT INTO Q_S10_HEAP VALUES ('a', 1)", "INSERT INTO Q_S10_HEAP VALUES ('a', 1)",
            "CREATE TABLE Q_S10_MOVES (msg VARCHAR2(20)) ENABLE ROW MOVEMENT", "INSERT INTO Q_S10_MOVES VALUES ('m')",
            "CREATE TABLE Q_S10_IOT (id NUMBER PRIMARY KEY, msg VARCHAR2(20)) ORGANIZATION INDEX", "INSERT INTO Q_S10_IOT VALUES (1, 'i')");
        await using var o = await OpenAsync();
        var (heap, exec, batches) = await SelectAsync(o, "SELECT ROWID, msg, n FROM q_s10_heap ORDER BY ROWID");
        Assert.True(heap.Editable, heap.Reason);
        Assert.Equal(["ROWID"], heap.KeyColumns);
        Assert.True(heap.Columns.Single(c => c.Column == "ROWID").Locked);
        // Two identical rows: the ROWID tells them apart, and exactly the one edited changes.
        var second = Cell(batches[0], "ROWID", 1)!;
        var r = await o.H.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = o.Sid, ExecutionId = exec,
            Updates = [new GridRowUpdate { RowIndex = 1, Key = [V(second)], Changes = new() { ["MSG"] = V("b") }, Original = new() { ["MSG"] = V("a") } }],
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["MSG"] = V("c"), ["N"] = V("3") } }],
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("a b c", await ScalarAsync("SELECT LISTAGG(msg, ' ') WITHIN GROUP (ORDER BY msg) FROM q_s10_heap"));
        Assert.Equal("b", await ScalarAsync($"SELECT msg FROM q_s10_heap WHERE ROWID = CHARTOROWID('{second}')"));
        var inserted = Assert.Single(r.InsertedKeys!["0"]).Text!;
        Assert.Equal("c", await ScalarAsync($"SELECT msg FROM q_s10_heap WHERE ROWID = CHARTOROWID('{inserted}')"));

        var (moves, _, _) = await SelectAsync(o, "SELECT ROWID, msg FROM q_s10_moves");
        Assert.False(moves.Editable);
        Assert.Contains("row movement is enabled", moves.Reason);
        var (iot, _, _) = await SelectAsync(o, "SELECT ROWID, msg FROM q_s10_iot");
        Assert.False(iot.Editable);
        Assert.Equal("Read-only: primary key column ID is not in the SELECT list, and its ROWID cannot stand in: it is an index-organized table, whose ROWIDs are logical and follow the primary key", iot.Reason);
        var (noRowid, _, _) = await SelectAsync(o, "SELECT msg, n FROM q_s10_heap");
        Assert.Equal($"Read-only: {Schema}.Q_S10_HEAP has no primary key or NOT NULL unique index", noRowid.Reason);
    }

    [IntegrationFact]
    public async Task Turning_row_movement_on_is_seen_at_the_next_refresh_and_the_current_schema_is_read_again_after_alter_session()
    {
        await ExecAsync(Drop("Q_S10_FLIP"), "CREATE TABLE Q_S10_FLIP (msg VARCHAR2(20))", "INSERT INTO Q_S10_FLIP VALUES ('x')");
        await using var o = await OpenAsync();
        Assert.True((await SelectAsync(o, "SELECT ROWID, msg FROM q_s10_flip")).Edit.Editable);
        // ALTER SESSION makes the schema unknown until it is read again after the batch; unqualified names still resolve.
        Assert.Null((await o.H.RunAsync(o.Sid, $"ALTER SESSION SET CURRENT_SCHEMA = {Schema}")).Done.Error);
        Assert.True((await SelectAsync(o, "SELECT ROWID, msg FROM q_s10_flip")).Edit.Editable);
        await ExecAsync("ALTER TABLE Q_S10_FLIP ENABLE ROW MOVEMENT");
        await RefreshAsync(o.H, o.P);
        Assert.Contains("row movement is enabled", (await SelectAsync(o, "SELECT ROWID, msg FROM q_s10_flip")).Edit.Reason);
    }

    [IntegrationFact]
    public async Task After_a_cancelled_plsql_block_the_next_select_still_resolves_its_table()
    {
        // Found by smoke-ui: a cancelled block leaves its connection's schema unknown with no after-batch read; the next run
        // on that connection reads it before it starts.
        await ExecAsync(Drop("Q_S10_AFTER"), "CREATE TABLE Q_S10_AFTER (id NUMBER PRIMARY KEY, msg VARCHAR2(20))", "INSERT INTO Q_S10_AFTER VALUES (1, 'x')");
        await using var o = await OpenAsync();
        var r = await o.H.Rpc.QueryExecute(new QueryExecuteParams { SessionId = o.Sid, Sql = "BEGIN DBMS_SESSION.SLEEP(30); END;" });
        await Task.Delay(1500);
        await o.H.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
        Assert.Equal(ExecState.Cancelled, (await o.H.Notifier.WaitDone(r.ExecutionId!)).State);
        var (edit, _, _) = await SelectAsync(o, "SELECT id, msg FROM q_s10_after");
        Assert.True(edit.Editable, edit.Reason);
    }

    // ---------------------------------------------------------------------------------------------------- conflicts

    [IntegrationFact]
    public async Task An_empty_string_and_null_are_one_value_for_the_conflict_check()
    {
        await ExecAsync(Drop("Q_S10_NULLS"), "CREATE TABLE Q_S10_NULLS (id NUMBER PRIMARY KEY, s VARCHAR2(10), t VARCHAR2(10))",
            "INSERT INTO Q_S10_NULLS VALUES (1, NULL, 'x')", "INSERT INTO Q_S10_NULLS VALUES (2, '', 'y')");
        await using var o = await OpenAsync();
        var (_, exec, _) = await SelectAsync(o, "SELECT id, s, t FROM q_s10_nulls ORDER BY id");
        // Both rows hold NULL ('' is NULL in Oracle): an original of '' or NULL matches either.
        var ok = await o.H.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = o.Sid, ExecutionId = exec,
            Updates =
            [
                new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["S"] = V("a") }, Original = new() { ["S"] = V("") } },
                new GridRowUpdate { RowIndex = 1, Key = [V("2")], Changes = new() { ["S"] = V("") }, Original = new() { ["S"] = V(null) } },
            ],
        });
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal("a", await ScalarAsync("SELECT s FROM q_s10_nulls WHERE id = 1"));
        Assert.Null(await ScalarAsync("SELECT s FROM q_s10_nulls WHERE id = 2")); // '' written is NULL
        // An original that is not NULL against a NULL on the server is a conflict.
        var (_, exec2, _) = await SelectAsync(o, "SELECT id, s, t FROM q_s10_nulls ORDER BY id");
        var conflict = await o.H.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = o.Sid, ExecutionId = exec2,
            Updates = [new GridRowUpdate { RowIndex = 1, Key = [V("2")], Changes = new() { ["S"] = V("z") }, Original = new() { ["S"] = V("was here") } }],
        });
        Assert.False(conflict.Ok);
        var row = Assert.Single(conflict.Conflicts ?? []);
        Assert.Equal("changedOnServer", row.Reason);
        Assert.True(row.Current?["S"].IsNull);
    }

    [IntegrationFact]
    public async Task A_row_changed_by_someone_else_rolls_the_whole_patch_back()
    {
        await OrdersAsync();
        await using var o = await OpenAsync();
        var (_, exec, _) = await SelectAsync(o, $"SELECT id, name, qty FROM {Orders} ORDER BY id");
        await ExecAsync($"UPDATE {Orders} SET name = 'changed' WHERE id = 2");
        // data.rowTokens: the rows still as the grid shows them (a UNION ALL of binds FROM DUAL).
        var tokens = await o.H.Rpc.DataRowTokens(new RowTokensParams
        {
            SessionId = o.Sid, ExecutionId = exec, Keys = [[V("1")], [V("2")], [V("99")]],
            Expect = [new() { ["NAME"] = V("one") }, new() { ["NAME"] = V("two") }, new() { ["NAME"] = V("x") }],
        });
        Assert.Equal((string?[])["", null, null], tokens.Tokens);
        var r = await o.H.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = o.Sid, ExecutionId = exec,
            Updates =
            [
                new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["NAME"] = V("uno") }, Original = new() { ["NAME"] = V("one") } },
                new GridRowUpdate { RowIndex = 1, Key = [V("2")], Changes = new() { ["NAME"] = V("dos") }, Original = new() { ["NAME"] = V("two") } },
            ],
        });
        Assert.False(r.Ok);
        var c = Assert.Single(r.Conflicts ?? []);
        Assert.Equal((1, "changedOnServer"), (c.RowIndex, c.Reason));
        Assert.Equal("changed", c.Current?["NAME"].Text);
        Assert.Equal("one", await ScalarAsync($"SELECT name FROM {Orders} WHERE id = 1")); // rolled back with the conflicting row
        Assert.Equal(TxState.None, (await o.H.Rpc.SessionStateGet(new SessionRef { SessionId = o.Sid })).TxState);
    }

    [IntegrationFact]
    public async Task Inside_an_open_transaction_a_failed_patch_undoes_only_itself()
    {
        await OrdersAsync();
        await using var o = await OpenAsync();
        Assert.Equal(TxState.Open, (await o.H.Rpc.TxBegin(new SessionRef { SessionId = o.Sid })).TxState);
        Assert.Null((await o.H.RunAsync(o.Sid, $"UPDATE {Orders} SET qty = 30 WHERE id = 3")).Done.Error);
        var (_, exec, _) = await SelectAsync(o, $"SELECT id, name, qty FROM {Orders} ORDER BY id");
        var r = await o.H.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = o.Sid, ExecutionId = exec,
            Updates =
            [
                new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["QTY"] = V("10") }, Original = new() { ["QTY"] = V("1") } },
                new GridRowUpdate { RowIndex = 1, Key = [V("2")], Changes = new() { ["QTY"] = V("123456") }, Original = new() { ["QTY"] = V("2") } }, // NUMBER(5)
            ],
        });
        Assert.False(r.Ok);
        Assert.Contains("NUMBER(5) holds 5", r.Error);
        Assert.Equal(TxState.Open, (await o.H.Rpc.SessionStateGet(new SessionRef { SessionId = o.Sid })).TxState);
        var (check, batches, _) = await o.H.RunAsync(o.Sid, $"SELECT qty FROM {Orders} WHERE id IN (1, 3) ORDER BY id");
        Assert.Null(check.Error);
        Assert.Equal(("1", "30"), (Cell(batches[0], "QTY", 0), Cell(batches[0], "QTY", 1)));
        await o.H.Rpc.TxRollback(new SessionRef { SessionId = o.Sid });
        Assert.Equal(3L, Convert.ToInt64(await ScalarAsync($"SELECT qty FROM {Orders} WHERE id = 3"), CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------------------------------------------- import

    const string Items = "Q_S10_ITEMS";

    static async Task<string> CsvAsync(string dir, string name, int rows, Func<int, string> line, string header)
    {
        var path = Path.Combine(dir, name);
        var sb = new StringBuilder(header).Append('\n');
        for (var i = 1; i <= rows; i++) sb.Append(line(i)).Append('\n');
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    static async Task<ImportProgressEvent> ImportAsync(Opened o, string csv, string table, ImportRunOptions? options = null)
    {
        var ins = await o.H.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var run = await o.H.Rpc.ImportRun(new ImportRunParams { SessionId = o.Sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = Schema, Table = table, Options = options });
        Assert.Null(run.Blocked);
        return await WaitImportAsync(o.H, run.JobId!);
    }

    static async Task<ImportProgressEvent> WaitImportAsync(TestHost h, string jobId, int seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == jobId && e.Done) is { } e) return e;
            await Task.Delay(25);
        }
        throw new TimeoutException("The import did not finish.");
    }

    [IntegrationFact]
    public async Task A_csv_creates_its_table_imports_with_array_binding_and_a_key_violation_leaves_nothing()
    {
        await ExecAsync(Drop(Items));
        await using var o = await OpenAsync();
        var dir = Directory.CreateTempSubdirectory("qora-import-").FullName;
        try
        {
            var csv = await CsvAsync(dir, "items.csv", 3000, i => $"{i},item {i},{i % 50},{i}.25,2026-01-{1 + i % 28:00} 10:00:00,{(i % 2 == 0 ? "true" : "false")},",
                "id,name,qty,price,born,ok,empty");
            var ins = await o.H.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
            var target = await o.H.Rpc.ImportCheckTarget(new ImportTargetParams
            {
                SessionId = o.Sid, InspectionId = ins.InspectionId, Schema = Schema, Table = Items, NewTable = true, PrimaryKey = ["id"],
            });
            Assert.Contains("NUMBER(1)", target.CreateTableSql);
            Assert.Contains("DATE", target.CreateTableSql);
            Assert.Null((await o.H.RunAsync(o.Sid, target.CreateTableSql!)).Done.Error);
            await RefreshAsync(o.H, o.P);
            var run = await o.H.Rpc.ImportRun(new ImportRunParams { SessionId = o.Sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = Schema, Table = Items });
            var done = await WaitImportAsync(o.H, run.JobId!);
            Assert.Null(done.Error);
            Assert.Equal(3000L, done.RowsLoaded);
            Assert.Equal(3000L, await CountAsync(Items));
            Assert.Equal(1500m, await ScalarAsync($"SELECT SUM(\"ok\") FROM {Items}"));
            Assert.Equal("1500.25 2026-01-17 10:00:00", await ScalarAsync($"SELECT TO_CHAR(\"price\", 'TM9') || ' ' || TO_CHAR(\"born\", 'YYYY-MM-DD HH24:MI:SS') FROM {Items} WHERE \"id\" = 1500"));

            // The same keys again: the array execute fails on the primary key, and its one transaction leaves not one row.
            var again = await ImportAsync(o, csv, Items);
            Assert.Contains("ORA-00001", again.Error);
            Assert.Equal(3000L, await CountAsync(Items));
            Assert.Equal(TxState.None, (await o.H.Rpc.SessionStateGet(new SessionRef { SessionId = o.Sid })).TxState);

            // A value the column cannot take rejects its row (the reject log) instead of failing the load.
            var bad = await CsvAsync(dir, "bad.csv", 0, _ => "", "id,name,qty,price,born,ok,empty\n5001,a,1,1.25,2026-01-01 10:00:00,true,\n5002,b,2,1.25,2026-02-30 10:00:00,true,");
            var partial = await ImportAsync(o, bad, Items, new ImportRunOptions { MaxRejects = 5 });
            Assert.Null(partial.Error);
            Assert.Equal((1L, 1L), (partial.RowsLoaded, partial.Rejected));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [IntegrationFact]
    public async Task Batches_commit_as_they_go_and_inside_a_transaction_the_import_joins_it()
    {
        await ExecAsync(Drop("Q_S10_IDS"), "CREATE TABLE Q_S10_IDS (id NUMBER(10) PRIMARY KEY, name VARCHAR2(20 CHAR))", "INSERT INTO Q_S10_IDS VALUES (1800, 'taken')");
        await using var o = await OpenAsync();
        var dir = Directory.CreateTempSubdirectory("qora-import-").FullName;
        try
        {
            var csv = await CsvAsync(dir, "ids.csv", 2500, i => $"{i},item {i}", "ID,NAME");
            // Batches of 1,000: row 1800 fails, the first batch stays committed, rows 1001-1799 go with the failed batch.
            var failed = await ImportAsync(o, csv, "Q_S10_IDS", new ImportRunOptions { TransactionMode = ImportTransactionMode.Batches, CommitEvery = 1000 });
            Assert.StartsWith("File row 1,800: ORA-00001: unique constraint", failed.Error);
            Assert.Equal(1000L, failed.RowsLoaded);
            Assert.Equal(1001L, await CountAsync("Q_S10_IDS"));
            Assert.Equal(TxState.None, (await o.H.Rpc.SessionStateGet(new SessionRef { SessionId = o.Sid })).TxState);

            // Inside the user's transaction: a failing import undoes only itself; the user's ROLLBACK undoes the import too.
            Assert.Null((await o.H.RunAsync(o.Sid, "DELETE FROM q_s10_ids WHERE id < 1800")).Done.Error);
            Assert.Equal(TxState.Open, (await o.H.Rpc.TxBegin(new SessionRef { SessionId = o.Sid })).TxState);
            Assert.Null((await o.H.RunAsync(o.Sid, "INSERT INTO q_s10_ids VALUES (5000, 'mine')")).Done.Error);
            Assert.NotNull((await ImportAsync(o, csv, "Q_S10_IDS")).Error);
            Assert.Equal(TxState.Open, (await o.H.Rpc.SessionStateGet(new SessionRef { SessionId = o.Sid })).TxState);
            Assert.Null((await o.H.RunAsync(o.Sid, "DELETE FROM q_s10_ids WHERE id = 1800")).Done.Error);
            var ok = await ImportAsync(o, csv, "Q_S10_IDS");
            Assert.Null(ok.Error);
            Assert.Equal(2500L, ok.RowsLoaded);
            Assert.Equal(1L, await CountAsync("Q_S10_IDS")); // nothing committed yet: only the row from before
            await o.H.Rpc.TxRollback(new SessionRef { SessionId = o.Sid });
            Assert.Equal(1L, await CountAsync("Q_S10_IDS"));
            Assert.Equal("taken", await ScalarAsync("SELECT name FROM q_s10_ids"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [IntegrationFact]
    public async Task A_cancel_mid_import_leaves_nothing_in_one_transaction()
    {
        await ExecAsync(Drop("Q_S10_BIG"), "CREATE TABLE Q_S10_BIG (id NUMBER(10) PRIMARY KEY, a VARCHAR2(40 CHAR), b NUMBER(12,2), c TIMESTAMP(6))");
        await using var o = await OpenAsync();
        var dir = Directory.CreateTempSubdirectory("qora-import-").FullName;
        try
        {
            var csv = await CsvAsync(dir, "big.csv", 400_000, i => $"{i},value number {i},{i}.5,2026-02-03 04:05:06.{i % 1000000:000000}", "ID,A,B,C");
            var ins = await o.H.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
            var run = await o.H.Rpc.ImportRun(new ImportRunParams { SessionId = o.Sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = Schema, Table = "Q_S10_BIG" });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!o.H.Notifier.Of<ImportProgressEvent>().Any(e => e.JobId == run.JobId && e.RowsLoaded >= 5000) && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True((await o.H.Rpc.ImportCancel(new ImportJobRef { JobId = run.JobId! })).Cancelled);
            var done = await WaitImportAsync(o.H, run.JobId!);
            Assert.True(done.Cancelled, done.Error);
            Assert.Equal(0L, await CountAsync("Q_S10_BIG"));
            Assert.Equal(TxState.None, (await o.H.Rpc.SessionStateGet(new SessionRef { SessionId = o.Sid })).TxState);
            // The tab runs again.
            Assert.Null((await o.H.RunAsync(o.Sid, "SELECT 1 AS one FROM dual")).Done.Error);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ---------------------------------------------------------------------------------------------------- Prod guard

    [IntegrationFact]
    public async Task On_a_prod_profile_edits_and_imports_are_blocked_in_the_sidecar_until_confirmed()
    {
        await OrdersAsync();
        await using var o = await OpenAsync(EnvironmentKind.Prod);
        var (_, exec, _) = await SelectAsync(o, $"SELECT id, name FROM {Orders} ORDER BY id");
        var patch = new GridPatchParams
        {
            SessionId = o.Sid, ExecutionId = exec,
            Updates = [new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["NAME"] = V("prod") }, Original = new() { ["NAME"] = V("one") } }],
            Deletes = [new GridRowDelete { RowIndex = 2, Key = [V("3")], Original = new() { ["NAME"] = V("three") } }],
        };
        var blocked = await o.H.Rpc.DataApplyPatch(patch);
        Assert.Equal(GuardReason.ProdWrite, blocked.Blocked?.Reason);
        var destructive = await o.H.Rpc.DataApplyPatch(patch with { ConfirmedProd = true });
        Assert.Equal(GuardReason.Destructive, destructive.Blocked?.Reason);
        Assert.Equal("one", await ScalarAsync($"SELECT name FROM {Orders} WHERE id = 1"));
        var applied = await o.H.Rpc.DataApplyPatch(patch with { ConfirmedProd = true, ConfirmedDestructive = true });
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal("prod", await ScalarAsync($"SELECT name FROM {Orders} WHERE id = 1"));

        var dir = Directory.CreateTempSubdirectory("qora-import-").FullName;
        try
        {
            await ExecAsync(Drop("Q_S10_PROD"), "CREATE TABLE Q_S10_PROD (id NUMBER PRIMARY KEY)");
            await RefreshAsync(o.H, o.P);
            var csv = await CsvAsync(dir, "p.csv", 3, i => i.ToString(CultureInfo.InvariantCulture), "ID");
            var ins = await o.H.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
            var p = new ImportRunParams { SessionId = o.Sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = Schema, Table = "Q_S10_PROD" };
            // Every write on Prod needs its confirmation, whatever ran before on the tab.
            var guarded = await o.H.Rpc.ImportRun(p);
            Assert.Equal(GuardReason.ProdWrite, guarded.Blocked?.Reason);
            Assert.Null(guarded.JobId);
            Assert.Equal(0L, await CountAsync("Q_S10_PROD"));
            var run = await o.H.Rpc.ImportRun(p with { ConfirmedProd = true });
            Assert.Null((await WaitImportAsync(o.H, run.JobId!)).Error);
            Assert.Equal(3L, await CountAsync("Q_S10_PROD"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
