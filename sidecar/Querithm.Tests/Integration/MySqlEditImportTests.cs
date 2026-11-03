using System.Text;
using MySqlConnector;
using Querithm.Contract;
using static Querithm.Tests.Integration.MySqlIntegrationTests;
using MyHost = Querithm.Tests.Integration.MySqlIntegrationTests.Host;

namespace Querithm.Tests.Integration;

/// <summary>
/// Phase D2 grid edits and imports on MySQL 8.4 and MariaDB 11.8 (QUERITHM_IT=1): keys proven by the catalog, original-value
/// conflict checks without a row version, AUTO_INCREMENT keys read back with LAST_INSERT_ID(), and the batched INSERT loader.
/// </summary>
public sealed class MySqlEditImportTests
{
    static GridValue V(string? text) => text == null ? new GridValue { IsNull = true } : new GridValue { Text = text };

    static async Task<(MyHost H, Profile P, string Sid)> OpenWithAsync(string product, string setup)
    {
        await EnsureDatabaseAsync(product);
        await using (var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False;Database={Db(product)}"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = setup;
            await cmd.ExecuteNonQueryAsync();
        }
        var (h, p, sid) = await OpenAsync(product);
        await RefreshAsync(h, p, sid, product, _ => true);
        return (h, p, sid);
    }

    /// <summary>Editability resolves result columns against the snapshot, so it must hold the tables first.</summary>
    static async Task RefreshAsync(MyHost h, Profile p, string sid, string product, Func<SchemaSnapshot, bool> ready)
    {
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = Db(product), SessionId = sid, Full = true });
        var deadline = DateTime.UtcNow.AddSeconds(15);
        SchemaSnapshot snap;
        do
        {
            await Task.Delay(50);
            snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = Db(product), SessionId = sid });
        } while (!(snap.Objects.Length > 0 && ready(snap)) && DateTime.UtcNow < deadline);
    }

    static ResultEditInfo EditOf(MyHost h, string executionId) => h.Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == executionId).Edit!;

    static async Task<object?> ScalarAsync(string product, string sql)
    {
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False;Database={Db(product)}");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    const string Orders = """
        DROP TABLE IF EXISTS e_orders;
        CREATE TABLE e_orders (
          id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(40) NOT NULL, qty INT UNSIGNED, price DECIMAL(12,4), shipped DATETIME(3),
          active TINYINT(1) NOT NULL DEFAULT 1, raw VARBINARY(16), score DOUBLE, pos POINT NULL);
        INSERT INTO e_orders (name, qty, price, shipped, active, raw, score) VALUES
          ('one', 1, 1.5000, '2026-01-02 03:04:05.120', 1, 0x0A0B, 0.1), ('two', 2, 2.0000, NULL, 0, NULL, NULL), ('three', 3, NULL, NULL, 1, NULL, NULL);
        """;

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task A_table_with_a_primary_key_is_editable_and_spatial_columns_are_locked(string product)
    {
        var (h, _, sid) = await OpenWithAsync(product, Orders);
        await using var _ = h;
        var (done, _, id) = await Run(h, sid, "SELECT id, name, qty, price, shipped, active, raw, score, pos FROM e_orders ORDER BY id");
        Assert.Null(done.Error);
        var edit = EditOf(h, id);
        Assert.True(edit.Editable, edit.Reason);
        Assert.Equal(["id"], edit.KeyColumns);
        Assert.True(edit.Columns.Single(c => c.Column == "id").Generated);
        Assert.True(edit.Columns.Single(c => c.Column == "pos").Locked);
        Assert.False(edit.Columns.Single(c => c.Column == "score").Comparable);
        Assert.True(edit.Columns.Single(c => c.Column == "raw").Comparable);
        Assert.Contains("row version", edit.ConcurrencyNote);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Updates_inserts_and_deletes_are_written_with_their_types(string product)
    {
        var (h, _, sid) = await OpenWithAsync(product, Orders);
        await using var _ = h;
        var (_, _, exec) = await Run(h, sid, "SELECT id, name, qty, price, shipped, active, raw FROM e_orders ORDER BY id");
        var r = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates =
            [
                // The grid's text forms: DATETIME(3) without trailing zeros, booleans as 1/0, binary as 0x….
                new GridRowUpdate
                {
                    RowIndex = 0, Key = [V("1")],
                    Changes = new() { ["price"] = V("12345678.1234"), ["active"] = V("false"), ["raw"] = V("0xFF00"), ["name"] = V("one") },
                    Original = new() { ["price"] = V("1.5000"), ["active"] = V("1"), ["raw"] = V("0x0A0B"), ["shipped"] = V("2026-01-02 03:04:05.12"), ["name"] = V("one") },
                },
            ],
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["name"] = V("four"), ["shipped"] = V("2026-05-06 07:08:09.5") } }],
            Deletes = [new GridRowDelete { RowIndex = 1, Key = [V("2")], Original = new() { ["name"] = V("two"), ["qty"] = V("2") } }],
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal((1, 1, 1), (r.AppliedUpdates, r.AppliedInserts, r.AppliedDeletes));
        Assert.Equal("4", Assert.Single(r.InsertedKeys!["0"]).Text); // LAST_INSERT_ID()
        Assert.Equal(12345678.1234m, await ScalarAsync(product, "SELECT price FROM e_orders WHERE id = 1"));
        Assert.Equal("0", Convert.ToString(await ScalarAsync(product, "SELECT CAST(active AS CHAR) FROM e_orders WHERE id = 1")));
        Assert.Equal("FF00", await ScalarAsync(product, "SELECT HEX(raw) FROM e_orders WHERE id = 1"));
        Assert.Equal("2026-05-06 07:08:09.500", await ScalarAsync(product, "SELECT CAST(shipped AS CHAR) FROM e_orders WHERE id = 4"));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(product, "SELECT COUNT(*) FROM e_orders WHERE id = 2")));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task A_row_changed_by_someone_else_is_a_conflict_and_nothing_is_written(string product)
    {
        var (h, _, sid) = await OpenWithAsync(product, Orders);
        await using var _ = h;
        var (_, _, exec) = await Run(h, sid, "SELECT id, name, qty FROM e_orders ORDER BY id");
        await ScalarAsync(product, "UPDATE e_orders SET name = 'changed' WHERE id = 2");
        // Before an edit the grid checks the rows it is about to write (data.rowTokens): MariaDB has no column list on a derived table.
        var tokens = await h.Rpc.DataRowTokens(new RowTokensParams
        {
            SessionId = sid, ExecutionId = exec, Keys = [[V("1")], [V("2")], [V("99")]],
            Expect = [new() { ["name"] = V("one") }, new() { ["name"] = V("two") }, new() { ["name"] = V("x") }],
        });
        Assert.Equal(["", null, null], tokens.Tokens);
        var r = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates =
            [
                new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["name"] = V("uno") }, Original = new() { ["name"] = V("one") } },
                new GridRowUpdate { RowIndex = 1, Key = [V("2")], Changes = new() { ["name"] = V("dos") }, Original = new() { ["name"] = V("two") } },
            ],
        });
        Assert.False(r.Ok);
        var conflict = Assert.Single(r.Conflicts ?? []);
        Assert.Equal((1, "changedOnServer"), (conflict.RowIndex, conflict.Reason));
        Assert.Equal("changed", conflict.Current?["name"].Text);
        Assert.Equal("one", await ScalarAsync(product, "SELECT name FROM e_orders WHERE id = 1")); // rolled back with the conflicting row
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task A_value_the_column_cannot_take_is_refused_before_anything_is_written(string product)
    {
        var (h, _, sid) = await OpenWithAsync(product, Orders);
        await using var _ = h;
        var (_, _, exec) = await Run(h, sid, "SELECT id, name, qty FROM e_orders ORDER BY id");
        var r = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates =
            [
                new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["name"] = V("uno") }, Original = new() { ["name"] = V("one") } },
                new GridRowUpdate { RowIndex = 1, Key = [V("2")], Changes = new() { ["qty"] = V("-5") }, Original = new() { ["qty"] = V("2") } },
            ],
        });
        Assert.False(r.Ok);
        Assert.Contains("unsigned", r.Error);
        Assert.Equal("one", await ScalarAsync(product, "SELECT name FROM e_orders WHERE id = 1"));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Keys_must_be_proven_and_a_self_join_is_read_only(string product)
    {
        var (h, _, sid) = await OpenWithAsync(product, """
            DROP TABLE IF EXISTS e_uq; DROP TABLE IF EXISTS e_nullable_uq; DROP TABLE IF EXISTS e_code;
            CREATE TABLE e_uq (code VARCHAR(10) NOT NULL, v INT, UNIQUE KEY uq_code (code)); INSERT INTO e_uq VALUES ('a', 1);
            CREATE TABLE e_nullable_uq (code VARCHAR(10) NULL, v INT, UNIQUE KEY uq_code (code)); INSERT INTO e_nullable_uq VALUES (NULL, 1), (NULL, 2);
            CREATE TABLE e_code (code CHAR(3) PRIMARY KEY, label VARCHAR(10)); INSERT INTO e_code VALUES ('abc', 'x');
            """ + Orders);
        await using var _ = h;
        var (_, _, uq) = await Run(h, sid, "SELECT code, v FROM e_uq");
        Assert.Equal(["code"], EditOf(h, uq).KeyColumns);
        var (_, _, nuq) = await Run(h, sid, "SELECT code, v FROM e_nullable_uq");
        Assert.False(EditOf(h, nuq).Editable);
        Assert.Contains("no primary key", EditOf(h, nuq).Reason);
        var (_, _, self) = await Run(h, sid, "SELECT a.id, b.name FROM e_orders a JOIN e_orders b ON b.id = a.id + 1");
        Assert.False(EditOf(h, self).Editable);
        // A key the user types is read back as given (no LAST_INSERT_ID()).
        var (_, _, code) = await Run(h, sid, "SELECT code, label FROM e_code");
        var r = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = code, Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["code"] = V("xyz"), ["label"] = V("y") } }],
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("xyz", Assert.Single(r.InsertedKeys!["0"]).Text);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task A_csv_imports_with_multi_row_inserts_and_a_failure_leaves_nothing_behind(string product)
    {
        var (h, p, sid) = await OpenWithAsync(product, "DROP TABLE IF EXISTS i_items;" + Orders);
        await using var _ = h;
        var dir = Directory.CreateTempSubdirectory("qmysql-import-").FullName;
        try
        {
            var csv = Path.Combine(dir, "items.csv");
            var sb = new StringBuilder("id,name,qty,price,born,ok\n");
            for (var i = 1; i <= 3000; i++) sb.Append($"{i},item {i},{i % 50},{i}.25,2026-01-{1 + i % 28:00} 10:00:00,{(i % 2 == 0 ? "true" : "false")}\n");
            await File.WriteAllTextAsync(csv, sb.ToString(), new UTF8Encoding(false));
            var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
            var target = await h.Rpc.ImportCheckTarget(new ImportTargetParams
            {
                SessionId = sid, InspectionId = ins.InspectionId, Schema = Db(product), Table = "i_items", NewTable = true, PrimaryKey = ["id"],
            });
            Assert.Contains("tinyint(1)", target.CreateTableSql);
            Assert.Null((await Run(h, sid, target.CreateTableSql!)).Done.Error);
            await RefreshAsync(h, p, sid, product, s => s.Objects.Any(o => o.Name == "i_items"));
            var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = Db(product), Table = "i_items" });
            var done = await WaitImportAsync(h, run.JobId!);
            Assert.Null(done.Error);
            Assert.Equal(3000L, done.RowsLoaded);
            Assert.Equal(3000L, Convert.ToInt64(await ScalarAsync(product, "SELECT COUNT(*) FROM i_items")));
            Assert.Equal(1500L, Convert.ToInt64(await ScalarAsync(product, "SELECT SUM(ok) FROM i_items")));

            // A second load of the same keys fails on the primary key: one transaction, so not one row of it stays.
            var ins2 = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
            var again = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins2.InspectionId, FileToken = csv, FilePath = csv, Schema = Db(product), Table = "i_items" });
            var failed = await WaitImportAsync(h, again.JobId!);
            Assert.NotNull(failed.Error);
            Assert.Equal(3000L, Convert.ToInt64(await ScalarAsync(product, "SELECT COUNT(*) FROM i_items")));
            Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);

            // A value the column cannot take rejects its row (the reject log) instead of failing the statement.
            var bad = Path.Combine(dir, "bad.csv");
            await File.WriteAllTextAsync(bad, "id,name,qty,price,born,ok\n5001,a,1,1.25,2026-01-01 10:00:00,true\n5002,b,-3,1.25,2026-01-01 10:00:00,true\n", new UTF8Encoding(false));
            var ins3 = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = bad, FilePath = bad });
            await Run(h, sid, "ALTER TABLE i_items MODIFY qty INT UNSIGNED");
            await RefreshAsync(h, p, sid, product, s => s.Objects.Any(o => o.Name == "i_items" && o.Columns?.Any(c => c.Name == "qty" && c.Type.Contains("unsigned")) == true));
            var third = await h.Rpc.ImportRun(new ImportRunParams
            {
                SessionId = sid, InspectionId = ins3.InspectionId, FileToken = bad, FilePath = bad, Schema = Db(product), Table = "i_items",
                Options = new ImportRunOptions { MaxRejects = 5 },
            });
            var partial = await WaitImportAsync(h, third.JobId!);
            Assert.Null(partial.Error);
            Assert.Equal((1L, 1L), (partial.RowsLoaded, partial.Rejected));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Batches_commit_as_they_go_and_inside_a_transaction_the_import_joins_it(string product)
    {
        var (h, p, sid) = await OpenWithAsync(product, "DROP TABLE IF EXISTS i_ids; CREATE TABLE i_ids (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO i_ids VALUES (1800, 'taken');");
        await using var _ = h;
        await RefreshAsync(h, p, sid, product, s => s.Objects.Any(o => o.Name == "i_ids"));
        var dir = Directory.CreateTempSubdirectory("qmysql-import-").FullName;
        try
        {
            var csv = Path.Combine(dir, "ids.csv");
            var sb = new StringBuilder("id,name\n");
            for (var i = 1; i <= 2500; i++) sb.Append($"{i},item {i}\n");
            await File.WriteAllTextAsync(csv, sb.ToString(), new UTF8Encoding(false));
            async Task<ImportProgressEvent> ImportAsync(ImportRunOptions options)
            {
                var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
                var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = Db(product), Table = "i_ids", Options = options });
                return await WaitImportAsync(h, run.JobId!);
            }
            async Task<long> Count() => Convert.ToInt64(await ScalarAsync(product, "SELECT COUNT(*) FROM i_ids"));

            // Batches of 1,000: row 1800 fails, the first batch stays committed, rows 1001-1799 go with the failed batch.
            var failed = await ImportAsync(new ImportRunOptions { TransactionMode = ImportTransactionMode.Batches, CommitEvery = 1000 });
            Assert.NotNull(failed.Error);
            Assert.Equal(1000L, failed.RowsLoaded);
            Assert.Equal(1001L, await Count());
            Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);

            // Inside the user's transaction: a failing import undoes only itself; the user's ROLLBACK undoes the import too.
            Assert.Null((await Run(h, sid, "DELETE FROM i_ids WHERE id < 1800")).Done.Error);
            Assert.Null((await Run(h, sid, "START TRANSACTION; INSERT INTO i_ids VALUES (5000, 'mine');")).Done.Error);
            Assert.NotNull((await ImportAsync(new ImportRunOptions())).Error);
            Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
            Assert.Null((await Run(h, sid, "DELETE FROM i_ids WHERE id = 1800")).Done.Error);
            var ok = await ImportAsync(new ImportRunOptions());
            Assert.Null(ok.Error);
            Assert.Equal(2500L, ok.RowsLoaded);
            Assert.Null((await Run(h, sid, "ROLLBACK")).Done.Error);
            Assert.Equal(1L, await Count());
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    static async Task<ImportProgressEvent> WaitImportAsync(MyHost h, string jobId)
    {
        for (var i = 0; i < 600; i++)
        {
            if (h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == jobId && e.Done) is { } e) return e;
            await Task.Delay(25);
        }
        throw new TimeoutException("The import did not finish.");
    }
}
