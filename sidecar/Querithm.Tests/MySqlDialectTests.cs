using Querithm.Contract;
using Querithm.Providers.MySql;
using Querithm.Contract.Providers;

namespace Querithm.Tests;

/// <summary>Phase D2 grid-edit and import rules for MySQL / MariaDB that need no server.</summary>
public sealed class MySqlDialectTests
{
    static readonly MySqlDataEdit Edit = new();
    static readonly MySqlImport Import = new();

    [Theory]
    [InlineData("int(11) unsigned zerofill", "int", true, new[] { 11 })]
    [InlineData("bigint unsigned", "bigint", true, new int[0])]
    [InlineData("decimal(12,4)", "decimal", false, new[] { 12, 4 })]
    [InlineData("enum('a,b','c')", "enum", false, new int[0])]
    [InlineData("datetime(3)", "datetime", false, new[] { 3 })]
    public void Column_types_are_taken_apart(string type, string name, bool unsigned, int[] args)
    {
        var t = MySqlType.Parse(type);
        Assert.Equal((name, unsigned), (t.Name, t.Unsigned));
        Assert.Equal(args, t.Args);
    }

    [Theory]
    [InlineData("tinyint(1)", "true", 1)]
    [InlineData("tinyint(1)", "False", 0)]
    [InlineData("bit(3)", "7", 7UL)]
    [InlineData("int unsigned", "4294967295", "4294967295")]
    [InlineData("decimal(65,30)", "-1.5e10", "-1.5e10")]
    [InlineData("date", "0000-00-00", "0000-00-00")]
    [InlineData("datetime(3)", "2026-01-02 03:04:05.12", "2026-01-02 03:04:05.12")]
    [InlineData("varchar(10)", " keeps spaces ", " keeps spaces ")]
    public void Grid_text_becomes_the_parameter_the_column_takes(string type, string text, object expected) =>
        Assert.Equal(expected, MySqlDataEdit.ToParameter(MySqlType.Parse(type), text));

    [Fact]
    public void Binary_text_is_hex_and_a_shortened_value_is_refused()
    {
        Assert.Equal(new byte[] { 0x0A, 0xFF }, MySqlDataEdit.ToParameter(MySqlType.Parse("varbinary(16)"), "0x0AFF"));
        Assert.Throws<FormatException>(() => MySqlDataEdit.ToParameter(MySqlType.Parse("blob"), "0x0A0B…"));
        Assert.Throws<FormatException>(() => MySqlDataEdit.ToParameter(MySqlType.Parse("binary(2)"), "0xABC"));
        Assert.Throws<FormatException>(() => MySqlDataEdit.ToParameter(MySqlType.Parse("binary(2)"), "hello"));
    }

    [Theory]
    [InlineData("int unsigned", "-1")]
    [InlineData("tinyint", "128")]
    [InlineData("bit(3)", "8")]
    [InlineData("int", "1.5")]
    [InlineData("decimal(10,2)", "abc")]
    [InlineData("date", "2026-02-30")]
    [InlineData("datetime", "yesterday")]
    [InlineData("year", "1800")]
    public void Values_the_server_would_reject_or_change_are_refused_here(string type, string text) =>
        Assert.Throws<FormatException>(() => MySqlDataEdit.ToParameter(MySqlType.Parse(type), text));

    [Fact]
    public void An_inserted_row_is_read_back_by_its_given_key_or_last_insert_id_and_never_guessed()
    {
        var auto = Edit.InsertReturningKey("`d`.`t`", [("name", "@p0")], [("id", "int", true)]);
        Assert.Equal("INSERT INTO `d`.`t` (`name`) VALUES (@p0); SELECT CAST(`id` AS CHAR) FROM `d`.`t` WHERE `id` = LAST_INSERT_ID()", auto);
        var given = Edit.InsertReturningKey("`d`.`t`", [("code", "@p0")], [("code", "char(3)", false)]);
        Assert.EndsWith("WHERE `code` <=> @p0", given);
        // A key filled by an expression default: no key comes back rather than another row's.
        var unknown = Edit.InsertReturningKey("`d`.`t`", [("name", "@p0")], [("uuid", "binary(16)", false)]);
        Assert.EndsWith("WHERE FALSE", unknown);
        Assert.Contains("CONCAT('0x', HEX(`uuid`))", unknown);
    }

    [Fact]
    public void Preview_literals_read_the_same_whatever_the_sql_mode()
    {
        Assert.Equal("X'615C62'", Edit.PreviewLiteral("varchar(10)", new GridValue { Text = "a\\b" }));
        Assert.Equal("'it''s'", Edit.PreviewLiteral("varchar(10)", new GridValue { Text = "it's" }));
        Assert.Equal("X'0AFF'", Edit.PreviewLiteral("varbinary(4)", new GridValue { Text = "0x0aff" }));
        Assert.Equal("1", Edit.PreviewLiteral("tinyint(1)", new GridValue { Text = "true" }));
    }

    [Fact]
    public void Floats_time_json_and_long_binary_are_not_compared_or_used_as_keys()
    {
        foreach (var t in new[] { "double", "float", "time(3)", "json", "blob", "varbinary(255)", "point" })
            Assert.False(Edit.Comparable(t) || Edit.ExactKey(t), t);
        foreach (var t in new[] { "int", "bigint unsigned", "varchar(20)", "decimal(10,2)", "datetime(6)", "binary(16)", "enum('a')" })
            Assert.True(Edit.Comparable(t) && Edit.ExactKey(t), t);
    }

    static ImportColumnProfile Col(ImportKind kind, string? min = null, string? max = null, int precision = 0, int scale = 0, int maxLength = 0) =>
        new() { Name = "c", Kind = kind, Min = min, Max = max, Precision = precision, Scale = scale, MaxLength = maxLength };

    static SnapColumn Target(string type) => new() { Name = "c", Type = type, Ordinal = 1, Nullable = true };

    [Fact]
    public void Import_types_new_tables_and_widens_by_the_file()
    {
        Assert.Equal("tinyint(1)", Import.TypeFor(Col(ImportKind.Bool)));
        Assert.Equal("decimal(12,4)", Import.TypeFor(Col(ImportKind.Decimal, precision: 12, scale: 4)));
        Assert.Equal("text", Import.TypeFor(Col(ImportKind.Decimal, precision: 70, scale: 2))); // beyond DECIMAL(65,30): kept as text
        Assert.Equal("varchar(255)", Import.TypeFor(Col(ImportKind.Text, maxLength: 40)));
        Assert.Equal("mediumtext", Import.TypeFor(Col(ImportKind.Text, maxLength: 20000)));
        var wide = Import.Check(Col(ImportKind.Int64, "0", "5000000000"), Target("int unsigned"));
        Assert.Equal((ImportVerdictKind.Widen, "bigint"), (wide.Kind, wide.WidenTo));
        Assert.Equal(ImportVerdictKind.Fits, Import.Check(Col(ImportKind.Int32, "0", "4000000000"), Target("int unsigned")).Kind);
        Assert.Equal(ImportVerdictKind.Mismatch, Import.Check(Col(ImportKind.DateTimeOffset), Target("datetime")).Kind);
        Assert.Equal("varchar(300)", Import.Check(Col(ImportKind.Text, maxLength: 300), Target("varchar(50)")).WidenTo);
        Assert.False(Import.BlankIsNull("varchar(5)"));
        Assert.True(Import.BlankIsNull("int"));
    }

    [Fact]
    public void A_text_key_column_becomes_varchar_and_modify_says_what_it_restates()
    {
        var sql = Import.CreateTable("d", "t", [("k", "text", false), ("v", "int", false)], ["k"]);
        Assert.Contains("`k` varchar(768) NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (`k`)", sql);
        Assert.EndsWith("ENGINE=InnoDB;", sql);
        var alter = Import.AlterColumnType("d", "t", new SnapColumn { Name = "v", Type = "int", Ordinal = 2, Nullable = false }, "bigint");
        Assert.Contains("MODIFY COLUMN `v` bigint NOT NULL;", alter);
        Assert.StartsWith("-- MODIFY restates", alter);
    }
}

/// <summary>Phase D2 plan formats, from real server output: TREE text (MySQL 8 EXPLAIN ANALYZE), JSON v2 (MySQL 9), MariaDB ANALYZE.</summary>
public sealed class MySqlPlanTests
{
    static readonly MySqlPlans Plans = new();

    static string Raw(string plan) => System.Text.Json.JsonSerializer.Serialize(new[] { new Dictionary<string, string> { ["text"] = "SELECT 1", ["plan"] = plan } });

    static IEnumerable<PlanNode> All(PlanNode n) => n.Children.SelectMany(All).Prepend(n);

    [Fact]
    public void Tree_text_nests_by_indentation_with_cumulative_costs_and_per_loop_rows()
    {
        const string tree = """
            -> Nested loop inner join  (cost=1.15 rows=2) (actual time=0.323..0.329 rows=2 loops=1)
                -> Filter: (u.t_id is not null)  (cost=0.45 rows=2) (actual time=0.0167..0.0197 rows=2 loops=1)
                    -> Table scan on u  (cost=0.45 rows=2) (actual time=0.0154..0.0181 rows=2 loops=1)
                -> Filter: (t.v > 3)  (cost=0.3 rows=1) (actual time=0.00631..0.00652 rows=1 loops=2)
                    -> Single-row index lookup on t using PRIMARY (id=u.t_id)  (cost=0.3 rows=1) (never executed)
            """;
        var root = Plans.Parse(Raw(tree)).Statements.Single().Root!;
        Assert.Equal("Nested loop inner join", root.Operator);
        Assert.Equal(2, root.Children.Length);
        Assert.Equal(1.15, root.SubtreeCost, 6);
        Assert.Equal(1.15 - 0.45 - 0.3, root.EstCost, 6);
        var filter = root.Children[1];
        Assert.Equal((1.0 * 2, 2.0), (filter.ActualRows, filter.ActualExecutions)); // rows are per loop
        Assert.Equal(0.00652 * 2, filter.ElapsedMs!.Value, 9);
        var lookup = filter.Children.Single();
        Assert.Equal(0, lookup.ActualRows);
        Assert.Equal("true", lookup.Properties["never executed"]);
    }

    [Fact]
    public void Json_v2_is_an_operator_tree_with_inputs()
    {
        const string v2 = """
            {"query": "select 1", "query_plan": {"operation": "Nested loop inner join", "access_type": "join", "estimated_rows": 2.0,
              "estimated_total_cost": 0.9, "actual_rows": 2.0, "actual_loops": 1, "actual_last_row_ms": 0.05,
              "inputs": [
                {"operation": "Table scan on u", "table_name": "u", "schema_name": "q", "access_type": "table", "estimated_rows": 2.0, "estimated_total_cost": 0.45,
                 "actual_rows": 2.0, "actual_loops": 1, "actual_last_row_ms": 0.02, "used_columns": ["t_id"]},
                {"operation": "Single-row index lookup on t using PRIMARY (id = u.t_id)", "table_name": "t", "index_name": "PRIMARY", "access_type": "index",
                 "estimated_rows": 1.0, "estimated_total_cost": 0.35, "actual_rows": 1.0, "actual_loops": 2, "actual_last_row_ms": 0.004}]},
             "query_type": "select", "json_schema_version": "2.0"}
            """;
        var root = Plans.Parse(Raw(v2)).Statements.Single().Root!;
        Assert.Equal(("Nested loop inner join", "join"), (root.Operator, root.PhysicalOp));
        Assert.Equal(["u", "t"], root.Children.Select(c => c.ObjectName));
        Assert.Equal(0.9 - 0.45 - 0.35, root.EstCost, 6);
        Assert.Equal((2.0, 2.0), (root.Children[1].ActualRows, root.Children[1].ActualExecutions));
        Assert.Equal("t_id", root.Children[0].Properties["used_columns"]);
        Assert.Null(Plans.Parse(Raw(v2.Replace("\"actual_rows\": 1.0, \"actual_loops\": 2, \"actual_last_row_ms\": 0.004", "\"x\": 1"))).Statements[0].Root!.Children[1].ActualRows);
    }

    [Fact]
    public void Mariadb_analyze_adds_measured_rows_loops_and_time_to_the_v1_layout()
    {
        const string analyze = """
            {"query_optimization": {"r_total_time_ms": 0.14}, "query_block": {"select_id": 1, "cost": 0.0138, "r_loops": 1, "r_total_time_ms": 0.03,
              "nested_loop": [
                {"table": {"table_name": "u", "access_type": "ALL", "loops": 1, "r_loops": 1, "rows": 2, "r_rows": 2, "cost": 0.0111, "r_table_time_ms": 0.01, "r_other_time_ms": 0.006}},
                {"table": {"table_name": "t", "access_type": "eq_ref", "key": "PRIMARY", "used_key_parts": ["id"], "loops": 2, "r_loops": 2, "rows": 1, "r_rows": 1, "cost": 0.0026}}]}}
            """;
        var root = Plans.Parse(Raw(analyze)).Statements.Single().Root!;
        Assert.Equal(0.03, root.ElapsedMs);
        var tables = All(root).Where(n => n.ObjectName is "u" or "t").ToDictionary(n => n.ObjectName!);
        Assert.Equal((2.0, 1.0, 0.016), (tables["u"].ActualRows!.Value, tables["u"].ActualExecutions!.Value, Math.Round(tables["u"].ElapsedMs!.Value, 3)));
        Assert.Equal((2.0, 0.0026), (tables["t"].ActualRows!.Value, tables["t"].EstCost));
    }

    [Theory]
    [InlineData("SELECT * FROM t", true)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", true)]
    [InlineData("TABLE t", true)]
    [InlineData("SELECT * FROM t INTO OUTFILE '/tmp/x'", false)]
    [InlineData("SELECT 1 INTO @v", false)]
    [InlineData("UPDATE t SET v = 1", false)]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t", false)]
    [InlineData("/*!50000 DROP TABLE t */", false)]
    [InlineData("CREATE TABLE t2 AS SELECT * FROM t", false)]
    public void Only_reads_get_an_actual_plan(string sql, bool allowed)
    {
        var code = Querithm.Intelligence.MySql.MySqlSplitter.Split(sql).Single().Tokens.Where(t => !t.IsTrivia).ToList();
        Assert.Equal(allowed, MySqlPlans.ReadOnly(code));
    }

    [Theory]
    [InlineData("8.0.17", false)]
    [InlineData("8.0.18", true)]
    [InlineData("8.4.13", true)]
    [InlineData("9.7.4", true)]
    [InlineData("5.7.44-log", false)]
    public void Explain_analyze_needs_mysql_8_0_18(string version, bool supported) => Assert.Equal(supported, MySqlPlans.SupportsExplainAnalyze(version));
}

public sealed class MySqlSignInReasonTests
{
    [Fact]
    public void Driver_sign_in_failures_are_said_in_querithm_terms()
    {
        var sha2 = MySqlProvider.SignInReason("Authentication method 'caching_sha2_password' failed. Either use a secure connection, specify the server's RSA public key with ServerRSAPublicKeyFile, or set AllowPublicKeyRetrieval=True.");
        Assert.Contains("Turn TLS on", sha2);
        Assert.DoesNotContain("AllowPublicKeyRetrieval", sha2);
        Assert.Contains("ed25519", MySqlProvider.SignInReason("You must install the MySqlConnector.Authentication.Ed25519 package and call Ed25519AuthenticationPlugin.Install to use client_ed25519 authentication."));
        Assert.Contains("parsec", MySqlProvider.SignInReason("Authentication method 'parsec' not supported by any of the available plugins."));
        Assert.Null(MySqlProvider.SignInReason("Access denied for user 'x'@'y' (using password: YES)"));
    }
}

/// <summary>Phase D2 monitor rules that need no server.</summary>
public sealed class MySqlActivityRulesTests
{
    static MySqlActivity.Probe Probe(bool maria = false, bool ps = true, bool process = true, bool killOthers = true, string version = "8.4.13") =>
        new(version, maria, ps, process, killOthers, "root@%", maria);

    [Fact]
    public void Mysql_with_performance_schema_offers_every_pane_it_reads()
    {
        var caps = MySqlActivity.Capabilities(Probe());
        Assert.True(caps.CanTerminate && caps.CanCancelRequest && caps.AllSessionsVisible);
        foreach (var pane in new[] { MonitorPane.Sessions, MonitorPane.Locks, MonitorPane.Waits, MonitorPane.Metrics, MonitorPane.TopQueries })
            Assert.True(caps.Panes.Single(p => p.Pane == pane).Available, pane.ToString());
        Assert.False(caps.Panes.Single(p => p.Pane == MonitorPane.Deadlocks).Available);
        Assert.Null(caps.QueryHashReason);
    }

    [Fact]
    public void Mariadb_without_performance_schema_refuses_kills_and_says_why()
    {
        var caps = MySqlActivity.Capabilities(Probe(maria: true, ps: false, version: "11.8.9-MariaDB-ubu2404"));
        Assert.False(caps.CanTerminate || caps.CanCancelRequest);
        Assert.Contains("performance_schema", caps.TerminateReason);
        Assert.True(caps.Panes.Single(p => p.Pane == MonitorPane.Sessions).Available);
        Assert.False(caps.Panes.Single(p => p.Pane == MonitorPane.Locks).Available);
        Assert.False(caps.Panes.Single(p => p.Pane == MonitorPane.TopQueries).Available);
        Assert.StartsWith("MariaDB ", caps.ServerVersion);
    }

    [Fact]
    public void Missing_privileges_narrow_what_is_shown_and_killable()
    {
        var caps = MySqlActivity.Capabilities(Probe(process: false, killOthers: false));
        Assert.False(caps.AllSessionsVisible);
        Assert.Contains("PROCESS", caps.SessionsReason);
        Assert.Contains("CONNECTION_ADMIN", caps.OtherLoginsReason);
    }

    [Theory]
    [InlineData("5.7.44", false, "MySQL 8.0")]
    [InlineData("8.0.36", false, null)]
    [InlineData("10.5.27-MariaDB", true, "MariaDB 10.6")]
    [InlineData("10.11.9-MariaDB", true, null)]
    public void The_monitor_needs_mysql_8_or_mariadb_10_6(string version, bool maria, string? refusal)
    {
        var r = MySqlActivity.StartRefusal(Probe(maria: maria, version: version));
        if (refusal == null) Assert.Null(r); else Assert.Contains(refusal, r);
    }

    [Theory]
    [InlineData("wait/lock/innodb/row", "Lock")]
    [InlineData("state:Waiting for table metadata lock", "Lock")]
    [InlineData("wait/synch/mutex/innodb/buf_pool_mutex", "Latch")]
    [InlineData("wait/io/file/innodb/innodb_log_file", "Log IO")]
    [InlineData("wait/io/file/innodb/innodb_data_file", "Buffer IO")]
    [InlineData("wait/io/table/sql/handler", "Buffer IO")]
    [InlineData("wait/io/socket/sql/client_connection", "Network")]
    [InlineData("idle", "Other")]
    public void Wait_instruments_map_to_the_monitor_categories(string wait, string category) => Assert.Equal(category, new MySqlActivity().WaitCategory(wait));

    [Fact]
    public void Identities_are_thread_ids_and_statement_starts_are_event_ids()
    {
        var a = new MySqlActivity();
        Assert.True(a.IsValidInstance("12345") && a.IsValidInstance(""));
        Assert.False(a.IsValidInstance("12a") || a.IsValidInstance(new string('9', 21)));
        Assert.True(a.IsValidStatementStart("77"));
        Assert.False(a.IsValidStatementStart("") || a.IsValidStatementStart("-1"));
        Assert.True(a.IsBenignWait("idle"));
        Assert.False(a.IsBenignWait("wait/lock/innodb/row"));
    }
}

public sealed class MySqlDataCompareTests
{
    static readonly MySqlDataCompare Dc = new();

    [Theory]
    [InlineData("int(11)", Querithm.Contract.Providers.ValueKind.Integer)]
    [InlineData("bigint unsigned", Querithm.Contract.Providers.ValueKind.Integer)]
    [InlineData("char(5)", Querithm.Contract.Providers.ValueKind.PaddedText)]
    [InlineData("varchar(50)", Querithm.Contract.Providers.ValueKind.Text)]
    [InlineData("enum('a','b')", Querithm.Contract.Providers.ValueKind.Text)]
    [InlineData("decimal(10,2)", Querithm.Contract.Providers.ValueKind.Decimal)]
    [InlineData("double", Querithm.Contract.Providers.ValueKind.Float)]
    [InlineData("datetime(3)", Querithm.Contract.Providers.ValueKind.DateTime)]
    [InlineData("varbinary(16)", Querithm.Contract.Providers.ValueKind.Binary)]
    [InlineData("time", Querithm.Contract.Providers.ValueKind.Unordered)]
    [InlineData("json", Querithm.Contract.Providers.ValueKind.Unordered)]
    [InlineData("point", Querithm.Contract.Providers.ValueKind.Unordered)]
    public void Column_types_compare_by_their_kind(string type, Querithm.Contract.Providers.ValueKind kind) => Assert.Equal(kind, Dc.KindOf(type));

    [Fact]
    public void Text_keys_order_by_utf8_bytes_whatever_the_collation()
    {
        Assert.Equal("CAST(CONVERT(`k` USING utf8mb4) AS BINARY)", Dc.OrderExpression("`k`", Querithm.Contract.Providers.ValueKind.Text));
        Assert.Equal("`id`", Dc.OrderExpression("`id`", Querithm.Contract.Providers.ValueKind.Integer));
    }

    [Theory]
    [InlineData("SELECT * FROM t", null)]
    [InlineData("WITH q AS (SELECT 1) SELECT * FROM q;", null)]
    [InlineData("TABLE t", null)]
    [InlineData("SELECT 1; SELECT 2", "exactly one")]
    [InlineData("UPDATE t SET a = 1", "must be a SELECT")]
    [InlineData("WITH q AS (SELECT 1) DELETE FROM t", "must be a SELECT")]
    [InlineData("SELECT * FROM t INTO OUTFILE '/tmp/x'", "INTO")]
    [InlineData("  ", "empty")]
    public void A_compared_query_is_one_read(string sql, string? refusal)
    {
        var r = Dc.QueryRefusal(sql);
        if (refusal == null) Assert.Null(r); else Assert.Contains(refusal, r);
    }

    [Fact]
    public void A_derived_query_keeps_with_and_order_by_and_drops_the_semicolon() =>
        Assert.Equal(("", "WITH q AS (SELECT 1 AS id) SELECT * FROM q ORDER BY id"), Dc.DerivedQuery("WITH q AS (SELECT 1 AS id) SELECT * FROM q ORDER BY id; -- end"));
}

public sealed class MySqlSchemaCompareRulesTests
{
    static readonly MySqlSchemaCompare Sc = new();

    [Theory]
    [InlineData("CREATE DEFINER=`root`@`%` PROCEDURE `p`() SELECT 1", "CREATE PROCEDURE `p`() SELECT 1")]
    [InlineData("CREATE DEFINER=`app`@`10.0.0.%` TRIGGER `t` BEFORE INSERT ON `x` FOR EACH ROW SET NEW.a = 1", "CREATE TRIGGER `t` BEFORE INSERT ON `x` FOR EACH ROW SET NEW.a = 1")]
    [InlineData("CREATE DEFINER=CURRENT_USER() FUNCTION f() RETURNS INT RETURN 1", "CREATE FUNCTION f() RETURNS INT RETURN 1")]
    [InlineData("CREATE SQL SECURITY DEFINER VIEW v AS SELECT 1", "CREATE SQL SECURITY DEFINER VIEW v AS SELECT 1")]
    public void The_definer_account_is_removed_and_sql_security_kept(string def, string expected) => Assert.Equal(expected, MySqlSchemaCompare.StripDefiner(def));

    [Fact]
    public void Object_names_and_a_triggers_table_are_qualified_by_the_database()
    {
        Assert.Equal("CREATE PROCEDURE `shop`.`p`() SELECT 1", MySqlSchemaCompare.Qualify("CREATE PROCEDURE `p`() SELECT 1", "shop"));
        Assert.Equal("CREATE TRIGGER `shop`.`t` BEFORE INSERT ON `shop`.`orders` FOR EACH ROW SET NEW.a = 1",
            MySqlSchemaCompare.Qualify("CREATE TRIGGER `t` BEFORE INSERT ON `orders` FOR EACH ROW SET NEW.a = 1", "shop"));
        Assert.Equal("CREATE PROCEDURE `x`.`p`() SELECT 1", MySqlSchemaCompare.Qualify("CREATE PROCEDURE `x`.`p`() SELECT 1", "shop"));
    }

    [Theory]
    [InlineData("abc", "", "varchar", false, "'abc'")]
    [InlineData("it's", "", "varchar", false, "'it''s'")]
    [InlineData("0.00", "", "decimal", false, "0.00")]
    [InlineData("CURRENT_TIMESTAMP(3)", "DEFAULT_GENERATED on update CURRENT_TIMESTAMP(3)", "datetime", false, "CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3)")]
    [InlineData("(uuid())", "DEFAULT_GENERATED", "char", false, "((uuid()))")]
    [InlineData("'abc'", "", "varchar", true, "'abc'")]
    [InlineData("NULL", "", "varchar", true, null)]
    [InlineData("current_timestamp()", "on update current_timestamp()", "timestamp", true, "current_timestamp() ON UPDATE current_timestamp()")]
    public void Column_defaults_become_sql(string? columnDefault, string extra, string dataType, bool maria, string? expected) =>
        Assert.Equal(expected, MySqlSchemaCompare.DefaultSql(columnDefault, extra, dataType, maria));

    [Theory]
    [InlineData("int(11)", "int")]
    [InlineData("int(10) unsigned", "int unsigned")]
    [InlineData("tinyint(1)", "tinyint(1)")]
    [InlineData("bigint(20) unsigned zerofill", "bigint unsigned zerofill")]
    [InlineData("varchar(50)", "varchar(50)")]
    public void Integer_display_widths_are_dropped(string type, string expected) => Assert.Equal(expected, MySqlSchemaCompare.CanonicalType(type));

    [Fact]
    public void Normalisation_folds_case_quotes_and_the_niladic_parentheses()
    {
        Assert.Equal(Sc.NormalizeExpression("CURRENT_TIMESTAMP"), Sc.NormalizeExpression("current_timestamp()"));
        Assert.Equal(Sc.NormalizeExpression("(`price` > 0)"), Sc.NormalizeExpression("price > 0"));
        Assert.Equal("create view v as select 1", Sc.NormalizeBody("CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`%` SQL SECURITY DEFINER VIEW `v` AS select 1"));
        Assert.Equal("ALTER TABLE `d`.`t` DROP PRIMARY KEY;", Sc.DropConstraint(new ModelTable { Schema = "d", Name = "t", Columns = [], PrimaryKey = new ModelKey { Name = "PRIMARY", Columns = ["id"] } }, "PRIMARY"));
        Assert.Contains("DROP FOREIGN KEY `fk`", Sc.DropConstraint(new ModelTable { Schema = "d", Name = "t", Columns = [], Fks = [new ModelFk { Name = "fk", Columns = ["a"], RefSchema = "d", RefTable = "x", RefColumns = ["id"] }] }, "fk"));
        Assert.Contains("DROP CONSTRAINT `ck`", Sc.DropConstraint(new ModelTable { Schema = "d", Name = "t", Columns = [] }, "ck"));
        Assert.Throws<NotSupportedException>(() => Sc.RenameObject(Querithm.Contract.Providers.RefKind.Procedure, "d", "p", "q"));
    }
}
