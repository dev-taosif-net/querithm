using System.Text;
using Microsoft.Data.Sqlite;
using Querithm.Contract;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// Phase D1: grid edits and imports on SQLite — proven keys only (a nullable declared PRIMARY KEY is not one), original-value
/// conflict checks without a row version, affinity doing the type conversion, and the generic INSERT loader for imports.
/// </summary>
public sealed class SqliteEditImportTests
{
    static GridValue V(string? text) => text == null ? new GridValue { IsNull = true } : new GridValue { Text = text };

    static async Task<(SqliteHost H, Profile P, string Sid, string Path)> OpenAsync(string setup, FileAccessMode mode = FileAccessMode.ReadWrite)
    {
        var h = new SqliteHost();
        var path = h.NewDatabase(setup: setup);
        var (p, sid) = await h.OpenFileAsync(path, mode);
        await RefreshAsync(h, p, sid, _ => true);
        return (h, p, sid, path);
    }

    /// <summary>Editability resolves result columns against the snapshot, so it must hold the tables first.</summary>
    static async Task RefreshAsync(SqliteHost h, Profile p, string sid, Func<SchemaSnapshot, bool> ready)
    {
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = "main", SessionId = sid, Full = true });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        SchemaSnapshot snap;
        do
        {
            await Task.Delay(50);
            snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = "main", SessionId = sid });
        } while (!(snap.Objects.Length > 0 && ready(snap)) && DateTime.UtcNow < deadline);
    }

    static QueryResultSetEvent ResultSet(SqliteHost h, string executionId) =>
        h.Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == executionId);

    static object? Scalar(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    const string Orders = """
        CREATE TABLE orders (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER, price REAL, raw BLOB);
        INSERT INTO orders (name, qty, price, raw) VALUES ('one', 1, 0.1, x'00'), ('two', 2, 2.5, NULL), ('three', 3, NULL, NULL);
        """;

    [Fact]
    public async Task A_table_with_a_rowid_key_is_editable_and_blob_columns_are_locked()
    {
        var (h, _, sid, _) = await OpenAsync(Orders);
        await using var _ = h;
        var (done, _, id) = await h.RunAsync(sid, "SELECT id, name, qty, price, raw FROM orders ORDER BY id");
        Assert.Null(done.Error);
        var edit = ResultSet(h, id).Edit!;
        Assert.True(edit.Editable, edit.Reason);
        Assert.Equal(["id"], edit.KeyColumns);
        Assert.False(edit.Columns.Single(c => c.Column == "name").Locked);
        Assert.True(edit.Columns.Single(c => c.Column == "raw").Locked);
    }

    [Fact]
    public async Task Updates_inserts_and_deletes_are_written_and_affinity_types_the_values()
    {
        var (h, _, sid, path) = await OpenAsync(Orders);
        await using var _ = h;
        var (_, _, exec) = await h.RunAsync(sid, "SELECT id, name, qty, price FROM orders ORDER BY id");
        var r = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates = [new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["qty"] = V("7"), ["price"] = V("0.30000000000000004") }, Original = new() { ["qty"] = V("1"), ["price"] = V("0.1") } }],
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["name"] = V("four"), ["qty"] = V("4") } }],
            // Delete row 2, not the last one: SQLite hands the next rowid out as max(rowid) + 1.
            Deletes = [new GridRowDelete { RowIndex = 1, Key = [V("2")], Original = new() { ["name"] = V("two"), ["qty"] = V("2") } }],
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal((1, 1, 1), (r.AppliedUpdates, r.AppliedInserts, r.AppliedDeletes));
        Assert.Equal("4", Assert.Single(r.InsertedKeys!["0"]).Text);
        Assert.Equal("integer", Scalar(path, "SELECT typeof(qty) FROM orders WHERE id = 1")); // the text '7' was stored as an integer
        Assert.Equal(0.30000000000000004, Scalar(path, "SELECT price FROM orders WHERE id = 1"));
        Assert.Equal(0L, Scalar(path, "SELECT count(*) FROM orders WHERE id = 2"));
        Assert.Equal("four", Scalar(path, "SELECT name FROM orders WHERE id = 4"));
    }

    [Fact]
    public async Task A_row_changed_by_someone_else_is_a_conflict_and_nothing_is_written()
    {
        var (h, _, sid, path) = await OpenAsync(Orders);
        await using var _ = h;
        var (_, _, exec) = await h.RunAsync(sid, "SELECT id, name, qty FROM orders ORDER BY id");
        using (var other = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            other.Open();
            using var cmd = other.CreateCommand();
            cmd.CommandText = "UPDATE orders SET name = 'changed' WHERE id = 2";
            cmd.ExecuteNonQuery();
        }
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
        Assert.Contains(r.Conflicts ?? [], c => c.RowIndex == 1);
        Assert.Equal("one", Scalar(path, "SELECT name FROM orders WHERE id = 1")); // rolled back with the conflicting row
        Assert.Equal("changed", Scalar(path, "SELECT name FROM orders WHERE id = 2"));
    }

    [Fact]
    public async Task Row_checks_before_an_edit_find_rows_that_changed_on_disk()
    {
        // data.rowTokens joins the keys as a derived table: SQLite has no column list on one (regression: VALUES … AS v(…)).
        var (h, _, sid, path) = await OpenAsync(Orders);
        await using var _ = h;
        var (_, _, exec) = await h.RunAsync(sid, "SELECT id, name, qty FROM orders ORDER BY id");
        using (var other = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            other.Open();
            using var cmd = other.CreateCommand();
            cmd.CommandText = "UPDATE orders SET qty = 20 WHERE id = 2";
            cmd.ExecuteNonQuery();
        }
        var r = await h.Rpc.DataRowTokens(new RowTokensParams
        {
            SessionId = sid, ExecutionId = exec,
            Keys = [[V("1")], [V("2")], [V("9")]],
            Expect = [new() { ["qty"] = V("1") }, new() { ["qty"] = V("2") }, new() { ["qty"] = V("1") }],
        });
        Assert.Equal(["", null, null], r.Tokens); // unchanged, changed on disk, gone
    }

    [Fact]
    public async Task Row_checks_for_more_keys_than_a_compound_select_may_hold_are_split()
    {
        // Grid-edit audit: the keys travel as SELECT … UNION ALL SELECT …, which SQLite caps at 500 terms; 1000 per statement
        // failed with "too many terms in compound SELECT".
        var (h, _, sid, _) = await OpenAsync("""
            CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 600) INSERT INTO blobs SELECT i, randomblob(4) FROM n;
            """);
        await using var _ = h;
        var (_, _, exec) = await h.RunAsync(sid, "SELECT id, data FROM blobs");
        Assert.True(ResultSet(h, exec).Edit!.Editable, ResultSet(h, exec).Edit!.Reason);
        var r = await h.Rpc.DataRowTokens(new RowTokensParams
        {
            SessionId = sid, ExecutionId = exec, Keys = Enumerable.Range(1, 600).Select(i => new[] { V(i.ToString()) }).ToArray(),
        });
        Assert.Equal(600, r.Tokens.Length);
        Assert.All(r.Tokens, t => Assert.Equal("", t)); // every row found
    }

    [Fact]
    public async Task A_self_join_and_a_compound_select_are_read_only()
    {
        // Grid-edit audit: SQLite names one base table for both aliases of a self-join, and the leftmost table for every
        // column of a UNION, so a key could update another row than the one shown.
        var (h, _, sid, _) = await OpenAsync(Orders + "CREATE TABLE archive (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER); INSERT INTO archive VALUES (9, 'old', 1);");
        await using var _ = h;
        var (_, _, plain) = await h.RunAsync(sid, "SELECT id, name FROM orders");
        Assert.True(ResultSet(h, plain).Edit!.Editable, ResultSet(h, plain).Edit!.Reason);
        var (_, _, self) = await h.RunAsync(sid, "SELECT a.id, b.name FROM orders a JOIN orders b ON b.id = a.id + 1");
        Assert.False(ResultSet(h, self).Edit!.Editable);
        Assert.Equal("Read-only: the same table (orders) appears more than once in the query", ResultSet(h, self).Edit!.Reason);
        var (_, _, union) = await h.RunAsync(sid, "SELECT id, name FROM orders UNION ALL SELECT id, name FROM archive");
        Assert.False(ResultSet(h, union).Edit!.Editable);
        Assert.Equal("Read-only: the query combines several queries (UNION)", ResultSet(h, union).Edit!.Reason);
    }

    [Fact]
    public async Task A_partial_or_expression_unique_index_is_not_a_row_key()
    {
        var (h, _, sid, _) = await OpenAsync("""
            CREATE TABLE p (code TEXT NOT NULL, active INTEGER NOT NULL, label TEXT); CREATE UNIQUE INDEX ux_active ON p (code) WHERE active = 1;
            INSERT INTO p VALUES ('a', 0, 'x'), ('a', 0, 'y'), ('a', 1, 'z');
            CREATE TABLE e (code TEXT NOT NULL, label TEXT); CREATE UNIQUE INDEX ux_lower ON e (lower(code), code);
            INSERT INTO e VALUES ('a', 'x'), ('A', 'y');
            CREATE TABLE ok (code TEXT NOT NULL, label TEXT); CREATE UNIQUE INDEX ux_code ON ok (code); INSERT INTO ok VALUES ('a', 'x');
            """);
        await using var _ = h;
        foreach (var t in new[] { "p", "e" })
        {
            var (_, _, id) = await h.RunAsync(sid, $"SELECT code, label FROM {t}");
            var edit = ResultSet(h, id).Edit!;
            Assert.False(edit.Editable, $"{t} is editable with key {string.Join(",", edit.KeyColumns)}");
            Assert.Contains("no primary key", edit.Reason);
        }
        var (_, _, ok) = await h.RunAsync(sid, "SELECT code, label FROM ok");
        Assert.Equal(["code"], ResultSet(h, ok).Edit!.KeyColumns);
    }

    [Fact]
    public async Task A_table_created_in_the_tab_is_editable_on_the_next_run_untyped_columns_included()
    {
        // The UI flow: no explicit refresh — the DDL-triggered one must be enough, and an untyped column stays writable.
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: "CREATE TABLE seed (x);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.RunAsync(sid, "CREATE TABLE t (id INTEGER PRIMARY KEY, v); INSERT INTO t(v) VALUES (1),('a'); SELECT * FROM t;");
        ResultEditInfo? edit = null;
        for (var i = 0; i < 40 && edit is not { Editable: true }; i++)
        {
            await Task.Delay(100);
            var (_, _, id) = await h.RunAsync(sid, "SELECT * FROM t;");
            edit = ResultSet(h, id).Edit;
        }
        Assert.True(edit!.Editable, edit.Reason);
        var v = edit.Columns.Single(c => c.Column == "v");
        Assert.False(v.Locked, v.LockReason);
        Assert.True(edit.Columns.Single(c => c.Column == "id").Locked); // the rowid alias: numbered by the engine
    }

    [Fact]
    public async Task A_nullable_declared_primary_key_does_not_make_rows_editable()
    {
        var (h, _, sid, _) = await OpenAsync("CREATE TABLE codes (code TEXT PRIMARY KEY, label TEXT); INSERT INTO codes VALUES (NULL, 'a'), (NULL, 'b'), ('x', 'c');");
        await using var _ = h;
        var (_, _, id) = await h.RunAsync(sid, "SELECT code, label FROM codes");
        var edit = ResultSet(h, id).Edit!;
        Assert.False(edit.Editable);
        Assert.Contains("primary key", edit.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_not_null_primary_key_and_a_without_rowid_table_are_editable()
    {
        var (h, _, sid, _) = await OpenAsync("""
            CREATE TABLE a (code TEXT NOT NULL PRIMARY KEY, v); INSERT INTO a VALUES ('k', 1);
            CREATE TABLE b (code TEXT PRIMARY KEY, v) WITHOUT ROWID; INSERT INTO b VALUES ('k', 1);
            """);
        await using var _ = h;
        foreach (var t in new[] { "a", "b" })
        {
            var (_, _, id) = await h.RunAsync(sid, $"SELECT code, v FROM {t}");
            Assert.True(ResultSet(h, id).Edit!.Editable, $"{t}: {ResultSet(h, id).Edit!.Reason}");
        }
    }

    [Fact]
    public async Task A_read_only_grant_refuses_the_write_and_leaves_the_file_alone()
    {
        var (h, _, sid, path) = await OpenAsync(Orders, FileAccessMode.ReadOnly);
        await using var _ = h;
        var before = await File.ReadAllBytesAsync(path);
        var (_, _, exec) = await h.RunAsync(sid, "SELECT id, name FROM orders ORDER BY id");
        var r = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates = [new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["name"] = V("uno") }, Original = new() { ["name"] = V("one") } }],
        });
        Assert.False(r.Ok);
        Assert.Contains("read-only", r.Error ?? "");
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_csv_imports_into_a_new_table_and_a_failure_leaves_nothing_behind()
    {
        var (h, p, sid, path) = await OpenAsync("CREATE TABLE other (x);");
        await using var _ = h;
        var csv = Path.Combine(h.Dir, "items.csv");
        var sb = new StringBuilder("id,name,qty,price,born\n");
        for (var i = 1; i <= 2500; i++) sb.Append($"{i},item {i},{i % 50},{i}.25,2026-01-{1 + i % 28:00}\n");
        await File.WriteAllTextAsync(csv, sb.ToString(), new UTF8Encoding(false));
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var target = await h.Rpc.ImportCheckTarget(new ImportTargetParams { SessionId = sid, InspectionId = ins.InspectionId, Schema = "main", Table = "items", NewTable = true, PrimaryKey = ["id"] });
        Assert.Contains("INTEGER", target.CreateTableSql);
        Assert.Null((await h.RunAsync(sid, target.CreateTableSql!)).Done.Error);
        await RefreshAsync(h, p, sid, s => s.Objects.Any(o => o.Name == "items"));
        // id INTEGER PRIMARY KEY is the rowid (an identity column): the file's ids are kept only when asked, as on SQL Server.
        var keep = new ImportRunOptions { KeepIdentity = true };
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = "main", Table = "items", Options = keep });
        Assert.NotNull(run.JobId);
        ImportProgressEvent? done = null;
        for (var i = 0; i < 400 && (done = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == run.JobId && e.Done)) == null; i++) await Task.Delay(25);
        Assert.NotNull(done);
        Assert.Null(done!.Error);
        Assert.Equal(2500L, done.RowsLoaded);
        Assert.Equal(2500L, Scalar(path, "SELECT count(*) FROM items"));
        Assert.Equal("integer", Scalar(path, "SELECT typeof(qty) FROM items WHERE id = 7"));

        // A second load of the same keys fails on the primary key: one transaction, so not one row of it stays.
        var ins2 = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var again = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins2.InspectionId, FileToken = csv, FilePath = csv, Schema = "main", Table = "items", Options = keep });
        ImportProgressEvent? failed = null;
        for (var i = 0; i < 400 && (failed = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == again.JobId && e.Done)) == null; i++) await Task.Delay(25);
        Assert.NotNull(failed?.Error);
        Assert.Equal(2500L, Scalar(path, "SELECT count(*) FROM items"));
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
    }

    /// <summary>Imports <paramref name="csv"/> into main.items and waits for the final progress event.</summary>
    static async Task<ImportProgressEvent> ImportAsync(SqliteHost h, string sid, string csv, ImportRunOptions options)
    {
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = "main", Table = "items", Options = options });
        ImportProgressEvent? done = null;
        for (var i = 0; i < 400 && (done = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == run.JobId && e.Done)) == null; i++) await Task.Delay(25);
        return done ?? throw new TimeoutException("The import did not finish.");
    }

    static async Task<string> IdsCsvAsync(SqliteHost h, int rows)
    {
        var csv = Path.Combine(h.Dir, $"ids{rows}.csv");
        var sb = new StringBuilder("id,name\n");
        for (var i = 1; i <= rows; i++) sb.Append($"{i},item {i}\n");
        await File.WriteAllTextAsync(csv, sb.ToString(), new UTF8Encoding(false));
        return csv;
    }

    [Fact]
    public async Task Batches_mode_commits_every_batch_and_a_failing_row_keeps_only_the_committed_ones()
    {
        var (h, _, sid, path) = await OpenAsync("CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT);");
        await using (h)
        {
            var ok = await ImportAsync(h, sid, await IdsCsvAsync(h, 2500), new ImportRunOptions { TransactionMode = ImportTransactionMode.Batches, CommitEvery = 1000, KeepIdentity = true });
            Assert.Null(ok.Error);
            Assert.Equal(2500L, ok.RowsLoaded);
            Assert.Equal(2500L, Scalar(path, "SELECT count(*) FROM items"));
        }
        var (h2, _, sid2, path2) = await OpenAsync("CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO items VALUES (1800, 'taken');");
        await using var _ = h2;
        var failed = await ImportAsync(h2, sid2, await IdsCsvAsync(h2, 2500), new ImportRunOptions { TransactionMode = ImportTransactionMode.Batches, CommitEvery = 1000, KeepIdentity = true });
        Assert.NotNull(failed.Error);
        Assert.Equal(1000L, failed.RowsLoaded); // the first batch was committed; rows 1001-1799 went with the failed one
        Assert.Equal(1001L, Scalar(path2, "SELECT count(*) FROM items"));
        Assert.Equal(TxState.None, (await h2.Rpc.SessionStateGet(new SessionRef { SessionId = sid2 })).TxState);
    }

    [Fact]
    public async Task Inside_an_open_transaction_the_import_joins_it_and_a_failure_undoes_only_the_import()
    {
        var (h, _, sid, path) = await OpenAsync("CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO items VALUES (1800, 'taken');");
        await using var _ = h;
        var csv = await IdsCsvAsync(h, 2500);
        static async Task<string?> Count(SqliteHost h, string sid) => ((Apache.Arrow.StringArray)(await h.RunAsync(sid, "SELECT count(*) AS n FROM items")).Batches.Single().Column("n")).GetString(0);
        Assert.Null((await h.RunAsync(sid, "BEGIN; INSERT INTO items VALUES (5000, 'mine');")).Done.Error);
        var failed = await ImportAsync(h, sid, csv, new ImportRunOptions { KeepIdentity = true });
        Assert.NotNull(failed.Error);
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal("2", await Count(h, sid)); // the user's own row stays, not one imported row
        Assert.Null((await h.RunAsync(sid, "DELETE FROM items WHERE id = 1800")).Done.Error);
        var ok = await ImportAsync(h, sid, csv, new ImportRunOptions { KeepIdentity = true });
        Assert.Null(ok.Error);
        Assert.Equal("2501", await Count(h, sid));
        Assert.Null((await h.RunAsync(sid, "ROLLBACK")).Done.Error);
        Assert.Equal(1L, Scalar(path, "SELECT count(*) FROM items")); // the import was part of the rolled-back transaction
    }

    [Fact]
    public async Task An_in_memory_database_imports_into_a_table_created_just_before_through_its_own_view()
    {
        await using var h = new SqliteHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var csv = await IdsCsvAsync(h, 2500);
        // Right after the CREATE: the table exists only in this session's own view, never in a shared snapshot.
        Assert.Null((await h.RunAsync(sid, "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT)")).Done.Error);
        var done = await ImportAsync(h, sid, csv, new ImportRunOptions { KeepIdentity = true });
        Assert.Null(done.Error);
        Assert.Equal(2500L, done.RowsLoaded);
        Assert.Null((await h.RunAsync(sid, "CREATE TEMP TABLE scratch (id INTEGER PRIMARY KEY, name TEXT)")).Done.Error);
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = "temp", Table = "scratch", Options = new ImportRunOptions { KeepIdentity = true } });
        ImportProgressEvent? temp = null;
        for (var i = 0; i < 400 && (temp = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == run.JobId && e.Done)) == null; i++) await Task.Delay(25);
        Assert.Null(temp?.Error);
        Assert.Equal(2500L, temp!.RowsLoaded);

        // A table created inside the open transaction is read when it ends (D0d): the import says so instead of "does not exist".
        Assert.Null((await h.RunAsync(sid, "BEGIN; CREATE TABLE later (id INTEGER PRIMARY KEY, name TEXT);")).Done.Error);
        var ins2 = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var refused = await Assert.ThrowsAsync<Querithm.Core.QuerithmException>(() => h.Rpc.ImportRun(new ImportRunParams
        {
            SessionId = sid, InspectionId = ins2.InspectionId, FileToken = csv, FilePath = csv, Schema = "main", Table = "later", Options = new ImportRunOptions { KeepIdentity = true },
        }));
        Assert.Contains("Commit, then import", refused.Message);
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Null((await h.RunAsync(sid, "COMMIT")).Done.Error);
    }

    /// <summary>A row named "slow" takes seconds to insert (its trigger counts a large cross join), so Cancel lands mid-patch.</summary>
    const string Slow = """
        CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE nums (x INTEGER);
        WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 1000) INSERT INTO nums SELECT x FROM c;
        CREATE TABLE slow_log (n INTEGER);
        CREATE TRIGGER slow AFTER INSERT ON t WHEN NEW.name = 'slow' BEGIN
          INSERT INTO slow_log SELECT count(*) FROM nums a, nums b, nums c;
        END;
        INSERT INTO t (name) VALUES ('one');
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_grid_apply_cancelled_mid_patch_writes_nothing_and_leaves_the_tab_usable(bool inTransaction)
    {
        // H5: the patch's first row is in when the cancel lands on the second one. Nothing of it may stay (SQLite rolls an
        // interrupted write's whole transaction back itself), and the undo must not close the tab's own connection.
        var (h, p, sid, path) = await OpenAsync(Slow);
        await using var _ = h;
        await RefreshAsync(h, p, sid, s => s.Objects.Any(o => o.Name == "t"));
        var (_, _, exec) = await h.RunAsync(sid, "SELECT id, name FROM t ORDER BY id");
        if (inTransaction) await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var started = h.Notifier.Of<QueryStartedEvent>().Count();
        // SQLite's driver runs synchronously: the Apply gets a thread of its own, as an RPC does in the host.
        var apply = Task.Run(() => h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["name"] = V("fast") } }, new GridRowInsert { TempIndex = 1, Values = new() { ["name"] = V("slow") } }],
        }));
        string? id = null;
        for (var i = 0; i < 400 && (id = h.Notifier.Of<QueryStartedEvent>().Skip(started).FirstOrDefault()?.ExecutionId) == null; i++) await Task.Delay(10);
        Assert.NotNull(id);
        await Task.Delay(300);
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = id! });

        var ex = await Assert.ThrowsAsync<Querithm.Core.QuerithmException>(() => apply.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("cancelled", ex.Code);
        Assert.Equal(ExecState.Cancelled, (await h.Notifier.WaitDone(id!)).State);
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        // The tab's connection is still the one it had: a retry works and writes each row once.
        var (count, _, _) = await h.RunAsync(sid, "SELECT count(*) AS n FROM t");
        Assert.Null(count.Error);
        Assert.Equal(1L, Scalar(path, "SELECT count(*) FROM t"));
    }
}
