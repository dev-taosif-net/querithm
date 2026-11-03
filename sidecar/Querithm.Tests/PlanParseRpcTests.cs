using System.Text;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Metadata;

namespace Querithm.Tests;

/// <summary>
/// plan.parse (Phase C2, "Open as plan" from a result cell): showplan XML and EXPLAIN (FORMAT JSON) text become the plan
/// view's tree without a server; anything else is refused at the RPC boundary with a reason.
/// </summary>
public class PlanParseRpcTests
{
    const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    internal static string ShowPlan(string relOps = """<RelOp NodeId="0" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="5" EstimatedTotalSubtreeCost="0.5"><IndexScan><Object Schema="[dbo]" Table="[Orders]"/></IndexScan></RelOp>""") => $"""
        <?xml version="1.0" encoding="utf-16"?>
        <ShowPlanXML xmlns="{Ns}" Version="1.6"><BatchSequence><Batch><Statements>
        <StmtSimple StatementText="SELECT * FROM dbo.Orders" StatementSubTreeCost="0.5"><QueryPlan>{relOps}</QueryPlan></StmtSimple>
        </Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    internal const string PgPlan = """
        [
          {
            "Plan": { "Node Type": "Seq Scan", "Relation Name": "orders", "Schema": "public", "Total Cost": 12.5, "Plan Rows": 250 }
          }
        ]
        """;

    static Task<PlanParseResult> Parse(TestHost h, Engine engine, string raw) => h.Rpc.PlanParse(new PlanParseParams { Engine = engine, Raw = raw });

    static async Task<QuerithmException> Refused(TestHost h, Engine engine, string raw)
    {
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Parse(h, engine, raw));
        Assert.Equal("invalid", ex.Code);
        return ex;
    }

    [Fact]
    public async Task SqlServer_showplan_xml_becomes_a_plan_tree()
    {
        await using var h = new TestHost();
        var r = await Parse(h, Engine.Mssql, ShowPlan());
        Assert.Equal("xml", r.Format);
        var stmt = Assert.Single(r.Plan.Statements);
        Assert.Equal("SELECT * FROM dbo.Orders", stmt.Text);
        Assert.Equal("dbo.Orders", stmt.Root!.ObjectName);
    }

    [Fact]
    public async Task Postgres_explain_json_becomes_a_plan_tree()
    {
        await using var h = new TestHost();
        var r = await Parse(h, Engine.Pg, PgPlan);
        Assert.Equal("json", r.Format);
        var stmt = Assert.Single(r.Plan.Statements);
        Assert.Equal(12.5, stmt.SubtreeCost);
        Assert.Equal("Seq Scan", stmt.Root!.PhysicalOp);
    }

    [Fact]
    public async Task Empty_and_oversized_text_is_refused_before_parsing()
    {
        await using var h = new TestHost();
        Assert.Contains("empty", (await Refused(h, Engine.Mssql, "  ")).Message);
        var huge = ShowPlan() + new string(' ', PlanParsing.MaxChars);
        Assert.Contains("10 MB", (await Refused(h, Engine.Mssql, huge)).Message);
        Assert.Contains("10 MB", (await Refused(h, Engine.Pg, "[" + new string(' ', PlanParsing.MaxChars) + "]")).Message);
    }

    [Fact]
    public async Task A_missing_parameter_object_or_an_unknown_engine_is_refused()
    {
        await using var h = new TestHost();
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanParse(null!));
        Assert.Equal("invalid", ex.Code);
        Assert.Contains("engine", (await Refused(h, (Engine)42, ShowPlan())).Message);
    }

    [Theory]
    [InlineData("<ShowPlanXML><unclosed></ShowPlanXML>", "Not a SQL Server showplan")]
    [InlineData("<root><a/></root>", "root element is <root>")]
    [InlineData("not xml at all", "Not a SQL Server showplan")]
    [InlineData("<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"/>", "no statement plan")]
    public async Task Text_that_is_not_a_readable_showplan_is_refused_with_the_reason(string raw, string reason)
    {
        await using var h = new TestHost();
        Assert.Contains(reason, (await Refused(h, Engine.Mssql, raw)).Message);
    }

    [Theory]
    [InlineData("{\"Plan\": {}}")]
    [InlineData("[{\"NotAPlan\": 1}]")]
    [InlineData("[{\"Plan\": {\"Node Type\": \"Seq Scan\"}, \"Execution Time\": \"soon\"}]")]
    [InlineData("[{\"Plan\": ")]
    [InlineData("<ShowPlanXML/>")]
    public async Task Text_that_is_not_a_readable_explain_json_is_refused(string raw)
    {
        await using var h = new TestHost();
        Assert.Contains("Not a PostgreSQL EXPLAIN (FORMAT JSON) plan", (await Refused(h, Engine.Pg, raw)).Message);
    }

    [Fact]
    public async Task An_empty_explain_array_is_refused()
    {
        await using var h = new TestHost();
        Assert.Contains("no plan", (await Refused(h, Engine.Pg, "[]")).Message);
    }

    [Fact]
    public async Task Deeply_nested_plans_are_refused_instead_of_overflowing_the_stack()
    {
        await using var h = new TestHost();
        // 2,000 nested operators: the plan walk recurses per operator, so this must stop at the depth check.
        var sb = new StringBuilder();
        for (var i = 0; i < 2000; i++) sb.Append($"""<RelOp NodeId="{i}" PhysicalOp="Nested Loops" EstimatedTotalSubtreeCost="1"><NestedLoops>""");
        for (var i = 0; i < 2000; i++) sb.Append("</NestedLoops></RelOp>");
        Assert.Contains("deeper than", (await Refused(h, Engine.Mssql, ShowPlan(sb.ToString()))).Message);
        var json = new StringBuilder("[");
        for (var i = 0; i < 2000; i++) json.Append("{\"Plan\": {\"Node Type\": \"Hash\", \"Plans\": [");
        Assert.Contains("Not a PostgreSQL EXPLAIN", (await Refused(h, Engine.Pg, json.ToString())).Message);
    }
}
