using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Providers.Oracle;
using Op = Querithm.Providers.Oracle.OraclePlans.Operation;

namespace Querithm.Tests;

/// <summary>
/// D4 S09: Oracle plans without a server — PLAN_TABLE / V$SQL_PLAN_STATISTICS_ALL rows → PlanTree, the raw JSON read back
/// as untrusted input (plan.parse), what is refused before anything runs, and the GATHER_PLAN_STATISTICS hint placed on tokens.
/// </summary>
public class OraclePlanTests
{
    static readonly OraclePlans Plans = new();

    static Op Row(int id, int? parent, string operation, string? options = null, double? cost = null, double? rows = null, string? owner = null, string? name = null,
        string? access = null, string? filter = null, double? starts = null, double? outRows = null, double? elapsedUs = null, double? cr = null, double? cu = null, double? temp = null) =>
        new(id, parent, operation, options, owner, name, null, null, null, cost, rows, null, null, null, null, null, null, access, filter, null,
            starts, outRows, elapsedUs, cr, cu, null, temp, null);

    /// <summary>EMP joined to DEPT by a hash join, as EXPLAIN PLAN writes it (ids depth-first, parents first).</summary>
    static readonly List<Op> Estimated =
    [
        Row(0, null, "SELECT STATEMENT", cost: 6, rows: 14),
        Row(1, 0, "HASH JOIN", cost: 6, rows: 14, access: "\"E\".\"DEPT_ID\"=\"D\".\"DEPT_ID\""),
        Row(2, 1, "TABLE ACCESS", "FULL", cost: 3, rows: 4, owner: "APP", name: "DEPT"),
        Row(3, 1, "TABLE ACCESS", "FULL", cost: 2, rows: 14, owner: "APP", name: "EMP", filter: "\"E\".\"SAL\">1000"),
    ];

    static PlanTree Tree(List<Op> rows, bool actual = false, string text = "SELECT * FROM emp e JOIN dept d ON d.dept_id = e.dept_id") =>
        Plans.Parse(OraclePlans.Write([(text, rows)], actual));

    [Fact]
    public void Plan_table_rows_become_a_tree_by_parent_id()
    {
        var st = Assert.Single(Tree(Estimated).Statements);
        Assert.StartsWith("SELECT * FROM emp", st.Text);
        var root = st.Root!;
        Assert.Equal("SELECT STATEMENT", root.Operator);
        Assert.Equal(6, st.SubtreeCost);
        var join = Assert.Single(root.Children);
        Assert.Equal("HASH JOIN", join.Operator);
        Assert.Equal("HASH JOIN", join.LogicalOp);
        Assert.Equal(["2", "3"], join.Children.Select(c => c.Id));
        var dept = join.Children[0];
        Assert.Equal("TABLE ACCESS FULL", dept.Operator);
        Assert.Equal("TABLE ACCESS", dept.LogicalOp);
        Assert.Equal("APP.DEPT", dept.ObjectName);
        Assert.Equal(4, dept.EstRows);
        Assert.Equal("\"E\".\"DEPT_ID\"=\"D\".\"DEPT_ID\"", join.Properties["Access predicates"]);
        Assert.Equal("\"E\".\"SAL\">1000", join.Children[1].Properties["Filter predicates"]);
        Assert.Null(dept.ActualRows);
    }

    [Fact]
    public void Oracle_costs_are_cumulative_so_each_operation_keeps_only_its_own_share()
    {
        var join = Tree(Estimated).Statements[0].Root!.Children[0];
        Assert.Equal(6, join.SubtreeCost);
        Assert.Equal(1, join.EstCost); // 6 − (3 + 2)
        Assert.Equal(3, join.Children[0].EstCost);
        Assert.Equal(50, join.Children[0].CostPercent);
        // A child costed above its parent (rounding in the optimizer's figures) never makes a negative share.
        var odd = Tree([Row(0, null, "SELECT STATEMENT", cost: 1), Row(1, 0, "TABLE ACCESS", "FULL", cost: 2)]).Statements[0].Root!;
        Assert.Equal(0, odd.EstCost);
    }

    [Fact]
    public void Actual_rows_starts_time_and_buffers_come_from_the_last_run()
    {
        var rows = new List<Op>
        {
            Row(0, null, "SELECT STATEMENT", cost: 6, starts: 1, outRows: 14, elapsedUs: 2500, cr: 10, cu: 0),
            Row(1, 0, "NESTED LOOPS", cost: 6, rows: 14, starts: 1, outRows: 14, elapsedUs: 2400, cr: 10),
            Row(2, 1, "INDEX", "UNIQUE SCAN", cost: 0, rows: 1, starts: 14, outRows: 14, elapsedUs: 100, cr: 4, cu: 1),
        };
        var st = Tree(rows, actual: true).Statements[0];
        Assert.Equal(2.5, st.ElapsedMs);
        var probe = st.Root!.Children[0].Children[0];
        Assert.Equal(14, probe.ActualRows);
        Assert.Equal(14, probe.ActualExecutions);
        Assert.Equal(0.1, probe.ElapsedMs);
        Assert.Equal(5, probe.LogicalReads);
        // E-Rows is per start: 1 row × 14 starts = 14 rows is no misestimate.
        Assert.Empty(probe.Warnings);
    }

    [Fact]
    public void Spills_cartesian_joins_and_large_misestimates_are_warned()
    {
        var rows = new List<Op>
        {
            Row(0, null, "SELECT STATEMENT", cost: 9, starts: 1, outRows: 5000),
            Row(1, 0, "MERGE JOIN", "CARTESIAN", cost: 9, rows: 10, starts: 1, outRows: 5000, temp: 1048576),
        };
        var join = Tree(rows, actual: true).Statements[0].Root!.Children[0];
        Assert.Equal(["cardinality", "noJoinPredicate", "spill"], join.Warnings.Select(w => w.Kind).Order(StringComparer.Ordinal));
        Assert.Contains("1,048,576 bytes", join.Warnings.Single(w => w.Kind == "spill").Message);
        Assert.Contains("Estimated 10 rows per start, got 5,000", join.Warnings.Single(w => w.Kind == "cardinality").Message);
    }

    [Fact]
    public void A_plan_deep_as_the_limit_reads_without_recursion_and_one_deeper_is_refused()
    {
        List<Op> Chain(int n) => Enumerable.Range(0, n + 1).Select(i => Row(i, i == 0 ? null : i - 1, "VIEW", cost: n - i)).ToList();
        var node = Tree(Chain(OraclePlans.MaxDepth)).Statements[0].Root!;
        var depth = 0;
        while (node.Children.Length > 0) { node = node.Children[0]; depth++; }
        Assert.Equal(OraclePlans.MaxDepth, depth);
        Assert.Throws<InvalidOperationException>(() => Tree(Chain(OraclePlans.MaxDepth + 1)));
    }

    [Theory]
    [InlineData("""[{"text": "x", "actual": false, "operations": [{"id": 0, "operation": "SELECT STATEMENT"}, {"id": 1, "parentId": 7, "operation": "X"}]}]""")] // parent not listed
    [InlineData("""[{"text": "x", "actual": false, "operations": [{"id": 1, "operation": "SELECT STATEMENT"}, {"id": 0, "parentId": 1, "operation": "X"}]}]""")] // parent after the child
    [InlineData("""[{"text": "x", "actual": false, "operations": [{"id": 0, "operation": "A"}, {"id": 0, "parentId": 0, "operation": "B"}]}]""")] // id twice
    [InlineData("""[{"text": "x", "actual": false, "operations": [{"id": 0, "operation": "A"}, {"id": 1, "operation": "B"}]}]""")] // two roots
    [InlineData("""[{"text": "x", "actual": false, "operations": [{"id": 1, "parentId": 1, "operation": "A"}]}]""")] // its own parent, no root
    [InlineData("""[{"text": "x", "actual": false, "operations": [{"id": -1, "operation": "A"}]}]""")]
    [InlineData("""[{"text": "x", "actual": false, "operations": [{"id": 0}]}]""")] // no operation name
    [InlineData("""[{"text": "x", "actual": false}]""")]
    [InlineData("""{"text": "x"}""")]
    [InlineData("""[{"text": "x", "actual": false, "operations": [{"id": "zero", "operation": "A"}]}]""")]
    [InlineData("not json")]
    public void Plan_parse_refuses_text_that_is_not_an_oracle_plan(string raw)
    {
        var ex = Assert.Throws<QuerithmException>(() => PlanParsing.Parse(Registry(), new PlanParseParams { Engine = Engine.Oracle, Raw = raw }));
        Assert.Equal("invalid", ex.Code);
    }

    [Fact]
    public void Plan_parse_reads_the_raw_text_back_as_format_oracle_json()
    {
        var raw = OraclePlans.Write([("SELECT 1 FROM dual", [Row(0, null, "SELECT STATEMENT", cost: 2, rows: 1), Row(1, 0, "FAST DUAL", cost: 2, rows: 1)])], false);
        var r = PlanParsing.Parse(Registry(), new PlanParseParams { Engine = Engine.Oracle, Raw = raw });
        Assert.Equal("oracle-json", r.Format);
        Assert.Equal("FAST DUAL", r.Plan.Statements[0].Root!.Children[0].Operator);
        // An empty operations list (nothing returned) is a statement without a tree, not an error.
        Assert.Null(Plans.Parse("""[{"text": "x", "actual": true, "operations": []}]""").Statements[0].Root);
    }

    static ProviderRegistry Registry() => new([new OracleProvider()]);

    // ------------------------------------------------------------------------------------- refused before anything runs

    [Theory]
    [InlineData("SELECT * FROM emp")]
    [InlineData("WITH d AS (SELECT * FROM dept) SELECT * FROM d")]
    [InlineData("INSERT INTO t SELECT * FROM u")]
    [InlineData("UPDATE emp SET sal = sal * 1.1 WHERE deptno = 10")]
    [InlineData("DELETE FROM emp WHERE id = 1")]
    [InlineData("MERGE INTO t USING u ON (t.id = u.id) WHEN MATCHED THEN UPDATE SET t.v = u.v")]
    [InlineData("SELECT 1 FROM dual;\nSELECT 2 FROM dual;")]
    [InlineData("SET SERVEROUTPUT ON\nSELECT 1 FROM dual")]
    public void Sql_statements_plan_in_both_modes(string sql)
    {
        if (sql.StartsWith("SET", StringComparison.Ordinal))
        {
            // A SQL*Plus line holds no statement to plan: refused with its name (also SET SERVEROUTPUT, which only toggles output).
            Assert.Contains("SET on line 1 is a SQL*Plus command", Plans.Refusal(sql, PlanMode.Estimated));
            return;
        }
        Assert.Null(Plans.Refusal(sql, PlanMode.Estimated));
        Assert.Null(Plans.Refusal(sql, PlanMode.Actual));
    }

    [Theory]
    [InlineData("CREATE TABLE t2 AS SELECT * FROM emp")]
    [InlineData("CREATE INDEX emp_ix ON emp (ename)")]
    [InlineData("CREATE UNIQUE INDEX emp_ux ON emp (ename)")]
    public void Ddl_explain_plan_accepts_is_estimated_only(string sql)
    {
        Assert.Null(Plans.Refusal(sql, PlanMode.Estimated));
        Assert.Contains("is DDL", Plans.Refusal(sql, PlanMode.Actual));
        Assert.Contains("commits before and after", Plans.Refusal(sql, PlanMode.Actual));
    }

    [Theory]
    [InlineData("BEGIN DELETE FROM emp; END;", "PL/SQL unit")]
    [InlineData("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;\n/", "PL/SQL unit")]
    [InlineData("DROP TABLE emp", "(DROP) has no plan")]
    [InlineData("CREATE TABLE t (a NUMBER)", "(CREATE) has no plan")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = x", "(ALTER) has no plan")]
    [InlineData("COMMIT", "(COMMIT) has no plan")]
    [InlineData("EXEC p", "EXEC on line 1 is a SQL*Plus command")]
    [InlineData("  ", "no SQL statement")]
    public void What_has_no_plan_is_refused_with_the_reason(string sql, string says)
    {
        Assert.Contains(says, Plans.Refusal(sql, PlanMode.Estimated));
        Assert.Contains(says, Plans.Refusal(sql, PlanMode.Actual));
    }

    [Fact]
    public void An_actual_plan_cannot_fill_in_binds_and_says_which()
    {
        const string sql = "SELECT * FROM emp WHERE id = :id AND name = :\"Who\"";
        Assert.Null(Plans.Refusal(sql, PlanMode.Estimated)); // EXPLAIN PLAN takes unbound binds (measured on the fixture)
        var why = Plans.Refusal(sql, PlanMode.Actual);
        Assert.Contains(":ID, :Who", why);
        Assert.Contains("estimated plan", why);
        // A ':name' inside a string or comment is no bind.
        Assert.Null(Plans.Refusal("SELECT ':x' FROM dual -- :y", PlanMode.Actual));
    }

    [Fact]
    public async Task GetPlan_refuses_what_Refusal_refuses_before_touching_the_connection()
    {
        // The connection is never used: Refusal is checked first (a null connection would throw otherwise).
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => Plans.GetPlanAsync(null!, "DROP TABLE emp", PlanMode.Estimated, default));
        Assert.Contains("has no plan", ex.Message);
    }

    // --------------------------------------------------------------------------------------------- GATHER_PLAN_STATISTICS

    [Theory]
    [InlineData("SELECT * FROM emp", "SELECT /*+ GATHER_PLAN_STATISTICS */ * FROM emp")]
    [InlineData("select * from emp", "select /*+ GATHER_PLAN_STATISTICS */ * from emp")]
    [InlineData("/* lead */ SELECT 1 FROM dual", "/* lead */ SELECT /*+ GATHER_PLAN_STATISTICS */ 1 FROM dual")]
    [InlineData("SELECT /*+ FULL(e) */ * FROM emp e", "SELECT /*+ GATHER_PLAN_STATISTICS FULL(e) */ * FROM emp e")]
    [InlineData("SELECT --+ FULL(e)\n * FROM emp e", "SELECT --+ GATHER_PLAN_STATISTICS FULL(e)\n * FROM emp e")]
    [InlineData("WITH d AS (SELECT /*+ MATERIALIZE */ * FROM dept) SELECT * FROM d", "WITH d AS (SELECT /*+ MATERIALIZE */ * FROM dept) SELECT /*+ GATHER_PLAN_STATISTICS */ * FROM d")]
    [InlineData("UPDATE emp SET sal = 1 WHERE id = 2", "UPDATE /*+ GATHER_PLAN_STATISTICS */ emp SET sal = 1 WHERE id = 2")]
    [InlineData("SELECT 'SELECT /*+ x */' FROM dual", "SELECT /*+ GATHER_PLAN_STATISTICS */ 'SELECT /*+ x */' FROM dual")]
    public void The_statistics_hint_goes_right_after_the_verb_merged_into_a_hint_already_there(string sql, string expected) =>
        Assert.Equal(expected, OraclePlans.Hinted(sql));

    [Fact]
    public void The_provider_plans_with_OraclePlans() => Assert.IsType<OraclePlans>(new OracleProvider(null, null).Plans);
}
