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

    // ------------------------------------------------------------------------------- database-qualified names

    [Theory]
    // SSMS "Select Top 1000 Rows" writes the database name in front: the tab's own database is this snapshot.
    [InlineData("SELECT TOP (1000) [OrderId], [Status] FROM SalesDB.[dbo].[Orders];")]
    [InlineData("SELECT OrderId, Status FROM [SalesDB].[dbo].[Orders]")]
    // SQL Server compares database names through the server collation; the snapshot's lookups are case-insensitive too.
    [InlineData("SELECT OrderId, Status FROM salesdb.dbo.orders")]
    [InlineData("SELECT o.OrderId, o.Status FROM SalesDB.dbo.Orders AS o")]
    [InlineData("SELECT SalesDB.dbo.Orders.OrderId, Orders.Status FROM SalesDB.dbo.Orders")]
    // db..table names the default schema: resolved like an unqualified name.
    [InlineData("SELECT OrderId, Status FROM SalesDB..Orders")]
    public void A_name_qualified_with_the_tabs_own_database_resolves_like_schema_dot_table(string sql)
    {
        var set = Assert.Single(Predict(sql)!)!;
        Assert.Equal([new ColumnOrigin(1, "OrderId"), new ColumnOrigin(1, "Status")], set.Select(c => c.Origin));
        Assert.Null(ResultSources.Refusal(sql, Sales, 0));
    }

    [Fact]
    public void Star_over_the_tabs_own_database_expands_to_the_table_columns()
    {
        var set = Assert.Single(Predict("SELECT * FROM SalesDB.dbo.Orders")!)!;
        Assert.Equal(["OrderId", "CustomerId", "OrderDate", "TotalAmount", "Status"], set.Select(c => c.Name));
        Assert.All(set, c => Assert.Equal(1, c.Origin!.ObjectId));
    }

    [Theory]
    [InlineData("SELECT OrderId, Status FROM OtherDb.dbo.Orders", "OtherDb.dbo.Orders is in database OtherDb, not this tab's database (SalesDB) — switch the tab to OtherDb to edit it")]
    [InlineData("SELECT * FROM [OtherDb].[dbo].[Orders]", "OtherDb.dbo.Orders is in database OtherDb, not this tab's database (SalesDB) — switch the tab to OtherDb to edit it")]
    [InlineData("SELECT OrderId FROM OtherDb..Orders", "OtherDb..Orders is in database OtherDb, not this tab's database (SalesDB) — switch the tab to OtherDb to edit it")]
    // A linked server is another server even when its database has this tab's name.
    [InlineData("SELECT OrderId FROM Remote.SalesDB.dbo.Orders", "Remote.SalesDB.dbo.Orders is on linked server Remote")]
    public void Another_database_or_a_linked_server_is_read_only_and_says_why(string sql, string reason)
    {
        // Same schema and table name as the tab's own dbo.Orders: it must never resolve to it.
        var set = Assert.Single(Predict(sql)!);
        Assert.True(set == null || set.All(c => c.Origin == null));
        Assert.Equal(reason, ResultSources.Refusal(sql, Sales, 0));
        Assert.Equal(reason, new Providers.SqlServer.SqlServerProvider().ResultEditRefusal(sql, 0, Sales));
    }

    [Fact]
    public void A_join_of_this_database_and_another_keeps_this_databases_columns_and_locks_the_rest()
    {
        const string sql = "SELECT o.OrderId, o.Status, x.Status AS OtherStatus FROM SalesDB.dbo.Orders o JOIN OtherDb.dbo.Orders x ON x.OrderId = o.OrderId";
        var set = Assert.Single(Predict(sql)!)!;
        Assert.Equal([new ColumnOrigin(1, "OrderId"), new ColumnOrigin(1, "Status"), null], set.Select(c => c.Origin));
        Assert.Null(ResultSources.Refusal(sql, Sales, 0));
    }

    [Fact]
    public void The_tabs_database_named_and_unnamed_is_still_a_self_join()
    {
        const string sql = "SELECT a.OrderId, b.Status FROM SalesDB.dbo.Orders a JOIN dbo.Orders b ON b.OrderId = a.OrderId + 1";
        Assert.Null(Assert.Single(Predict(sql)!));
        Assert.Equal("the same table (dbo.Orders) appears more than once in the query", ResultSources.Refusal(sql, Sales, 0));
    }

    [Fact]
    public void Db_dot_dot_table_still_refuses_a_name_in_several_schemas()
    {
        var snap = Sales with
        {
            Objects = [.. Sales.Objects, new Contract.SnapObject
            {
                Id = 90, Schema = "audit", Name = "Orders", Kind = Contract.ObjectKind.Table,
                Columns = [new Contract.SnapColumn { Name = "OrderId", Type = "bigint", Pk = true }],
            }],
        };
        Assert.Equal("table Orders exists in more than one schema (ambiguous schema — qualify the table name)",
            ResultSources.Refusal("SELECT OrderId FROM SalesDB..Orders", snap, 0));
    }

    [Fact]
    public void A_tempdb_temp_table_stays_opaque()
    {
        Assert.Null(Assert.Single(Predict("SELECT * FROM tempdb..#t")!));
        Assert.Null(ResultSources.Refusal("SELECT * FROM tempdb..#t", Sales, 0));
    }

    /// <summary>
    /// The user's case end to end through the analyzer: the SSMS-style three-part name is editable exactly like the two-part
    /// one, with the same key, table and columns — and another database's table of the same name is read-only.
    /// </summary>
    [Fact]
    public void The_three_part_name_of_this_database_is_editable_like_the_two_part_name()
    {
        Contract.ResultEditInfo Edit(string sql)
        {
            var set = Assert.Single(Predict(sql)!);
            var columns = (set ?? []).Select((c, i) => new Contract.ResultColumn { Name = c.Name ?? $"c{i}", TypeName = "x", ArrowType = "utf8", Ordinal = i }).ToArray();
            return Core.Execution.EditabilityAnalyzer.Analyze(new Providers.SqlServer.SqlServerDataEdit(), columns, set?.Select(c => c.Origin).ToArray(), Sales, _ => []);
        }
        var two = Edit("SELECT TOP (1000) [OrderId], [CustomerId], [Status] FROM [dbo].[Orders]");
        var three = Edit("SELECT TOP (1000) [OrderId], [CustomerId], [Status] FROM SalesDB.[dbo].[Orders]");
        Assert.True(two.Editable, two.Reason);
        Assert.True(three.Editable, three.Reason);
        Assert.Equal((two.ObjectId, two.Schema, two.Table), (three.ObjectId, three.Schema, three.Table));
        Assert.Equal(two.KeyColumns, three.KeyColumns);
        Assert.Equal(two.Columns.Select(c => (c.Column, c.Locked)), three.Columns.Select(c => (c.Column, c.Locked)));
        Assert.False(three.Columns.Single(c => c.Column == "Status").Locked);
    }
}
