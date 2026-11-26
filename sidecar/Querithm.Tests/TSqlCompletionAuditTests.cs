using Querithm.Contract;
using Querithm.Intelligence.TSql.Completion;

namespace Querithm.Tests;

/// <summary>
/// Completion audit 2026-11-25, SQL Server tier 2: each case returned nothing or the wrong scope before its fix
/// (H4, H5, M1, M2, the OVER (ORDER BY) aliases); the "still" cases pin the behaviour the fixes must keep.
/// </summary>
public class TSqlCompletionAuditTests
{
    static readonly FakeSchema Sales = new(FakeSchema.SalesDb(), "SalesDB");

    static string[] Labels(string sql)
    {
        var at = sql.IndexOf('|');
        return TSqlCompletion.Complete(new CompleteParams
        {
            DocId = "audit-" + Guid.NewGuid(), Text = sql.Remove(at, 1), Offset = at, Dialect = Dialect.Tsql, ProfileId = "p1", Database = "SalesDB",
        }, Sales, CancellationToken.None).Select(i => i.Label).ToArray();
    }

    static void Has(string[] items, params string[] labels)
    {
        foreach (var l in labels) Assert.True(items.Contains(l), $"expected '{l}' in [{string.Join(", ", items.Take(50))}]");
    }

    static void Lacks(string[] items, params string[] labels)
    {
        foreach (var l in labels) Assert.False(items.Contains(l), $"did not expect '{l}' in [{string.Join(", ", items.Take(50))}]");
    }

    // H4: SET <option> ON without ';' then DML — the usual procedure preamble.

    [Fact]
    public void Set_nocount_on_without_a_semicolon_ends_before_the_update()
    {
        var items = Labels("CREATE TABLE #w (Id int, Amount money)\nGO\nCREATE PROCEDURE dbo.p AS\nSET NOCOUNT ON\nUPDATE #w SET Amount = 0 WHERE |");
        Has(items, "Id", "Amount");
    }

    [Theory]
    [InlineData("SET NOCOUNT ON\nUPDATE |")]
    [InlineData("SET XACT_ABORT ON\nDELETE FROM |")]
    [InlineData("SET IDENTITY_INSERT dbo.Orders ON\nINSERT INTO |")]
    public void A_statement_after_a_set_option_offers_tables(string sql) => Has(Labels(sql), "Orders", "Customers");

    [Fact]
    public void Still_a_foreign_key_action_stays_in_its_create_table()
    {
        // ON DELETE CASCADE / ON UPDATE SET NULL continue the CREATE TABLE: the column list after them still completes types.
        var doc = DocModel.Get("CREATE TABLE dbo.x (Id int REFERENCES dbo.Orders (OrderId) ON DELETE CASCADE ON UPDATE SET NULL, Note int)", "fk-" + Guid.NewGuid());
        Assert.Single(doc.Segs, s => s.Kind == SegKind.Statement);
    }

    // H5: WITH … INSERT … SELECT is one statement.

    [Fact]
    public void A_cte_feeding_insert_select_is_one_statement()
    {
        var items = Labels("CREATE TABLE #w (Id int, Amount money);\nWITH c AS (SELECT OrderId, TotalAmount AS Amt FROM dbo.Orders) INSERT INTO #w (Id, Amount) SELECT c.| FROM c");
        Has(items, "OrderId", "Amt");
    }

    [Fact]
    public void A_cte_feeding_insert_exec_is_one_statement()
    {
        var doc = DocModel.Get("WITH c AS (SELECT 1 AS x) INSERT INTO #w EXEC dbo.usp_GetCustomerOrders 1", "ie-" + Guid.NewGuid());
        Assert.Single(doc.Segs, s => s.Kind == SegKind.Statement);
    }

    // M1: a derived table does not see the query around it.

    [Fact]
    public void A_derived_table_sees_neither_the_outer_sources_nor_the_placeholder()
    {
        var items = Labels("SELECT * FROM dbo.Customers c JOIN (SELECT | FROM dbo.Orders) d ON d.CustomerId = c.CustomerId");
        Has(items, "OrderId", "TotalAmount");
        Lacks(items, "c.CustomerId", "c.FullName");
        Assert.DoesNotContain(items, l => l.Contains(StatementAnalysis.Placeholder, StringComparison.Ordinal));
    }

    [Fact]
    public void Still_apply_and_correlated_subqueries_see_the_outer_query()
    {
        Has(Labels("SELECT * FROM dbo.Customers c CROSS APPLY (SELECT TOP 1 o.OrderId FROM dbo.Orders o WHERE o.CustomerId = |) x"), "c.CustomerId");
        Has(Labels("SELECT * FROM dbo.Customers c WHERE EXISTS (SELECT 1 FROM dbo.Orders o WHERE o.CustomerId = |)"), "c.CustomerId");
    }

    // Review 2026-11-25 of M1: only the derived table's own FROM is hidden. Before, every level outside it went too.

    [Fact]
    public void A_derived_table_inside_a_subquery_still_sees_the_outer_query()
    {
        var items = Labels("SELECT * FROM dbo.Customers c WHERE EXISTS (SELECT 1 FROM (SELECT o.OrderId FROM dbo.Orders o WHERE o.CustomerId = |) d)");
        Has(items, "c.CustomerId", "o.TotalAmount");
        Lacks(items, "d.OrderId");
    }

    [Fact]
    public void No_derived_table_is_offered_inside_its_own_definition_however_far_out()
    {
        // Two nested APPLYs: only the innermost alias (y) was left out, so x and x.OrderId were offered inside x's own body.
        var items = Labels("SELECT * FROM dbo.Customers c CROSS APPLY (SELECT o.OrderId FROM dbo.Orders o CROSS APPLY (SELECT TOP 1 o.TotalAmount AS t WHERE o.CustomerId = |) y) x");
        Has(items, "c.CustomerId", "o.TotalAmount");
        Lacks(items, "x", "x.OrderId", "y");
    }

    // Review 2026-11-25 (B#5): a SET target after a CASE value.

    [Fact]
    public void A_set_target_after_a_case_value_offers_only_the_target_columns_bare()
    {
        var items = Labels("UPDATE o SET Status = CASE WHEN o.TotalAmount > 1 THEN 2 END, | FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId");
        Has(items, "TotalAmount", "OrderDate");
        Lacks(items, "c.FullName", "o.TotalAmount");
    }

    [Fact]
    public void Still_the_value_inside_an_open_case_sees_every_source()
    {
        Has(Labels("UPDATE o SET Status = CASE WHEN | FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId"), "c.FullName", "o.TotalAmount");
    }

    // M2: an open CASE or '(' at the cursor keeps the FROM after it.

    [Theory]
    [InlineData("SELECT o.OrderId, CASE WHEN o.| FROM dbo.Orders o")]
    [InlineData("SELECT COUNT(o.| FROM dbo.Orders o")]
    [InlineData("SELECT o.OrderId, CASE WHEN o.| FROM dbo.Orders o WHERE")]
    // Review 2026-11-25: LEFT( / RIGHT( are the string functions, not a join, when the repair cuts the rest at its next clause.
    [InlineData("SELECT o.OrderId, CASE WHEN o.| , LEFT(o.Status, 2) AS s FROM dbo.Orders o WHERE")]
    [InlineData("SELECT o.OrderId, CASE WHEN o.| , RIGHT(o.Status, 2) AS s FROM dbo.Orders o WHERE")]
    public void An_open_case_or_parenthesis_at_the_cursor_keeps_the_from(string sql) => Has(Labels(sql), "TotalAmount", "Status");

    // The query's ORDER BY sees select-list aliases; a window's or WITHIN GROUP's ORDER BY does not.

    [Fact]
    public void Select_list_aliases_are_not_offered_inside_over_order_by()
    {
        var items = Labels("SELECT TotalAmount AS amt, ROW_NUMBER() OVER (ORDER BY |) AS rn FROM dbo.Orders");
        Lacks(items, "amt", "rn");
        Has(items, "TotalAmount");
    }

    [Fact]
    public void Still_the_querys_own_order_by_offers_select_list_aliases()
    {
        Has(Labels("SELECT TotalAmount AS amt FROM dbo.Orders ORDER BY |"), "amt", "TotalAmount");
    }
}
