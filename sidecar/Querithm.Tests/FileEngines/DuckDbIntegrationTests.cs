using System.Text;
using Apache.Arrow;
using DuckDB.NET.Data;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Providers.DuckDb;

namespace Querithm.Tests.FileEngines;

/// <summary>Phase D3: DuckDB end to end through the RPC surface, on real temp-dir files and in-memory databases (no Docker).</summary>
public class DuckDbIntegrationTests
{
    static string?[] Texts(RecordBatch b, string col) => Enumerable.Range(0, b.Length).Select(i => b.Column(col) switch
    {
        StringArray s => s.GetString(i),
        Int32Array n => n.GetValue(i)?.ToString(),
        Int64Array n => n.GetValue(i)?.ToString(),
        var a => a.IsNull(i) ? null : a.ToString(),
    }).ToArray();

    static GridValue V(string? text) => text == null ? new GridValue { IsNull = true } : new GridValue { Text = text };

    /// <summary>Reads a file as another connection of this process would: the same path shares Querithm's instance while it is open.</summary>
    static object? Scalar(string path, string sql)
    {
        using var c = new DuckDBConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    static async Task<SchemaSnapshot> RefreshAsync(DuckDbHost h, Profile p, string sid, string database, Func<SchemaSnapshot, bool> ready)
    {
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = database, SessionId = sid, Full = true });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        SchemaSnapshot snap;
        do
        {
            await Task.Delay(50);
            snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = database, SessionId = sid });
        } while (!ready(snap) && DateTime.UtcNow < deadline);
        return snap;
    }

    [Fact]
    public async Task An_in_memory_database_runs_ddl_dml_and_typed_queries()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (done, _, _) = await h.RunAsync(sid, "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR); INSERT INTO t VALUES (1, 'a'), (2, NULL);");
        Assert.Null(done.Error);
        var (q, batches, _) = await h.RunAsync(sid, "SELECT id, v FROM t ORDER BY id");
        Assert.Null(q.Error);
        var b = Assert.Single(batches);
        Assert.IsType<Int32Array>(b.Column("id"));
        Assert.Equal(["1", "2"], Texts(b, "id"));
        Assert.Equal(["a", null], Texts(b, "v"));
    }

    [Fact]
    public async Task Duckdb_types_arrive_typed_or_as_exact_text()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (done, batches, _) = await h.RunAsync(sid, """
            SELECT 12345678901234567890.123456789012345678::DECIMAL(38,18) AS d38, 1.5::DECIMAL(10,2) AS d,
                   170141183460469231731687303715884105727::HUGEINT AS h, INTERVAL '1 year 2 months 3 days 04:05:06.789' AS iv,
                   TIMESTAMPTZ '2026-10-31 10:20:30.123456+06' AS tz, TIMESTAMP '2026-10-31 10:20:30' AS ts, DATE '2026-10-31' AS dt,
                   TIMETZ '10:20:30.5+06:30' AS ttz, [1, NULL, 3] AS l, [[1, NULL], [3]] AS ll, {'a': 1, 'b': 'x'} AS st, MAP {'k': 1} AS mp,
                   '\xAA\xBB'::BLOB AS bl, '6f9619ff-8b86-d011-b42d-00c04fc964ff'::UUID AS u, 'x'::ENUM('x', 'y') AS en, true AS b, NULL::INT AS n
            """);
        Assert.Null(done.Error);
        var b = Assert.Single(batches);
        string? T(string c) => Texts(b, c)[0];
        Assert.Equal("12345678901234567890.123456789012345678", T("d38"));
        Assert.Equal("1.50", T("d"));
        Assert.Equal("170141183460469231731687303715884105727", T("h"));
        Assert.Equal("1 year 2 months 3 days 04:05:06.789", T("iv"));
        Assert.Equal("10:20:30.5+06:30", T("ttz"));
        Assert.Equal("[1, NULL, 3]", T("l"));
        Assert.Equal("[[1, NULL], [3]]", T("ll"));
        Assert.Equal("{'a': 1, 'b': 'x'}", T("st"));
        Assert.Equal("{'k'=1}", T("mp"));
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", T("u"));
        Assert.Equal("x", T("en"));
        var tz = Assert.IsType<TimestampArray>(b.Column("tz"));
        Assert.Equal("UTC", ((Apache.Arrow.Types.TimestampType)tz.Data.DataType).Timezone);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 4, 20, 30, TimeSpan.Zero).AddTicks(1234560), tz.GetTimestamp(0));
        Assert.IsType<TimestampArray>(b.Column("ts"));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, Assert.IsType<BinaryArray>(b.Column("bl")).GetBytes(0).ToArray());
        Assert.True(Assert.IsType<BooleanArray>(b.Column("b")).GetValue(0));
        Assert.True(b.Column("n").IsNull(0));
    }

    [Fact]
    public async Task Values_the_driver_cannot_read_fail_with_a_reason_and_the_session_continues()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (u, _, _) = await h.RunAsync(sid, "SELECT union_value(n := 1)::UNION(n INT, s VARCHAR) AS x");
        Assert.Contains("Cast it to VARCHAR", u.Error?.Message);
        var (inf, _, _) = await h.RunAsync(sid, "SELECT 'infinity'::TIMESTAMP AS x");
        Assert.Contains("infinite", inf.Error?.Message);
        var (ok, b, _) = await h.RunAsync(sid, "SELECT union_value(n := 1)::UNION(n INT, s VARCHAR)::VARCHAR AS x, 'infinity'::TIMESTAMP::VARCHAR AS y");
        Assert.Null(ok.Error);
        Assert.Equal(["1"], Texts(b.Single(), "x"));
        Assert.Equal(["infinity"], Texts(b.Single(), "y"));
    }

    [Fact]
    public async Task A_read_only_file_reads_and_refuses_writes_without_changing_the_file()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR); INSERT INTO t VALUES (1, 'a');");
        var before = await File.ReadAllBytesAsync(path);
        var (_, sid) = await h.OpenFileAsync(path);
        var (q, batches, _) = await h.RunAsync(sid, "SELECT v FROM t");
        Assert.Null(q.Error);
        Assert.Equal(["a"], Texts(batches.Single(), "v"));
        var (w, _, _) = await h.RunAsync(sid, "INSERT INTO t VALUES (2, 'b')");
        Assert.Contains("read-only", w.Error?.Message);
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_writable_grant_writes_and_the_change_persists()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        Assert.Null((await h.RunAsync(sid, "INSERT INTO t VALUES (1, 'kept')")).Done.Error);
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        Assert.DoesNotContain(DuckDbInstances.Open(), o => o.DataSource == path);
        Assert.Equal("kept", Scalar(path, "SELECT v FROM t"));
    }

    [Fact]
    public async Task An_empty_file_with_writes_allowed_becomes_a_new_database_in_the_same_file()
    {
        await using var h = new DuckDbHost();
        var path = Path.Combine(h.Dir, "new.duckdb");
        await File.WriteAllBytesAsync(path, []);
        var identity = Core.Sessions.FileIdentity.Of(path);
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        var (done, _, _) = await h.RunAsync(sid, "CREATE TABLE t (a INTEGER); INSERT INTO t VALUES (7);");
        Assert.Null(done.Error);
        Assert.Equal(identity, Core.Sessions.FileIdentity.Of(path));
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        Assert.Equal(7, Convert.ToInt32(Scalar(path, "SELECT a FROM t")));
        Assert.Empty(Directory.GetFiles(DuckDbInstances.TempDirectory(h.AppData), "new-*"));
    }

    [Fact]
    public async Task An_empty_read_only_file_says_writes_are_needed_and_stays_empty()
    {
        await using var h = new DuckDbHost();
        var path = Path.Combine(h.Dir, "empty.duckdb");
        await File.WriteAllBytesAsync(path, []);
        var (_, sid) = await h.OpenFileAsync(path);
        var (done, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Contains("Allow writes", done.Error?.Message ?? "");
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public async Task A_corrupt_file_gives_a_clear_error()
    {
        await using var h = new DuckDbHost();
        var path = Path.Combine(h.Dir, "broken.duckdb");
        await File.WriteAllTextAsync(path, new string('x', 8192));
        var (_, sid) = await h.OpenFileAsync(path);
        var (done, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Contains("not a valid DuckDB database", done.Error?.Message ?? "");
    }

    [Fact]
    public async Task A_file_locked_by_another_process_gives_a_clear_error()
    {
        if (OperatingSystem.IsWindows()) return; // the fcntl lock below is POSIX
        await using var h = new DuckDbHost();
        var path = h.NewDatabase();
        // Another program holding DuckDB's write lock (an exclusive fcntl lock on the file).
        using var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3",
            ["-c", "import fcntl,sys,time; f=open(sys.argv[1],'r+b'); fcntl.lockf(f, fcntl.LOCK_EX); print('locked', flush=True); time.sleep(30)", path])
        {
            RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        try
        {
            Assert.Equal("locked", await holder.StandardOutput.ReadLineAsync());
            var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
            var (done, _, _) = await h.RunAsync(sid, "SELECT 1");
            Assert.Contains("open in another program", done.Error?.Message ?? "");
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public async Task Cancel_interrupts_a_long_query_and_the_connection_is_reused()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT count(*) FROM range(100000000000) a" });
        await Task.Delay(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
        var done = await h.Notifier.WaitDone(r.ExecutionId!);
        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"cancel took {sw.ElapsedMilliseconds} ms");
        var (again, b, _) = await h.RunAsync(sid, "SELECT 41 + 1 AS x");
        Assert.Null(again.Error);
        Assert.Equal(["42"], Texts(b.Single(), "x"));
    }

    [Fact]
    public async Task Cancel_during_a_file_read_and_a_cancelled_write_leave_the_file_whole()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE big AS SELECT range AS k, md5(range::VARCHAR) AS v FROM range(3000000); CREATE TABLE t (k BIGINT);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        var read = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT count(*) FROM big a, big b WHERE a.v < b.v" });
        await Task.Delay(300);
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = read.ExecutionId! });
        Assert.Equal(ExecState.Cancelled, (await h.Notifier.WaitDone(read.ExecutionId!)).State);
        var write = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "INSERT INTO t SELECT a.k FROM big a, big b WHERE a.v < b.v" });
        await Task.Delay(300);
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = write.ExecutionId! });
        Assert.Equal(ExecState.Cancelled, (await h.Notifier.WaitDone(write.ExecutionId!)).State);
        var (n, b, _) = await h.RunAsync(sid, "SELECT count(*) AS n FROM t");
        Assert.Null(n.Error);
        Assert.Equal(["0"], Texts(b.Single(), "n"));
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        Assert.Equal(3000000L, Convert.ToInt64(Scalar(path, "SELECT count(*) FROM big")));
    }

    [Fact]
    public async Task Transactions_commit_roll_back_and_a_failed_statement_needs_a_rollback()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await h.RunAsync(sid, "INSERT INTO t VALUES (1, 'gone')");
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(TxState.None, (await h.Rpc.TxRollback(new SessionRef { SessionId = sid })).TxState);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await h.RunAsync(sid, "INSERT INTO t VALUES (2, 'kept')");
        Assert.Equal(TxState.None, (await h.Rpc.TxCommit(new SessionRef { SessionId = sid })).TxState);
        var (_, b, _) = await h.RunAsync(sid, "SELECT v FROM t");
        Assert.Equal(["kept"], Texts(b.Single(), "v"));
        // A script's own BEGIN is followed; an error inside it leaves the transaction open (aborted) until ROLLBACK.
        await h.RunAsync(sid, "BEGIN TRANSACTION; INSERT INTO t VALUES (3, 'x');");
        var (dup, _, _) = await h.RunAsync(sid, "INSERT INTO t VALUES (2, 'duplicate key')");
        Assert.NotNull(dup.Error);
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await h.RunAsync(sid, "ROLLBACK");
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        var savepoint = await Assert.ThrowsAsync<NotSupportedException>(() => new DuckDbTransactions().SavepointAsync(null!, "a", default));
        Assert.Contains("no savepoints", savepoint.Message);
    }

    [Fact]
    public async Task The_catalog_lists_tables_keys_indexes_views_macros_and_sequences_and_temp_objects_stay_in_the_session()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: """
            CREATE SEQUENCE seq_customer;
            CREATE TABLE customer (id INTEGER PRIMARY KEY DEFAULT nextval('seq_customer'), name VARCHAR NOT NULL UNIQUE, credit DECIMAL(10,2));
            CREATE TABLE "order" (id BIGINT PRIMARY KEY, customer_id INTEGER REFERENCES customer(id), total DOUBLE);
            CREATE INDEX ix_order_customer ON "order"(customer_id);
            CREATE VIEW big AS SELECT * FROM "order" WHERE total > 100;
            CREATE MACRO twice(x) AS x * 2;
            CREATE MACRO top_orders(n) AS TABLE SELECT * FROM "order" ORDER BY total DESC LIMIT n;
            CREATE SCHEMA crm; CREATE TABLE crm.region (code VARCHAR PRIMARY KEY);
            """);
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.RunAsync(sid, "CREATE TEMP TABLE scratch (x INT)");
        const string db = "sales"; // the catalog DuckDB names after the file
        var snap = await RefreshAsync(h, p, sid, db, s => s.Objects.Any(o => o.Name == "scratch"));
        var order = snap.Objects.Single(o => o.Name == "order");
        Assert.Equal(["id", "customer_id", "total"], order.Columns!.Select(c => c.Name));
        Assert.True(order.Columns!.Single(c => c.Name == "id").Pk);
        var customer = snap.Objects.Single(o => o.Name == "customer");
        Assert.Equal("DECIMAL(10,2)", customer.Columns!.Single(c => c.Name == "credit").Type);
        Assert.Equal("nextval('seq_customer')", customer.Columns!.Single(c => c.Name == "id").Default);
        Assert.Contains(snap.Fks, f => f.Parent == order.Id && f.Referenced == customer.Id);
        Assert.Equal(ObjectKind.View, snap.Objects.Single(o => o.Name == "big").Kind);
        Assert.Equal(ObjectKind.Func, snap.Objects.Single(o => o.Name == "twice").Kind);
        Assert.Equal(ObjectKind.Tvf, snap.Objects.Single(o => o.Name == "top_orders").Kind);
        Assert.Equal(ObjectKind.Sequence, snap.Objects.Single(o => o.Name == "seq_customer").Kind);
        Assert.Equal("crm", snap.Objects.Single(o => o.Name == "region").Schema);
        Assert.Equal("temp", snap.Objects.Single(o => o.Name == "scratch").Schema);
        var shared = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = db });
        Assert.DoesNotContain(shared.Objects, o => o.Name == "scratch");
    }

    static async Task<System.Data.Common.DbConnection> OpenRaw(DuckDbHost h, string path) =>
        await new DuckDbProvider(h.AppData).OpenAsync(new Profile { Id = "x", Name = "x", Engine = Engine.DuckDb, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "g9" } },
            null, null, DuckDbHost.Grant(path, FileAccessMode.ReadWrite, "g9"), default);

    [Fact]
    public async Task Dependencies_row_counts_scripts_types_and_modify_apply_of_a_view_and_a_macro()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase("deps.duckdb", """
            CREATE TABLE item (id INTEGER PRIMARY KEY, qty INTEGER); INSERT INTO item VALUES (1, 1), (2, 2), (3, 3);
            CREATE TABLE audit (id INTEGER, what VARCHAR);
            CREATE VIEW v_item AS SELECT id, qty FROM "ITEM" WHERE qty > 0;
            CREATE MACRO item_total() AS (SELECT sum(qty) FROM item);
            CREATE VIEW v_other AS SELECT what AS item FROM audit;
            CREATE TYPE mood AS ENUM ('happy', 'it''s');
            CREATE TYPE pt AS STRUCT(x INTEGER, y INTEGER);
            """);
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        async Task<string[]> Deps(string name, ObjectKind kind, DependencyDirection d) =>
            (await h.Rpc.MetaDependencies(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "main", Name = name, Kind = kind } }))
            .Where(x => x.Direction == d).Select(x => x.Object.Name).Order().ToArray();
        Assert.Equal(["item"], await Deps("v_item", ObjectKind.View, DependencyDirection.Uses)); // "ITEM": names compare case-insensitively
        Assert.Equal(["item"], await Deps("item_total", ObjectKind.Func, DependencyDirection.Uses));
        Assert.Equal(["item_total", "v_item"], await Deps("item", ObjectKind.Table, DependencyDirection.UsedBy)); // not v_other's alias
        Assert.Equal(["audit"], await Deps("v_other", ObjectKind.View, DependencyDirection.Uses));

        var snap = await RefreshAsync(h, p, sid, "deps", s => s.Objects.Any(o => o.Name == "pt"));
        var item = snap.Objects.Single(o => o.Name == "item");
        var counts = Assert.Single(await h.Rpc.MetaRowCounts(new RowCountsParams { SessionId = sid, ObjectIds = [item.Id] }));
        Assert.Equal((3L, true), (counts.Rows, counts.Estimated));
        Assert.Equal(ObjectKind.Type, snap.Objects.Single(o => o.Name == "mood").Kind);
        var mood = new ObjectRef { Schema = "main", Name = "mood", Kind = ObjectKind.Type };
        Assert.Equal("CREATE TYPE \"main\".\"mood\" AS ENUM ('happy', 'it''s');", (await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = mood, Kind = ScriptKind.Create })).Sql);
        Assert.Equal("CREATE TYPE \"main\".\"pt\" AS STRUCT(x INTEGER, y INTEGER);",
            (await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = new ObjectRef { Schema = "main", Name = "pt", Kind = ObjectKind.Type }, Kind = ScriptKind.Create })).Sql);
        Assert.StartsWith("DROP TYPE", (await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = mood, Kind = ScriptKind.DropCreate })).Sql);

        // Modify → Apply: the editable view and macro run as they are (CREATE OR REPLACE).
        var view = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "main", Name = "v_item", Kind = ObjectKind.View } });
        Assert.StartsWith("CREATE OR REPLACE VIEW", view.Sql);
        Assert.Null((await h.RunAsync(sid, view.Sql.Replace("SELECT id, qty", "SELECT id, qty, qty * 2 AS twice"))).Done.Error);
        var (_, batches, _) = await h.RunAsync(sid, "SELECT * FROM v_item");
        Assert.Contains(batches[0].Schema.FieldsList, f => f.Name == "twice");
        var macro = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "main", Name = "item_total", Kind = ObjectKind.Func } });
        Assert.StartsWith("CREATE OR REPLACE MACRO", macro.Sql);
        Assert.Null((await h.RunAsync(sid, macro.Sql.Replace("sum(qty)", "sum(qty) * 10"))).Done.Error);
        var (_, total, _) = await h.RunAsync(sid, "SELECT item_total()::VARCHAR AS t");
        Assert.Equal(["60"], Texts(total.Single(), "t"));
    }

    [Fact]
    public async Task Sequence_markers_ignore_values_drawn_and_table_markers_follow_their_indexes()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE SEQUENCE s; CREATE TABLE t (id INTEGER DEFAULT nextval('s'), v VARCHAR);");
        await using var c = await OpenRaw(h, path);
        var m = new DuckDbMetadata();
        var before = await m.GetStampsAsync(c, default);
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "INSERT INTO t (v) VALUES ('a'), ('b')"; await cmd.ExecuteNonQueryAsync(); }
        var drawn = await m.GetStampsAsync(c, default);
        Assert.Equal(before.Single(s => s.Name == "s").Marker, drawn.Single(s => s.Name == "s").Marker);
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "CREATE INDEX ix ON t(v)"; await cmd.ExecuteNonQueryAsync(); }
        var indexed = await m.GetStampsAsync(c, default);
        Assert.NotEqual(drawn.Single(s => s.Name == "t").Marker, indexed.Single(s => s.Name == "t").Marker);
    }

    [Fact]
    public async Task Estimated_and_actual_plans_become_plan_trees_and_parse_back()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR); INSERT INTO t SELECT range, 'v' || range FROM range(1000);");
        var (_, sid) = await h.OpenFileAsync(path);
        var plan = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT v, count(*) FROM t WHERE id > 10 GROUP BY v", Mode = PlanMode.Estimated });
        var root = Assert.Single(plan.Tree.Statements).Root!;
        static IEnumerable<PlanNode> All(PlanNode n) => n.Children.SelectMany(All).Prepend(n);
        Assert.Contains(All(root), n => n.ObjectName?.EndsWith(".t") == true);
        var reparsed = await h.Rpc.PlanParse(new PlanParseParams { Engine = Engine.DuckDb, Raw = plan.Raw! });
        Assert.Equal(All(root).Count(), All(reparsed.Plan.Statements[0].Root!).Count());
        var actual = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT v FROM t WHERE id < 990", Mode = PlanMode.Actual, ConfirmedRun = true });
        var scan = All(actual.Tree.Statements.Single().Root!).First(n => n.ObjectName?.EndsWith(".t") == true);
        Assert.Equal(990, scan.ActualRows);
        // DuckDB 1.5.6 prints no JSON profile for count(*) from a file table's statistics: a reason, not an empty plan.
        var none = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT count(*) FROM t", Mode = PlanMode.Actual, ConfirmedRun = true });
        Assert.Equal(ExecState.Failed, none.Done.State);
        Assert.Contains("estimated plan is available", Assert.Single(none.Errors));
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid });
        Assert.True(caps.ActualPlan.Available);
        Assert.Equal(NamespaceModel.Schemas, caps.Namespaces);
        Assert.False(caps.ChangeDatabase.Available);
        Assert.True(caps.DataEdit.Available);
        Assert.True(caps.Import.Available);
        Assert.Equal("DuckDB", caps.Product?.Name);
    }

    [Fact]
    public async Task Features_DuckDB_lacks_say_why_and_the_sidecar_refuses_them()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenFileAsync(h.NewDatabase());
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid });
        foreach (var (name, info) in new[] { ("schema compare", caps.SchemaCompare), ("data compare", caps.DataCompare), ("references", caps.References), ("monitor", caps.Monitor), ("trace", caps.Trace), ("debugger", caps.Debugger) })
        {
            Assert.False(info.Available, name);
            Assert.Contains("DuckDB", info.Reason);
        }
        var side = new DataCompareSide { SessionId = sid, Table = "t" };
        var dc = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.DataCompareStart(new DataCompareParams { Source = side, Target = side }));
        Assert.Equal(("unsupported", caps.DataCompare.Reason), (dc.Code, dc.Message));
    }

    [Theory]
    [InlineData("SELECT $a AS x", "$a")]
    [InlineData("SELECT $1 AS x", "$1")]
    [InlineData("SELECT ? AS x", "?")]
    [InlineData("SELECT ? AS x", "?1")] // the run dialog names a bare ? by its index
    public async Task Run_dialog_parameters_bind_in_every_duckdb_form(string sql, string name)
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (done, b, _) = await h.RunAsync(sid, sql, null, [new QueryParameter { Name = name, Value = "bound" }]);
        Assert.Null(done.Error);
        Assert.Equal(["bound"], Texts(b.Single(), "x"));
    }

    [Fact]
    public async Task A_text_parameter_is_cast_to_the_type_the_statement_expects()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenMemoryAsync();
        await h.RunAsync(sid, "CREATE TABLE t (id INTEGER, d DATE); INSERT INTO t VALUES (5, DATE '2026-10-31');");
        var (done, b, _) = await h.RunAsync(sid, "SELECT count(*) AS n FROM t WHERE id = $id AND d = $d", null,
            [new QueryParameter { Name = "$id", Value = "5" }, new QueryParameter { Name = "$d", Value = "2026-10-31" }]);
        Assert.Null(done.Error);
        Assert.Equal(["1"], Texts(b.Single(), "n"));
    }

    [Fact]
    public async Task Prod_guards_apply_to_file_profiles()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenFileAsync(h.NewDatabase(), FileAccessMode.ReadWrite, EnvironmentKind.Prod);
        var first = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Equal(GuardReason.ProdFirstRun, first.Blocked?.Reason);
        var del = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t", Options = new ExecuteOptions { ConfirmedProd = true } });
        Assert.Equal(GuardReason.Destructive, del.Blocked?.Reason);
    }

    const string Orders = """
        CREATE TABLE orders (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, qty INTEGER, price DECIMAL(10,2), score DOUBLE, tags VARCHAR[], raw BLOB);
        INSERT INTO orders VALUES (1, 'one', 1, 0.10, 0.5, ['a'], '\x00'::BLOB), (2, 'two', 2, 2.50, NULL, NULL, NULL), (3, 'three', 3, NULL, NULL, NULL, NULL);
        CREATE TABLE nokey (a INTEGER, b VARCHAR);
        """;

    [Fact]
    public async Task Grid_edits_write_typed_values_check_conflicts_and_tables_without_a_key_stay_read_only()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase("shop.duckdb", Orders);
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await RefreshAsync(h, p, sid, "shop", s => s.Objects.Any(o => o.Name == "orders"));
        var (done, _, exec) = await h.RunAsync(sid, "SELECT id, name, qty, price, score, tags, raw FROM orders ORDER BY id");
        Assert.Null(done.Error);
        var edit = h.Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == exec).Edit!;
        Assert.True(edit.Editable, edit.Reason);
        Assert.Equal(["id"], edit.KeyColumns);
        Assert.False(edit.Columns.Single(c => c.Column == "price").Locked);
        Assert.True(edit.Columns.Single(c => c.Column == "tags").Locked);

        var r = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates = [new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["qty"] = V("7"), ["price"] = V("0.30") }, Original = new() { ["qty"] = V("1"), ["price"] = V("0.10") } }],
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["id"] = V("4"), ["name"] = V("four"), ["qty"] = V("4") } }],
            Deletes = [new GridRowDelete { RowIndex = 1, Key = [V("2")], Original = new() { ["name"] = V("two"), ["qty"] = V("2") } }],
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal((1, 1, 1), (r.AppliedUpdates, r.AppliedInserts, r.AppliedDeletes));
        Assert.Equal("4", Assert.Single(r.InsertedKeys!["0"]).Text);
        Assert.Equal(7, Convert.ToInt32(Scalar(path, "SELECT qty FROM orders WHERE id = 1")));
        Assert.Equal(0.30m, Convert.ToDecimal(Scalar(path, "SELECT price FROM orders WHERE id = 1")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(path, "SELECT count(*) FROM orders WHERE id = 2")));

        // Someone else changes row 3 in between: a conflict, and nothing of the patch is written.
        Scalar(path, "UPDATE orders SET name = 'changed' WHERE id = 3");
        var conflict = await h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates =
            [
                new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["name"] = V("uno") }, Original = new() { ["name"] = V("one") } },
                new GridRowUpdate { RowIndex = 2, Key = [V("3")], Changes = new() { ["name"] = V("tres") }, Original = new() { ["name"] = V("three") } },
            ],
        });
        Assert.False(conflict.Ok);
        Assert.Contains(conflict.Conflicts ?? [], c => c.RowIndex == 2);
        Assert.Equal("one", Scalar(path, "SELECT name FROM orders WHERE id = 1"));

        var (_, _, nk) = await h.RunAsync(sid, "SELECT a, b FROM nokey");
        var nkEdit = h.Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == nk).Edit!;
        Assert.False(nkEdit.Editable);
        Assert.False(string.IsNullOrEmpty(nkEdit.Reason));
    }

    [Fact]
    public async Task A_self_join_and_a_union_are_read_only_and_an_expression_unique_index_is_not_a_key()
    {
        // Grid-edit audit: shapes whose rows a key cannot name exactly, and a unique index on lower(code) that proves nothing.
        await using var h = new DuckDbHost();
        var path = h.NewDatabase("audit.duckdb", """
            CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER); INSERT INTO t VALUES (1, 10), (2, 20);
            CREATE TABLE u (id INTEGER PRIMARY KEY, v INTEGER); INSERT INTO u VALUES (3, 30);
            CREATE TABLE codes (code VARCHAR NOT NULL, label VARCHAR); CREATE UNIQUE INDEX ux_lower_code ON codes (lower(code)); INSERT INTO codes VALUES ('a', 'x');
            """);
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await RefreshAsync(h, p, sid, "audit", s => s.Objects.Any(o => o.Name == "codes"));
        ResultEditInfo EditOf(string exec) => h.Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == exec).Edit!;

        var (_, _, plain) = await h.RunAsync(sid, "SELECT id, v FROM t");
        Assert.True(EditOf(plain).Editable, EditOf(plain).Reason);
        var (_, _, self) = await h.RunAsync(sid, "SELECT a.id, a.v FROM t a JOIN t b ON b.id = a.id + 1");
        Assert.False(EditOf(self).Editable);
        Assert.Equal("Read-only: the same table (t) appears more than once in the query", EditOf(self).Reason);
        var (_, _, union) = await h.RunAsync(sid, "SELECT id, v FROM t UNION ALL SELECT id, v FROM u");
        Assert.False(EditOf(union).Editable);
        Assert.Equal("Read-only: the query combines several queries (UNION)", EditOf(union).Reason);
        var (_, _, codes) = await h.RunAsync(sid, "SELECT code, label FROM codes");
        Assert.False(EditOf(codes).Editable);
        Assert.Contains("no primary key", EditOf(codes).Reason);
    }

    [Fact]
    public async Task Inside_an_open_transaction_grid_edits_and_imports_are_refused_up_front_because_duckdb_has_no_savepoints()
    {
        // L15: undoing a failed grid patch or import inside the user's transaction needs a savepoint, which DuckDB lacks.
        // Both refuse before anything runs, say why and what to do, and leave the user's transaction as it was.
        await using var h = new DuckDbHost();
        var path = h.NewDatabase("sp.duckdb", Orders);
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await RefreshAsync(h, p, sid, "sp", s => s.Objects.Any(o => o.Name == "orders"));
        var (_, _, exec) = await h.RunAsync(sid, "SELECT id, name, qty FROM orders ORDER BY id");
        var csv = Path.Combine(h.Dir, "more.csv");
        await File.WriteAllTextAsync(csv, "id,name,qty\n10,ten,10\n", new UTF8Encoding(false));
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        Assert.Null((await h.RunAsync(sid, "UPDATE orders SET qty = 9 WHERE id = 3")).Done.Error);

        var grid = await Assert.ThrowsAsync<Querithm.Core.QuerithmException>(() => h.Rpc.DataApplyPatch(new GridPatchParams
        {
            SessionId = sid, ExecutionId = exec,
            Updates = [new GridRowUpdate { RowIndex = 0, Key = [V("1")], Changes = new() { ["qty"] = V("7") }, Original = new() { ["qty"] = V("1") } }],
        }));
        Assert.Equal("unsupported", grid.Code);
        Assert.Contains("no savepoints", grid.Message);
        Assert.Contains("Commit or roll back first", grid.Message);
        var import = await Assert.ThrowsAsync<Querithm.Core.QuerithmException>(() => h.Rpc.ImportRun(new ImportRunParams
        {
            SessionId = sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = "main", Table = "orders",
        }));
        Assert.Equal("unsupported", import.Code);
        Assert.Contains("Commit or roll back first", import.Message);

        // Nothing ran: the transaction is still open with the user's own change, and is theirs to end.
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(TxState.None, (await h.Rpc.TxCommit(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(1, Convert.ToInt32(Scalar(path, "SELECT qty FROM orders WHERE id = 1")));
        Assert.Equal(9, Convert.ToInt32(Scalar(path, "SELECT qty FROM orders WHERE id = 3")));
        Assert.Equal(3L, Convert.ToInt64(Scalar(path, "SELECT count(*) FROM orders")));
    }

    static async Task<ImportProgressEvent> ImportAsync(DuckDbHost h, string sid, string csv, string table, ImportRunOptions? options = null)
    {
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = "main", Table = table, Options = options });
        ImportProgressEvent? done = null;
        for (var i = 0; i < 400 && (done = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == run.JobId && e.Done)) == null; i++) await Task.Delay(25);
        return done ?? throw new TimeoutException("import did not finish");
    }

    [Fact]
    public async Task The_appender_rejects_values_that_do_not_convert_and_fills_columns_the_file_lacks_with_defaults()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase("rej.duckdb", "CREATE TABLE items (id INTEGER PRIMARY KEY, qty SMALLINT, born DATE, note VARCHAR DEFAULT 'n/a');");
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await RefreshAsync(h, p, sid, "rej", s => s.Objects.Any(o => o.Name == "items"));
        var csv = Path.Combine(h.Dir, "items.csv");
        await File.WriteAllTextAsync(csv, "id,qty,born\n1,5,2026-01-02\n2,99999,2026-01-03\n3,7,not a date\n4,,2026-01-05\n", new UTF8Encoding(false));
        var done = await ImportAsync(h, sid, csv, "items", new ImportRunOptions { MaxRejects = 10 });
        Assert.Null(done.Error);
        Assert.Equal((2L, 2L), (done.RowsLoaded, done.Rejected));
        Assert.Equal("n/a", Scalar(path, "SELECT note FROM items WHERE id = 1"));
        Assert.IsType<DBNull>(Scalar(path, "SELECT qty FROM items WHERE id = 4"));
    }

    [Fact]
    public async Task A_table_with_a_list_column_is_loaded_with_cast_inserts_in_batches()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase("lists.duckdb", "CREATE TABLE tagged (id INTEGER PRIMARY KEY, tags VARCHAR[]);");
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await RefreshAsync(h, p, sid, "lists", s => s.Objects.Any(o => o.Name == "tagged"));
        var csv = Path.Combine(h.Dir, "tagged.csv");
        await File.WriteAllTextAsync(csv, "id,tags\n1,\"[a, b]\"\n2,[c]\n3,\n", new UTF8Encoding(false));
        var done = await ImportAsync(h, sid, csv, "tagged", new ImportRunOptions { TransactionMode = ImportTransactionMode.Batches, CommitEvery = 2 });
        Assert.Null(done.Error);
        Assert.Equal(3L, done.RowsLoaded);
        Assert.Equal("[a, b]", Scalar(path, "SELECT tags::VARCHAR FROM tagged WHERE id = 1"));
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
    }

    [Fact]
    public async Task A_csv_imports_into_a_new_table_and_a_failure_leaves_nothing_behind()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase("imp.duckdb", "CREATE TABLE other (x INTEGER);");
        var (p, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        var csv = Path.Combine(h.Dir, "items.csv");
        var sb = new StringBuilder("id,name,qty,price,born\n");
        for (var i = 1; i <= 2500; i++) sb.Append($"{i},item {i},{i % 50},{i}.25,2026-01-{1 + i % 28:00}\n");
        await File.WriteAllTextAsync(csv, sb.ToString(), new UTF8Encoding(false));
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var target = await h.Rpc.ImportCheckTarget(new ImportTargetParams { SessionId = sid, InspectionId = ins.InspectionId, Schema = "main", Table = "items", NewTable = true, PrimaryKey = ["id"] });
        Assert.Contains("INTEGER", target.CreateTableSql);
        Assert.Contains("DATE", target.CreateTableSql);
        Assert.Null((await h.RunAsync(sid, target.CreateTableSql!)).Done.Error);
        await RefreshAsync(h, p, sid, "imp", s => s.Objects.Any(o => o.Name == "items"));
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins.InspectionId, FileToken = csv, FilePath = csv, Schema = "main", Table = "items" });
        ImportProgressEvent? done = null;
        for (var i = 0; i < 400 && (done = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == run.JobId && e.Done)) == null; i++) await Task.Delay(25);
        Assert.NotNull(done);
        Assert.Null(done!.Error);
        Assert.Equal(2500L, done.RowsLoaded);
        Assert.Equal(2500L, Convert.ToInt64(Scalar(path, "SELECT count(*) FROM items")));
        Assert.Equal("2026-01-08", Convert.ToString(Scalar(path, "SELECT born::VARCHAR FROM items WHERE id = 7")));

        var ins2 = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = csv, FilePath = csv });
        var again = await h.Rpc.ImportRun(new ImportRunParams { SessionId = sid, InspectionId = ins2.InspectionId, FileToken = csv, FilePath = csv, Schema = "main", Table = "items" });
        ImportProgressEvent? failed = null;
        for (var i = 0; i < 400 && (failed = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == again.JobId && e.Done)) == null; i++) await Task.Delay(25);
        Assert.NotNull(failed?.Error);
        Assert.Equal(2500L, Convert.ToInt64(Scalar(path, "SELECT count(*) FROM items")));
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
    }

    [Fact]
    public async Task Closing_a_tab_while_a_helper_reads_on_its_connection_ends_the_read_first_and_the_file_opens_again()
    {
        // Regression: session.close closed the tab's connection under a helper's open reader (the refresh after DDL).
        // DuckDB kept the database, and on Windows its file lock, until that reader went: opening the file again failed.
        await using var h = new DuckDbHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        Assert.Null((await h.RunAsync(sid, "SELECT 1")).Done.Error);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = Task.Run(async () =>
        {
            await using var lease = await h.Services.Sessions.LeaseAsync(h.Services.Sessions.Get(sid), CancellationToken.None);
            await using var cmd = lease.Conn.Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM range(1000)";
            await using var r = await cmd.ExecuteReaderAsync(lease.Token);
            Assert.True(await r.ReadAsync(lease.Token));
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, lease.Token); // a slow consumer: the reader stays open until the close
        });
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid }).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => helper.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(42, Convert.ToInt32(Scalar(path, "SELECT 42")));
    }
}
