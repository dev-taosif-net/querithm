using Apache.Arrow;
using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Core;

namespace Querithm.Tests.FileEngines;

/// <summary>Phase D1: SQLite end to end through the RPC surface, on real temp-dir files and in-memory databases (no Docker).</summary>
public class SqliteIntegrationTests
{
    static string[] Texts(RecordBatch b, string col) => Enumerable.Range(0, b.Length).Select(i => ((StringArray)b.Column(col)).GetString(i)).ToArray();

    [Fact]
    public async Task An_in_memory_database_runs_ddl_dml_and_queries_with_mixed_storage_classes()
    {
        await using var h = new SqliteHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (done, _, _) = await h.RunAsync(sid, "CREATE TABLE t (id INTEGER PRIMARY KEY, v); INSERT INTO t(v) VALUES (2), (10), ('2'), (2.5), (x'0aff'), (NULL);");
        Assert.Null(done.Error);
        var (q, batches, _) = await h.RunAsync(sid, "SELECT id, v FROM t ORDER BY id");
        Assert.Null(q.Error);
        var b = Assert.Single(batches);
        Assert.Equal((string?[])["2", "10", "2", "2.5", "0aff", null], Texts(b, "v"));
        Assert.Equal([1, 1, 3, 2, 4, 0], Enumerable.Range(0, 6).Select(i => (int)((UInt8Array)b.Column("v__class")).GetValue(i)!.Value));
        Assert.Equal(["1", "2", "3", "4", "5", "6"], Texts(b, "id")); // even the rowid travels as dynamic text
    }

    [Fact]
    public async Task A_read_only_file_reads_and_refuses_writes_without_changing_the_file()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v); INSERT INTO t(v) VALUES ('a');");
        var before = await File.ReadAllBytesAsync(path);
        var (_, sid) = await h.OpenFileAsync(path);
        var (q, batches, _) = await h.RunAsync(sid, "SELECT v FROM t");
        Assert.Null(q.Error);
        Assert.Equal(["a"], Texts(batches.Single(), "v"));
        var (w, _, _) = await h.RunAsync(sid, "INSERT INTO t(v) VALUES ('b')");
        Assert.Contains("read-only", w.Error?.Message);
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_writable_grant_writes_and_the_change_persists()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        Assert.Null((await h.RunAsync(sid, "INSERT INTO t(v) VALUES ('kept')")).Done.Error);
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT v FROM t";
        Assert.Equal("kept", cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Every_connection_is_hardened_before_user_sql()
    {
        await using var h = new SqliteHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (_, b, _) = await h.RunAsync(sid, "SELECT (SELECT foreign_keys FROM pragma_foreign_keys) AS fk, (SELECT trusted_schema FROM pragma_trusted_schema) AS ts, (SELECT temp_store FROM pragma_temp_store) AS tmp");
        Assert.Equal(["1"], Texts(b.Single(), "fk"));
        Assert.Equal(["0"], Texts(b.Single(), "ts"));
        Assert.Equal(["2"], Texts(b.Single(), "tmp")); // MEMORY: no temporary files beside the database
        // Foreign keys are enforced.
        await h.RunAsync(sid, "CREATE TABLE p (id INTEGER PRIMARY KEY); CREATE TABLE c (pid INTEGER REFERENCES p(id));");
        var (bad, _, _) = await h.RunAsync(sid, "INSERT INTO c VALUES (42)");
        Assert.Contains("FOREIGN KEY", bad.Error?.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_file_is_never_created()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        File.Delete(path);
        var (done, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Contains("gone", done.Error?.Message ?? "");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_corrupt_file_gives_a_clear_error()
    {
        await using var h = new SqliteHost();
        var path = Path.Combine(h.Dir, "broken.db");
        await File.WriteAllTextAsync(path, "this is not a database, just text that is long enough to be a header and more");
        var (_, sid) = await h.OpenFileAsync(path);
        var (done, _, _) = await h.RunAsync(sid, "SELECT * FROM sqlite_schema");
        Assert.Contains("not a valid SQLite database", done.Error?.Message ?? "");
    }

    [Fact]
    public async Task Cancel_interrupts_a_long_query_and_the_connection_is_reused()
    {
        await using var h = new SqliteHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT count(*) FROM n" });
        await Task.Delay(200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
        var done = await h.Notifier.WaitDone(r.ExecutionId!);
        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.True(sw.ElapsedMilliseconds < TestTime.Budget(500), $"cancel took {sw.ElapsedMilliseconds} ms");
        var (again, b, _) = await h.RunAsync(sid, "SELECT 41 + 1 AS x");
        Assert.Null(again.Error);
        Assert.Equal(["42"], Texts(b.Single(), "x"));
    }

    [Fact]
    public async Task An_interrupted_statement_in_a_transaction_rolls_it_back_and_says_so()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await h.RunAsync(sid, "INSERT INTO t(v) VALUES ('in tx')");
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams
        {
            SessionId = sid, Sql = "INSERT INTO t(v) SELECT i FROM (WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100000000) SELECT i FROM n)",
        });
        await Task.Delay(200);
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
        await h.Notifier.WaitDone(r.ExecutionId!);
        var state = await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid });
        // SQLite rolls the whole transaction back on an interrupted write; the tab is told, never shown a phantom transaction.
        Assert.Equal(TxState.None, state.TxState);
        Assert.Contains(h.Messages(r.ExecutionId!), m => m.Contains("has ended on the server"));
        var (count, b, _) = await h.RunAsync(sid, "SELECT count(*) AS n FROM t");
        Assert.Equal(["0"], Texts(b.Single(), "n"));
    }

    [Fact]
    public async Task Transactions_commit_and_roll_back_on_the_tabs_own_connection()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await h.RunAsync(sid, "INSERT INTO t(v) VALUES ('gone')");
        Assert.Equal(TxState.None, (await h.Rpc.TxRollback(new SessionRef { SessionId = sid })).TxState);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await h.RunAsync(sid, "INSERT INTO t(v) VALUES ('kept')");
        var committed = await h.Rpc.TxCommit(new SessionRef { SessionId = sid });
        Assert.Equal((TxState.None, true), (committed.TxState, committed.Pinned)); // still the tab's connection
        var (_, b, _) = await h.RunAsync(sid, "SELECT v FROM t");
        Assert.Equal(["kept"], Texts(b.Single(), "v"));
        // A script's own BEGIN / COMMIT is followed too.
        await h.RunAsync(sid, "BEGIN; INSERT INTO t(v) VALUES ('script');");
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await h.RunAsync(sid, "COMMIT");
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
    }

    [Fact]
    public async Task A_lock_held_elsewhere_fails_after_the_busy_budget_with_a_clear_message()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        using var other = new SqliteConnection($"Data Source={path};Pooling=False");
        other.Open();
        using (var lockCmd = other.CreateCommand()) { lockCmd.CommandText = "BEGIN IMMEDIATE; INSERT INTO t(v) VALUES ('other');"; lockCmd.ExecuteNonQuery(); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (done, _, _) = await h.RunAsync(sid, "INSERT INTO t(v) VALUES ('mine')");
        Assert.Contains("locked by another connection", done.Error?.Message ?? "");
        Assert.True(sw.Elapsed.TotalSeconds >= 4, "waited the busy budget");
        using (var end = other.CreateCommand()) { end.CommandText = "ROLLBACK"; end.ExecuteNonQuery(); }
        Assert.Null((await h.RunAsync(sid, "INSERT INTO t(v) VALUES ('mine')")).Done.Error);
    }

    [Fact]
    public async Task The_catalog_lists_tables_columns_keys_foreign_keys_and_triggers_and_temp_objects_stay_in_the_session()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: """
            CREATE TABLE customer (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE "order" (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customer(id), total REAL);
            CREATE INDEX ix_order_customer ON "order"(customer_id);
            CREATE VIEW big AS SELECT * FROM "order" WHERE total > 100;
            CREATE TRIGGER tr_order AFTER INSERT ON "order" BEGIN SELECT 1; END;
            """);
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.RunAsync(sid, "CREATE TEMP TABLE scratch (x)");
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = "main", SessionId = sid, Full = true });
        SchemaSnapshot snap;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            await Task.Delay(50);
            snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = "main", SessionId = sid });
        } while (!snap.Objects.Any(o => o.Name == "scratch") && DateTime.UtcNow < deadline);
        var order = snap.Objects.Single(o => o.Name == "order");
        Assert.Equal(ObjectKind.Table, order.Kind);
        Assert.Equal(["id", "customer_id", "total"], order.Columns!.Select(c => c.Name));
        Assert.True(order.Columns!.Single(c => c.Name == "id").Pk);
        Assert.Contains(snap.Fks, f => f.Parent == order.Id && f.Referenced == snap.Objects.Single(o => o.Name == "customer").Id);
        Assert.Equal(order.Id, snap.Objects.Single(o => o.Name == "tr_order").ParentObjectId);
        Assert.Equal(ObjectKind.View, snap.Objects.Single(o => o.Name == "big").Kind);
        Assert.Equal("temp", snap.Objects.Single(o => o.Name == "scratch").Schema);
        // Another tab, and the shared snapshot, never see this session's temporary table.
        var shared = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = "main" });
        Assert.DoesNotContain(shared.Objects, o => o.Name == "scratch");
    }

    [Fact]
    public async Task Explain_query_plan_becomes_a_plan_tree_and_actual_plans_are_refused()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v); CREATE INDEX ix_v ON t(v);");
        var (_, sid) = await h.OpenFileAsync(path);
        var plan = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT * FROM t WHERE v = 1", Mode = PlanMode.Estimated });
        var root = Assert.Single(plan.Tree.Statements).Root!;
        Assert.Contains(root.Children, c => c.Operator.Contains("ix_v"));
        var reparsed = await h.Rpc.PlanParse(new PlanParseParams { Engine = Engine.Sqlite, Raw = plan.Raw! });
        Assert.Equal(root.Children.Length, reparsed.Plan.Statements[0].Root!.Children.Length);
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid });
        Assert.False(caps.ActualPlan.Available);
        Assert.Equal(NamespaceModel.Attached, caps.Namespaces);
        Assert.False(caps.ChangeDatabase.Available);
        Assert.True(caps.DataEdit.Available);
        Assert.True(caps.Import.Available);
        Assert.Equal("SQLite", caps.Product?.Name);
    }

    [Fact]
    public async Task Dependencies_come_from_the_stored_sql_of_views_and_triggers()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: """
            CREATE TABLE item (id INTEGER PRIMARY KEY, item_note TEXT, qty INTEGER);
            CREATE TABLE audit (id INTEGER PRIMARY KEY, what TEXT);
            CREATE VIEW v_item AS SELECT id, qty FROM "Item" WHERE qty > 0;
            CREATE TRIGGER tr_item AFTER INSERT ON item BEGIN INSERT INTO audit (what) VALUES (upper('x')); END;
            CREATE VIEW v_other AS SELECT item_note FROM audit JOIN (SELECT 1 AS item) x;
            """);
        var (_, sid) = await h.OpenFileAsync(path);
        async Task<string[]> Deps(string name, ObjectKind kind, DependencyDirection d) =>
            (await h.Rpc.MetaDependencies(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "main", Name = name, Kind = kind } }))
            .Where(x => x.Direction == d).Select(x => x.Object.Name).Order().ToArray();
        Assert.Equal(["item"], await Deps("v_item", ObjectKind.View, DependencyDirection.Uses)); // "Item": names compare case-insensitively
        Assert.Equal(["audit", "item"], await Deps("tr_item", ObjectKind.Trigger, DependencyDirection.Uses));
        Assert.Equal(["tr_item", "v_item"], await Deps("item", ObjectKind.Table, DependencyDirection.UsedBy)); // not v_other (a column and an alias named item…)
        Assert.Equal(["audit"], await Deps("v_other", ObjectKind.View, DependencyDirection.Uses));
    }

    [Fact]
    public async Task Row_counts_come_from_analyze_and_modify_apply_runs_again()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: """
            CREATE TABLE item (id INTEGER PRIMARY KEY, qty INTEGER); INSERT INTO item (qty) VALUES (1), (2), (3);
            CREATE VIEW v_item AS SELECT id FROM item;
            """);
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = "main", SessionId = sid, Full = true });
        SchemaSnapshot snap;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do { await Task.Delay(50); snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = "main", SessionId = sid }); }
        while (!snap.Objects.Any(o => o.Name == "v_item") && DateTime.UtcNow < deadline);
        var item = snap.Objects.Single(o => o.Name == "item");
        // Before ANALYZE SQLite has no statistics: unknown, never a made-up number.
        var before = Assert.Single(await h.Rpc.MetaRowCounts(new RowCountsParams { SessionId = sid, ObjectIds = [item.Id] }));
        Assert.Null(before.Rows);
        await h.RunAsync(sid, "ANALYZE");
        var after = Assert.Single(await h.Rpc.MetaRowCounts(new RowCountsParams { SessionId = sid, ObjectIds = [item.Id] }));
        Assert.Equal((3L, true), (after.Rows, after.Estimated));

        // Script and Modify → Apply: the editable view definition runs as it is (DROP … IF EXISTS + CREATE).
        var view = new ObjectRef { Schema = "main", Name = "v_item", Kind = ObjectKind.View };
        Assert.Contains("SELECT *", (await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = new ObjectRef { Schema = "main", Name = "item", Kind = ObjectKind.Table }, Kind = ScriptKind.SelectTop })).Sql);
        var editable = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = view });
        Assert.Contains("DROP VIEW IF EXISTS", editable.Sql);
        Assert.Null((await h.RunAsync(sid, editable.Sql.Replace("SELECT id", "SELECT id, qty"))).Done.Error);
        var (_, batches, _) = await h.RunAsync(sid, "SELECT * FROM v_item");
        Assert.Contains(batches[0].Schema.FieldsList, f => f.Name == "qty"); // the edited view now has qty
    }

    [Fact]
    public async Task Features_SQLite_lacks_say_why_and_the_sidecar_refuses_them()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v);");
        var (_, sid) = await h.OpenFileAsync(path);
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid });
        foreach (var (name, info) in new[] { ("schema compare", caps.SchemaCompare), ("data compare", caps.DataCompare), ("references", caps.References), ("monitor", caps.Monitor) })
        {
            Assert.False(info.Available, name);
            Assert.Contains("SQLite", info.Reason);
        }
        // The UI hides these, but the sidecar is what refuses: a crafted call gets the same reason.
        var side = new DataCompareSide { SessionId = sid, Table = "t" };
        var dc = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.DataCompareStart(new DataCompareParams { Source = side, Target = side }));
        Assert.Equal(("unsupported", caps.DataCompare.Reason), (dc.Code, dc.Message));
        var sc = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SchemaCompareRun(new SchemaCompareParams { Source = new CompareSide { SessionId = sid }, Target = new CompareSide { SessionId = sid } }));
        Assert.Equal(("unsupported", caps.SchemaCompare.Reason), (sc.Code, sc.Message));
    }

    [Theory]
    [InlineData("SELECT :a AS x", ":a")]
    [InlineData("SELECT @a AS x", "@a")]
    [InlineData("SELECT $a AS x", "$a")]
    [InlineData("SELECT ?1 AS x", "?1")]
    [InlineData("SELECT ? AS x", "?1")] // the run dialog names a bare ? by its index
    public async Task Run_dialog_parameters_bind_in_every_sqlite_form(string sql, string name)
    {
        await using var h = new SqliteHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (done, b, _) = await h.RunAsync(sid, sql, null, [new QueryParameter { Name = name, Value = "bound" }]);
        Assert.Null(done.Error);
        Assert.Equal(["bound"], Texts(b.Single(), "x"));
    }

    [Fact]
    public async Task Prod_guards_apply_to_file_profiles()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite, EnvironmentKind.Prod);
        var first = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Equal(GuardReason.ProdFirstRun, first.Blocked?.Reason);
        var drop = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t", Options = new ExecuteOptions { ConfirmedProd = true } });
        Assert.Equal(GuardReason.Destructive, drop.Blocked?.Reason);
        Assert.Equal("deleteWithoutWhere", drop.Blocked!.Findings.Single().Rule);
    }

    [Fact]
    public async Task Every_built_in_function_signature_help_offers_exists_in_the_bundled_library()
    {
        await using var h = new SqliteHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (done, batches, _) = await h.RunAsync(sid, "SELECT name FROM pragma_function_list UNION SELECT name FROM pragma_module_list");
        Assert.Null(done.Error);
        var names = batches.SelectMany(b => Texts(b, "name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = Querithm.Intelligence.Sqlite.SqliteCompletion.Builtins.Select(f => f.Name).Where(n => !names.Contains(n)).ToList();
        // The table-valued JSON functions are built in but in neither list of this build: they must run instead.
        Assert.Equal(["json_each", "json_tree"], missing);
        foreach (var n in missing) Assert.Null((await h.RunAsync(sid, $"SELECT count(*) AS n FROM {n}('[1, 2]')")).Done.Error);
    }

    [Fact]
    public async Task Tier_two_completion_and_signature_help_run_over_rpc_on_the_sessions_own_snapshot()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: "CREATE TABLE \"Order Items\" (id INTEGER PRIMARY KEY, qty INTEGER, \"group\" TEXT);");
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = "main", SessionId = sid, Full = true });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        SchemaSnapshot snap;
        do { await Task.Delay(50); snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = "main", SessionId = sid }); }
        while (!snap.Objects.Any(o => o.Name == "Order Items") && DateTime.UtcNow < deadline);

        const string text = "SELECT oi. FROM [order items] oi";
        var items = await h.Rpc.LangComplete(new CompleteParams { DocId = "d", Text = text, Offset = 10, Dialect = Dialect.Sqlite, ProfileId = p.Id, Database = "main", SessionId = sid });
        Assert.Equal(["group", "id", "qty"], items.Select(i => i.Label).Order(StringComparer.Ordinal));
        Assert.Equal("\"group\"", items.Single(i => i.Label == "group").InsertText);
        const string call = "SELECT round(qty, ";
        var help = await h.Rpc.LangSignature(new SignatureParams { DocId = "d", Text = call, Offset = call.Length, Dialect = Dialect.Sqlite, ProfileId = p.Id, Database = "main", SessionId = sid });
        Assert.Equal("round(X, [Y])", Assert.Single(help.Signatures).Label);
        Assert.Equal(1, help.ActiveParameter);
    }

    [Fact]
    public async Task Closing_a_tab_while_a_helper_reads_on_its_connection_ends_the_read_first_and_the_file_is_free_again()
    {
        // session.close used to close the tab's connection under a helper's open reader, whose read lock then outlived the tab.
        await using var h = new SqliteHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path);
        Assert.Null((await h.RunAsync(sid, "SELECT 1")).Done.Error);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = Task.Run(async () =>
        {
            await using var lease = await h.Services.Sessions.LeaseAsync(h.Services.Sessions.Get(sid), CancellationToken.None);
            await using var cmd = lease.Conn.Db.CreateCommand();
            cmd.CommandText = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 1000) SELECT x FROM n";
            await using var r = await cmd.ExecuteReaderAsync(lease.Token);
            Assert.True(await r.ReadAsync(lease.Token));
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, lease.Token); // a slow consumer: the reader stays open until the close
        });
        await reading.Task.WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));

        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid }).WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => helper.WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10))));
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var write = c.CreateCommand();
        write.CommandText = "INSERT INTO t(v) VALUES ('after the tab')";
        write.CommandTimeout = 2; // a reader's lock left behind would make this busy
        Assert.Equal(1, write.ExecuteNonQuery());
    }
}
