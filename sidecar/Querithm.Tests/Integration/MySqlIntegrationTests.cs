using Apache.Arrow;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.MySql;

namespace Querithm.Tests.Integration;

/// <summary>
/// Phase D2 against the Docker fixture (QUERITHM_IT=1): MySQL 8.4.13 and MariaDB 11.8.9 as one theory over products.
/// The fixture's certificates are self-signed, so these profiles take the explicit Required downgrade; the default
/// (VerifyFull) is shown to refuse them.
/// </summary>
public sealed class MySqlIntegrationTests
{
    /// <summary>MySQL 8.4 (the LTS floor), MySQL 9.7 (the current LTS) and MariaDB 11.8, all pinned by digest.</summary>
    public static TheoryData<string> Products => new() { "mysql", "mysql97", "mariadb" };

    internal static int Port(string product) => product switch { "mysql" => 33084, "mysql97" => 33097, "mysql-lctn" => 33085, _ => 33118 };
    internal static string Db(string product) => $"q_d2_{product.Replace('-', '_')}";

    internal sealed class Host : IAsyncDisposable
    {
        public CapturingNotifier Notifier { get; } = new();
        public CapturingFrames Frames { get; } = new();
        public Services Services { get; }
        public RpcServer Rpc { get; }
        readonly string _dir = Directory.CreateTempSubdirectory("qmysql-").FullName;

        public Host()
        {
            Services = Services.Create(LocalStore.InMemory(), Frames, Notifier, NullLoggerFactory.Instance, new ProviderRegistry([new MySqlProvider()]), _dir);
            Rpc = new RpcServer(Services, () => { });
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    internal static Profile ProfileFor(string product, TlsMode tls = TlsMode.Require, string? database = null) => new()
    {
        Id = "", Name = product, Engine = Engine.MySql, Host = "127.0.0.1", Port = Port(product), Auth = AuthKind.Password, Username = "root",
        TlsMode = tls, DatabaseDefault = database ?? Db(product),
    };

    internal static async Task EnsureDatabaseAsync(string product)
    {
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS {Db(product)}";
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<(Host H, Profile P, string Sid)> OpenAsync(string product)
    {
        await EnsureDatabaseAsync(product);
        var h = new Host();
        var p = await h.Rpc.ProfileSave(ProfileFor(product));
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword });
        return (h, p, s.SessionId);
    }

    internal static async Task<(QueryDoneEvent Done, List<RecordBatch> Batches, string Id)> Run(Host h, string sid, string sql)
    {
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql });
        var done = await h.Notifier.WaitDone(r.ExecutionId!);
        return (done, h.Frames.For(r.ExecutionId!).Where(f => f.Kind == 0x02).Select(f => TestHost.ReadBatch(f.Payload)).ToList(), r.ExecutionId!);
    }

    internal static string? Text(RecordBatch b, string col, int row = 0) => b.Column(col) switch
    {
        StringArray s => s.GetString(row),
        Int64Array i => i.GetValue(row)?.ToString(),
        Int32Array i => i.GetValue(row)?.ToString(),
        Decimal128Array d => d.GetValue(row)?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        var a => a.ToString(),
    };

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Connects_and_tells_the_products_apart(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        var state = await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid });
        Assert.Equal(product == "mariadb" ? "MariaDB" : "MySQL", state.Product?.Name);
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid });
        Assert.Equal(NamespaceModel.Databases, caps.Namespaces);
        Assert.True(caps.DataEdit.Available);
        Assert.True(caps.Import.Available);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Tls_is_verified_by_default_and_refuses_an_untrusted_certificate(string product)
    {
        await using var h = new Host();
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = ProfileFor(product, TlsMode.Default, "mysql"), Secret = TestHost.MySqlPassword });
        // MySQL's self-signed fixture certificate fails VerifyFull. MariaDB 11.4+ verifies its auto-generated certificate
        // with the password ("zero-configuration TLS", which MySqlConnector implements), so VerifyFull passes there.
        Assert.Equal(product == "mariadb", r.Ok);
        var ok = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = ProfileFor(product, TlsMode.Require, "mysql"), Secret = TestHost.MySqlPassword });
        Assert.True(ok.Ok, ok.Error);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Types_travel_losslessly_and_user_variables_work(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        var (done, b, _) = await Run(h, sid, """
            SET @x = 41;
            SELECT @x + 1 AS answer, CAST(12.34 AS DECIMAL(10,2)) AS dec2, CAST('12345678901234567890123456789012.123' AS DECIMAL(65,3)) AS wide,
                   CAST(18446744073709551615 AS UNSIGNED) AS big, CAST('2026-10-31 12:34:56' AS DATETIME) AS dt, 'naïve' AS txt
            """);
        Assert.Null(done.Error);
        var row = b.Single();
        Assert.Equal("42", Text(row, "answer"));
        Assert.Equal("12.34", Text(row, "dec2"));
        Assert.Equal("12345678901234567890123456789012.123", Text(row, "wide"));
        Assert.Equal("18446744073709551615", Text(row, "big"));
        Assert.Equal("2026-10-31 12:34:56", Text(row, "dt"));
        Assert.Equal("naïve", Text(row, "txt"));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task A_zero_date_stays_a_zero_date(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        await Run(h, sid, "SET SESSION sql_mode = ''; DROP TABLE IF EXISTS zd; CREATE TABLE zd (d DATETIME); INSERT INTO zd VALUES ('0000-00-00 00:00:00');");
        var (done, b, _) = await Run(h, sid, "SELECT d FROM zd");
        Assert.Null(done.Error);
        Assert.StartsWith("0000-00-00", Text(b.Single(), "d"));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Load_data_local_is_refused_before_it_runs(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.QueryExecute(new QueryExecuteParams
        {
            SessionId = sid, Sql = "LOAD DATA LOCAL INFILE '/etc/passwd' INTO TABLE zd", Options = new ExecuteOptions { ConfirmedDestructive = true },
        }));
        Assert.Equal("refused", ex.Code);
        var built = new MySqlConnectionStringBuilder(new MySqlProvider().BuildConnectionString(ProfileFor(product) with { Options = new() { ["AllowLoadLocalInfile"] = "true" } }, "x", null));
        Assert.False(built.AllowLoadLocalInfile); // profile extras never reach the driver string
        Assert.True(built.AllowUserVariables);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Ddl_commits_an_open_transaction_implicitly_and_the_tab_sees_it(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        await Run(h, sid, "DROP TABLE IF EXISTS tx_t; CREATE TABLE tx_t (id INT PRIMARY KEY) ENGINE=InnoDB;");
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Run(h, sid, "INSERT INTO tx_t VALUES (1)");
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await Run(h, sid, "CREATE TABLE tx_other (x INT)"); // implicit COMMIT
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await Run(h, sid, "DROP TABLE tx_other");
        // An independent connection sees the committed row.
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};Database={Db(product)};SslMode=Required;Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tx_t";
        Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        // And a rollback leaves nothing.
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Run(h, sid, "INSERT INTO tx_t VALUES (2)");
        Assert.Equal(TxState.None, (await h.Rpc.TxRollback(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Cancel_kills_the_query_and_the_session_is_reused(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT SLEEP(20)" });
        await Task.Delay(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
        var done = await h.Notifier.WaitDone(r.ExecutionId!);
        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"cancel took {sw.ElapsedMilliseconds} ms");
        var (again, b, _) = await Run(h, sid, "SELECT 1 AS one");
        Assert.Null(again.Error);
        Assert.Equal("1", Text(b.Single(), "one"));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Use_switches_the_tabs_database(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        await Run(h, sid, "USE mysql");
        Assert.Equal("mysql", (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).Database);
        var changed = await h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = Db(product) });
        Assert.Equal(Db(product), changed.Database);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task The_catalog_lists_objects_and_markers_follow_ddl(string product)
    {
        var (h, p, sid) = await OpenAsync(product);
        await using var _ = h;
        await Run(h, sid, """
            DROP TABLE IF EXISTS m_child; DROP TABLE IF EXISTS m_parent; DROP VIEW IF EXISTS m_view; DROP PROCEDURE IF EXISTS m_proc;
            CREATE TABLE m_parent (id INT PRIMARY KEY, name VARCHAR(20) NOT NULL);
            CREATE TABLE m_child (id INT PRIMARY KEY, parent_id INT, CONSTRAINT fk_child FOREIGN KEY (parent_id) REFERENCES m_parent(id));
            CREATE VIEW m_view AS SELECT id FROM m_parent;
            CREATE PROCEDURE m_proc(IN a INT, OUT b INT) SELECT a INTO b;
            CREATE TRIGGER m_trg BEFORE INSERT ON m_child FOR EACH ROW SET NEW.id = NEW.id;
            """);
        var meta = (MySqlMetadata)new MySqlProvider().Metadata;
        await using var c = (MySqlConnection)await new MySqlProvider().OpenAsync(ProfileFor(product), TestHost.MySqlPassword, Db(product), null, default);
        var stamps = await meta.GetStampsAsync(c, default);
        var child = stamps.Single(s => s.Name == "m_child");
        Assert.Equal(ObjectKind.Table, child.Kind);
        Assert.Equal(child.Id, stamps.Single(s => s.Name == "m_trg").ParentObjectId);
        Assert.Equal(ObjectKind.View, stamps.Single(s => s.Name == "m_view").Kind);
        Assert.Equal(ObjectKind.Proc, stamps.Single(s => s.Name == "m_proc").Kind);
        var details = await meta.GetDetailsAsync(c, stamps.Select(s => s.Id).ToList(), default);
        Assert.Contains(details.Indexes, i => i.ObjectId == child.Id && i.Primary);
        Assert.Contains(details.Fks, f => f.Parent == child.Id && f.Referenced == stamps.Single(s => s.Name == "m_parent").Id);
        Assert.Equal(2, details.Params.Count(x => x.ObjectId == stamps.Single(s => s.Name == "m_proc").Id));
        // No DDL: same markers. An ALTER: the table's marker changes (UPDATE_TIME is not used).
        // Other test classes share this database and run in parallel: compare this test's objects only.
        static IEnumerable<(long, long)> Mine(IEnumerable<Querithm.Contract.Providers.ObjectStamp> all) => all.Where(s => s.Name.StartsWith("m_")).OrderBy(s => s.Id).Select(s => (s.Id, s.Marker));
        var again = await meta.GetStampsAsync(c, default);
        Assert.Equal(Mine(stamps), Mine(again));
        await Run(h, sid, "ALTER TABLE m_child ADD COLUMN note VARCHAR(10)");
        var after = await meta.GetStampsAsync(c, default);
        Assert.NotEqual(child.Marker, after.Single(s => s.Name == "m_child").Marker);
        Assert.Equal(stamps.Single(s => s.Name == "m_parent").Marker, after.Single(s => s.Name == "m_parent").Marker);
        var def = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = Db(product), Name = "m_proc", Kind = ObjectKind.Proc }, default);
        Assert.Contains("m_proc", def);
        Assert.NotNull(p.Id);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Server_warnings_reach_the_messages_tab(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        var (done, _, id) = await Run(h, sid, "SELECT CAST('abc' AS SIGNED) AS n");
        Assert.Null(done.Error);
        var warnings = h.Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == id && m.Severity == Severity.Warning).ToList();
        Assert.Contains(warnings, m => m.Message.Contains("abc", StringComparison.OrdinalIgnoreCase) || m.Number == 1292);
        // Each server warning or note reaches the tab once (regression: the driver's InfoMessage and SHOW WARNINGS both sent it).
        Assert.Single(warnings);
        var (_, _, drop) = await Run(h, sid, "DROP TABLE IF EXISTS q_no_such_table_for_note");
        Assert.Single(h.Notifier.Of<ServerMessage>(), m => m.ExecutionId == drop && m.Message.Contains("q_no_such_table_for_note"));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Estimated_plans_come_from_explain_json(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        await Run(h, sid, "DROP TABLE IF EXISTS pl; CREATE TABLE pl (id INT PRIMARY KEY, v INT, KEY ix_v (v))");
        var plan = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT * FROM pl WHERE v = 3", Mode = PlanMode.Estimated });
        static IEnumerable<PlanNode> All(PlanNode n) => n.Children.SelectMany(All).Prepend(n);
        Assert.Contains(All(plan.Tree.Statements.Single().Root!), n => n.ObjectName == "pl");
        var reparsed = await h.Rpc.PlanParse(new PlanParseParams { Engine = Engine.MySql, Raw = plan.Raw! });
        Assert.Single(reparsed.Plan.Statements);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products))]
    public async Task Actual_plans_carry_measured_rows_and_writes_are_refused(string product)
    {
        var (h, _, sid) = await OpenAsync(product);
        await using var _ = h;
        await Run(h, sid, """
            DROP TABLE IF EXISTS pa_child; DROP TABLE IF EXISTS pa;
            CREATE TABLE pa (id INT PRIMARY KEY, v INT); INSERT INTO pa VALUES (1, 10), (2, 20), (3, 30);
            CREATE TABLE pa_child (id INT PRIMARY KEY, pa_id INT); INSERT INTO pa_child VALUES (1, 1), (2, 1), (3, 2);
            """);
        Assert.True((await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid })).ActualPlan.Available);
        var plan = await h.Rpc.PlanAsync(h.Notifier, new PlanParams
        {
            SessionId = sid, Sql = "SELECT p.v FROM pa p JOIN pa_child c ON c.pa_id = p.id WHERE p.v > 5", Mode = PlanMode.Actual, ConfirmedRun = true,
        });
        static IEnumerable<PlanNode> All(PlanNode n) => n.Children.SelectMany(All).Prepend(n);
        var nodes = All(plan.Tree.Statements.Single().Root!).ToList();
        // Tables appear under their aliases: MariaDB's table_name, MySQL's "Table scan on c".
        Assert.Contains(nodes, n => n.ObjectName is "p" or "c" || n.Operator.EndsWith(" on c") || n.Operator.EndsWith(" on p"));
        Assert.Contains(nodes, n => n.ActualRows is > 0);
        Assert.Contains(nodes, n => n.ElapsedMs is > 0);
        var reparsed = await h.Rpc.PlanParse(new PlanParseParams { Engine = Engine.MySql, Raw = plan.Raw! });
        Assert.Equal(nodes.Count, All(reparsed.Plan.Statements.Single().Root!).Count());
        // A write would run for real (and DDL commits): refused, and nothing changed.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = "UPDATE pa SET v = 0", Mode = PlanMode.Actual, ConfirmedRun = true }));
        Assert.Contains("only SELECT and TABLE", ex.Message);
        var (done, batches, _) = await Run(h, sid, "SELECT SUM(v) AS s FROM pa");
        Assert.Null(done.Error);
        Assert.Equal("60", Text(batches[0], "s"));
    }
}
