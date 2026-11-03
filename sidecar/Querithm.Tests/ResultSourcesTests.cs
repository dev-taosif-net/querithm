using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

/// <summary>Result-column origins for SQL Server key markers: exact where the AST is unambiguous, nothing otherwise.</summary>
public class ResultSourcesTests
{
    static readonly Contract.SchemaSnapshot Sales = FakeSchema.SalesDb();

    static List<PredictedColumn[]?>? Predict(string sql) => ResultSources.Predict(sql, Sales);

    [Fact]
    public void Star_expands_to_the_table_columns_in_order()
    {
        var set = Assert.Single(Predict("SELECT TOP (1000) * FROM [dbo].[Orders];")!)!;
        Assert.Equal(["OrderId", "CustomerId", "OrderDate", "TotalAmount", "Status"], set.Select(c => c.Name));
        Assert.All(set, c => Assert.Equal(1, c.Origin!.ObjectId));
    }

    [Fact]
    public void Aliased_join_columns_resolve_and_expressions_do_not()
    {
        var set = Assert.Single(Predict("SELECT o.OrderId, c.FullName AS Name, OrderDate, CustomerId, 1 + 1 AS x, COUNT(*) FROM Orders o JOIN dbo.Customers AS c ON c.CustomerId = o.CustomerId GROUP BY o.OrderId, c.FullName, OrderDate, CustomerId")!)!;
        Assert.Equal(new ColumnOrigin(1, "OrderId"), set[0].Origin);
        Assert.Equal(("Name", new ColumnOrigin(2, "FullName")), (set[1].Name, set[1].Origin));
        Assert.Equal(new ColumnOrigin(1, "OrderDate"), set[2].Origin); // only Orders has it
        Assert.Null(set[3].Origin); // CustomerId is in both tables
        Assert.Equal(("x", (ColumnOrigin?)null), (set[4].Name, set[4].Origin));
        Assert.Equal((null, (ColumnOrigin?)null), (set[5].Name, set[5].Origin));
    }

    [Fact]
    public void Only_row_returning_statements_count_as_result_sets()
    {
        var sets = Predict("""
            DECLARE @x int;
            SELECT @x = 1;
            UPDATE Orders SET Status = 'a';
            SELECT * INTO #copy FROM Orders;
            SELECT OrderId FROM Orders;
            SELECT 1 UNION SELECT 2;
            """)!;
        Assert.Equal(2, sets.Count);
        Assert.Equal(new ColumnOrigin(1, "OrderId"), Assert.Single(sets[0]!).Origin);
        Assert.Null(sets[1]);
    }

    [Theory]
    [InlineData("EXEC dbo.usp_GetCustomerOrders 1; SELECT * FROM Orders")]
    [InlineData("IF 1 = 1 SELECT * FROM Orders")]
    [InlineData("DELETE FROM Orders OUTPUT deleted.OrderId; SELECT * FROM Orders")]
    [InlineData("SELECT * FROM Orders WHERE")]
    public void Batches_that_might_reorder_result_sets_are_not_predicted(string sql) => Assert.Null(Predict(sql));

    [Fact]
    public void Ctes_derived_tables_and_temp_tables_are_opaque()
    {
        Assert.Null(Assert.Single(Predict("WITH Orders AS (SELECT 1 AS n) SELECT * FROM Orders")!));
        var set = Assert.Single(Predict("SELECT d.n, o.OrderId, Status FROM (SELECT 1 AS n) d CROSS JOIN Orders o")!)!;
        Assert.Null(set[0].Origin);
        Assert.Equal(new ColumnOrigin(1, "OrderId"), set[1].Origin);
        Assert.Null(set[2].Origin); // unqualified with an opaque source in scope: could be d.Status
        Assert.Null(Assert.Single(Predict("SELECT * FROM #t")!));
    }

    [Fact]
    public void Unqualified_names_need_dbo_or_a_unique_match()
    {
        Assert.Equal(new ColumnOrigin(3, "Name"), Assert.Single(Assert.Single(Predict("SELECT Name FROM Regions")!)!).Origin);
        Assert.Null(Assert.Single(Assert.Single(Predict("SELECT Name FROM dbo.Regions")!)!).Origin);
    }
}
