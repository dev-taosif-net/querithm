using Querithm.Contract;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Plan parsing is pure: canned showplan XML (SQL Server) and EXPLAIN JSON (PostgreSQL) must yield operators, costs, warnings and missing indexes.</summary>
public class PlanParsingTests
{
    const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    static string ShowPlan(string queryPlanInner, string relOps, string queryPlanAttrs = "") => $"""
        <ShowPlanXML xmlns="{Ns}"><BatchSequence><Batch><Statements>
        <StmtSimple StatementText="SELECT 1" StatementSubTreeCost="10">
          <QueryPlan {queryPlanAttrs}>{queryPlanInner}{relOps}</QueryPlan>
        </StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    [Fact]
    public void SqlServer_plan_tree_carries_objects_predicates_runtime_counters_and_skew_warnings()
    {
        const string ops = """
            <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10" EstimateRewinds="0" EstimateRebinds="0" EstimatedTotalSubtreeCost="10">
              <OutputList><ColumnReference Table="[o]" Column="Id"/><ColumnReference Alias="[c]" Column="Name"/></OutputList>
              <RunTimeInformation>
                <RunTimeCountersPerThread Thread="0" ActualRows="500" ActualExecutions="1" ActualElapsedms="12" ActualLogicalReads="40"/>
                <RunTimeCountersPerThread Thread="1" ActualRows="500" ActualExecutions="1" ActualElapsedms="15" ActualLogicalReads="2"/>
              </RunTimeInformation>
              <NestedLoops>
                <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="5" EstimatedTotalSubtreeCost="3">
                  <IndexScan Lookup="0">
                    <Object Database="[db]" Schema="[dbo]" Table="[Orders]" Index="[IX_Orders]"/>
                    <SeekPredicates><SeekPredicateNew><SeekKeys><Prefix><RangeColumns/><RangeExpressions><ScalarOperator ScalarString="[o].[Id]=(1)"/></RangeExpressions></Prefix></SeekKeys></SeekPredicateNew></SeekPredicates>
                    <Predicate><ScalarOperator ScalarString="[o].[Qty]&gt;(1)"/></Predicate>
                  </IndexScan>
                </RelOp>
                <RelOp NodeId="2" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="2">
                  <IndexScan Lookup="1"><Object Schema="[dbo]" Table="[Customers]"/></IndexScan>
                </RelOp>
              </NestedLoops>
            </RelOp>
            """;
        var tree = new SqlServerPlans().Parse(ShowPlan("", ops));
        var stmt = Assert.Single(tree.Statements);
        Assert.Equal("SELECT 1", stmt.Text);
        Assert.Equal(10, stmt.SubtreeCost);
        var root = stmt.Root!;
        Assert.Equal("Nested Loops (Inner Join)", root.Operator);
        Assert.Equal(1000, root.ActualRows);
        Assert.Equal(15, root.ElapsedMs);
        Assert.Equal(42, root.LogicalReads);
        Assert.Contains(root.Warnings, w => w.Kind == "cardinality");
        Assert.Equal("o.Id, c.Name", root.Properties["Output List"]);
        Assert.Equal(2, root.Children.Length);
        var seek = root.Children[0];
        Assert.Equal("dbo.Orders.IX_Orders", seek.ObjectName);
        Assert.Equal("[o].[Id]=(1)", seek.Properties["Seek Predicate"]);
        Assert.Equal("[o].[Qty]>(1)", seek.Properties["Predicate"]);
        Assert.Equal("Key Lookup (Clustered)", root.Children[1].Operator);
        Assert.Equal(50, root.CostPercent);   // own cost 10 - (3 + 2) = 5 of the statement cost 10
    }

    [Fact]
    public void SqlServer_warnings_and_missing_indexes_are_described_for_the_ui()
    {
        const string inner = """
            <Warnings NoJoinPredicate="true" SomethingElse="1" Ignored="0">
              <PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(int,[t].[a],0)"/>
              <SpillToTempDb SpillLevel="2"/>
              <HashSpillDetails WritesToTempDb="12"/>
              <SortSpillDetails WritesToTempDb="3"/>
              <ExchangeSpillDetails WritesToTempDb="4"/>
              <NoJoinPredicate/>
              <ColumnsWithNoStatistics><ColumnReference Column="[a]"/><ColumnReference Column="[b]"/></ColumnsWithNoStatistics>
              <MemoryGrantWarning GrantWarningKind="ExcessiveGrant"/>
              <UnmatchedIndexes/>
            </Warnings>
            <MissingIndexes>
              <MissingIndexGroup Impact="87.5">
                <MissingIndex Database="[db]" Schema="[dbo]" Table="[Orders]">
                  <ColumnGroup Usage="EQUALITY"><Column Name="[CustomerId]"/></ColumnGroup>
                  <ColumnGroup Usage="INEQUALITY"><Column Name="[OrderDate]"/></ColumnGroup>
                  <ColumnGroup Usage="INCLUDE"><Column Name="[Total]"/><Column Name="[Status]"/></ColumnGroup>
                </MissingIndex>
                <MissingIndex Schema="[dbo]" Table="[Customers]">
                  <ColumnGroup Usage="EQUALITY"><Column Name="[Name]"/></ColumnGroup>
                </MissingIndex>
              </MissingIndexGroup>
            </MissingIndexes>
            """;
        var stmt = Assert.Single(new SqlServerPlans().Parse(ShowPlan(inner, "", "")).Statements);
        Assert.Null(stmt.Root);
        Assert.Contains(stmt.Warnings, w => w.StartsWith("Implicit conversion (Seek Plan)"));
        Assert.Contains(stmt.Warnings, w => w == "Spill to tempdb (level 2)");
        Assert.Contains(stmt.Warnings, w => w == "Hash spill: 12 pages written to tempdb" || w.StartsWith("HashSpill"));
        Assert.Contains(stmt.Warnings, w => w.StartsWith("Sort") && w.Contains('3'));
        Assert.Contains(stmt.Warnings, w => w.StartsWith("Exchange") && w.Contains('4'));
        Assert.Contains(stmt.Warnings, w => w == "No join predicate");
        Assert.Contains(stmt.Warnings, w => w == "Columns with no statistics: [a], [b]");
        Assert.Contains(stmt.Warnings, w => w == "Memory grant warning: ExcessiveGrant");
        Assert.Contains(stmt.Warnings, w => w == "UnmatchedIndexes");
        Assert.Contains(stmt.Warnings, w => w == "SomethingElse");
        Assert.DoesNotContain(stmt.Warnings, w => w == "Ignored");

        Assert.Equal(2, stmt.MissingIndexes.Length);
        var mi = stmt.MissingIndexes[0];
        Assert.Equal(87.5, mi.Impact);
        Assert.Equal("dbo.Orders", mi.Table);
        Assert.Equal(["CustomerId"], mi.EqualityColumns);
        Assert.Equal(["OrderDate"], mi.InequalityColumns);
        Assert.Equal(["Total", "Status"], mi.IncludeColumns);
        Assert.Equal("CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId_OrderDate]\nON [dbo].[Orders] ([CustomerId], [OrderDate])\nINCLUDE ([Total], [Status]);", mi.CreateStatement);
        Assert.DoesNotContain("INCLUDE", stmt.MissingIndexes[1].CreateStatement);
    }

    [Fact]
    public void SqlServer_statement_times_and_cost_fall_back_to_the_root_operator()
    {
        const string inner = """<QueryTimeStats CpuTime="7" ElapsedTime="9"/>""";
        const string ops = """<RelOp NodeId="0" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="1" EstimatedTotalSubtreeCost="4"/>""";
        var xml = $"""<ShowPlanXML xmlns="{Ns}"><BatchSequence><Batch><Statements><StmtSimple StatementText="x"><QueryPlan>{inner}{ops}</QueryPlan></StmtSimple><StmtCursor StatementText="y"/></Statements></Batch></BatchSequence></ShowPlanXML>""";
        var tree = new SqlServerPlans().Parse(xml);
        Assert.Equal(2, tree.Statements.Length);
        Assert.Equal(4, tree.Statements[0].SubtreeCost);
        Assert.Equal(9, tree.Statements[0].ElapsedMs);
        Assert.Equal(7, tree.Statements[0].CpuMs);
        Assert.Equal("Table Scan", tree.Statements[0].Root!.Operator);
        Assert.Equal(0, tree.Statements[1].SubtreeCost);
        Assert.Null(tree.Statements[1].Root);
        Assert.Empty(tree.Statements[1].MissingIndexes);
    }

    [Fact]
    public void Postgres_plan_tree_flags_disk_sorts_hash_batches_and_cardinality_misses()
    {
        const string json = """
            [{
              "Plan": {
                "Node Type": "Hash Join", "Join Type": "Inner", "Total Cost": 100.0, "Plan Rows": 10, "Actual Rows": 5000, "Actual Loops": 1, "Actual Total Time": 4.5,
                "Shared Hit Blocks": 10, "Shared Read Blocks": 5,
                "Plans": [
                  { "Node Type": "Sort", "Total Cost": 40.0, "Plan Rows": 100, "Actual Rows": 100, "Actual Loops": 2, "Sort Space Type": "Disk", "Sort Space Used": 2048, "Sort Key": ["a", "b"] },
                  { "Node Type": "Hash", "Total Cost": 20.0, "Plan Rows": 100, "Hash Batches": 4 },
                  { "Node Type": "Index Scan", "Schema": "public", "Relation Name": "orders", "Index Name": "orders_pkey", "Total Cost": 5.0, "Plan Rows": 1, "Actual Rows": 0, "Actual Loops": 1 },
                  { "Node Type": "Index Only Scan", "Index Name": "only_idx", "Total Cost": 5.0, "Plan Rows": 100, "Actual Rows": 1, "Actual Loops": 1 }
                ]
              },
              "Execution Time": 12.5
            }]
            """;
        var tree = new PostgresPlans().Parse(json);
        var stmt = Assert.Single(tree.Statements);
        Assert.Equal(100, stmt.SubtreeCost);
        Assert.Equal(12.5, stmt.ElapsedMs);
        var root = stmt.Root!;
        Assert.Equal("Hash Join (Inner)", root.Operator);
        Assert.Equal(5000, root.ActualRows);
        Assert.Equal(15, root.LogicalReads);
        Assert.Contains(root.Warnings, w => w.Kind == "cardinality");
        Assert.Equal(4, root.Children.Length);
        var sort = root.Children[0];
        Assert.Contains(sort.Warnings, w => w.Kind == "spill" && w.Message.Contains("2048"));
        Assert.Equal("a, b", sort.Properties["Sort Key"]);
        Assert.Equal(200, sort.ActualRows);   // rows per loop times loops
        Assert.Contains(root.Children[1].Warnings, w => w.Kind == "spill" && w.Message.Contains("4 batches"));
        Assert.Equal("public.orders.orders_pkey", root.Children[2].ObjectName);
        Assert.Equal("only_idx", root.Children[3].ObjectName);
        Assert.Contains(root.Children[3].Warnings, w => w.Kind == "cardinality");   // 1 actual vs 100 estimated
        Assert.DoesNotContain(root.Children[2].Warnings, w => w.Kind == "cardinality");
    }
}
