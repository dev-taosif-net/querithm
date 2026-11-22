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

    // ------------------------------------------------------------------------------- batches: positional prediction

    [Fact]
    public void A_batch_that_does_not_parse_predicts_nothing_and_says_so()
    {
        Assert.Null(Predict("SELECT * FROM Orders WHERE"));
        var traced = ResultSources.Trace("SELECT * FROM Orders WHERE", Sales, 0);
        Assert.Null(traced.Columns);
        Assert.False(traced.Stale);
        Assert.StartsWith("Querithm could not parse this batch", traced.Why);
    }

    [Theory]
    [InlineData("EXEC dbo.usp_GetCustomerOrders 1; SELECT * FROM Orders", "EXEC")]
    [InlineData("EXECUTE dbo.usp_GetCustomerOrders 1; SELECT * FROM Orders", "EXECUTE")]
    [InlineData("IF 1 = 1 SELECT * FROM Orders", "IF")]
    [InlineData("WHILE 1 = 0 BEGIN SELECT 1 END; SELECT * FROM Orders", "WHILE")]
    [InlineData("BEGIN TRY SELECT 1 / 0 END TRY BEGIN CATCH SELECT ERROR_MESSAGE() END CATCH; SELECT * FROM Orders", "BEGIN TRY")]
    [InlineData("DBCC CHECKIDENT ('dbo.Orders'); SELECT * FROM Orders", "DBCC")]
    [InlineData("again: SELECT 1; IF 1 = 0 GOTO again; SELECT * FROM Orders", "IF")]
    public void Result_sets_after_a_statement_that_may_return_any_number_are_not_predicted_and_name_it(string sql, string keyword)
    {
        var sets = Predict(sql)!;
        var after = ResultSources.Trace(sql, Sales, sets.Count);
        Assert.Null(after.Columns);
        Assert.False(after.Stale); // nothing a metadata refresh would change
        Assert.Equal($"this result comes after {keyword} in the same batch, so Querithm cannot tell which statement returned it — run the SELECT on its own to edit its rows", after.Why);
    }

    [Fact]
    public void Result_sets_before_an_unpredictable_statement_keep_their_origins()
    {
        // The user's case: a SELECT with an EXEC further down in the same batch used to lose its edit option.
        const string sql = "SELECT * FROM dbo.Orders; EXEC dbo.usp_GetCustomerOrders 1; SELECT * FROM dbo.Customers";
        var sets = Predict(sql)!;
        var first = Assert.Single(sets)!;
        Assert.All(first, c => Assert.Equal(1, c.Origin!.ObjectId));
        Assert.NotNull(ResultSources.Trace(sql, Sales, 0).Columns);
        Assert.Contains("after EXEC", ResultSources.Trace(sql, Sales, 1).Why);
    }

    [Theory]
    [InlineData("IF OBJECT_ID('tempdb..#t') IS NOT NULL DROP TABLE #t; SELECT * FROM dbo.Orders")]
    [InlineData("IF 1 = 1 BEGIN UPDATE Orders SET Status = 'a' END ELSE BEGIN DELETE FROM Orders WHERE 1 = 0 END; SELECT * FROM dbo.Orders")]
    [InlineData("WHILE @@ROWCOUNT > 0 BEGIN DELETE TOP (10) FROM Orders WHERE 1 = 0 END; SELECT * FROM dbo.Orders")]
    [InlineData("BEGIN TRY UPDATE Orders SET Status = 'a' END TRY BEGIN CATCH THROW; END CATCH; SELECT * FROM dbo.Orders")]
    [InlineData("TRUNCATE TABLE dbo.Customers; SELECT * FROM dbo.Orders")]
    [InlineData("WAITFOR DELAY '00:00:00'; SELECT * FROM dbo.Orders")]
    [InlineData("GRANT SELECT ON dbo.Orders TO public; SELECT * FROM dbo.Orders")]
    [InlineData("UPDATE STATISTICS dbo.Orders; SELECT * FROM dbo.Orders")]
    [InlineData("CHECKPOINT; SELECT * FROM dbo.Orders")]
    [InlineData("DECLARE c CURSOR FOR SELECT OrderId FROM Orders; OPEN c; DECLARE @id bigint; FETCH NEXT FROM c INTO @id; CLOSE c; DEALLOCATE c; SELECT * FROM dbo.Orders")]
    [InlineData("BEGIN SELECT * FROM dbo.Orders END")]
    [InlineData("SET NOCOUNT ON; DECLARE @x int = 1; PRINT 'x'; SELECT * FROM dbo.Orders")]
    public void Statements_that_return_no_rows_do_not_cost_the_select_its_origins(string sql)
    {
        var set = Assert.Single(Predict(sql)!)!;
        Assert.Equal(["OrderId", "CustomerId", "OrderDate", "TotalAmount", "Status"], set.Select(c => c.Name));
        Assert.All(set, c => Assert.Equal(1, c.Origin!.ObjectId));
    }

    [Fact]
    public void An_output_clause_or_a_cursor_fetch_is_one_untraced_result_set_and_the_next_select_keeps_its_place()
    {
        var sets = Predict("DELETE FROM Orders OUTPUT deleted.OrderId; SELECT * FROM dbo.Orders")!;
        Assert.Equal(2, sets.Count);
        Assert.Null(sets[0]);
        Assert.All(sets[1]!, c => Assert.Equal(1, c.Origin!.ObjectId));
        Assert.Equal("the rows come from an OUTPUT clause, not from a table", ResultSources.Trace("DELETE FROM Orders OUTPUT deleted.OrderId", Sales, 0).Why);
        // OUTPUT … INTO returns no rows.
        Assert.Single(Predict("DECLARE @t TABLE (id bigint); DELETE FROM Orders OUTPUT deleted.OrderId INTO @t; SELECT * FROM dbo.Orders")!);

        var fetch = Predict("DECLARE c CURSOR FOR SELECT OrderId FROM Orders; OPEN c; FETCH NEXT FROM c; SELECT * FROM dbo.Orders")!;
        Assert.Equal(2, fetch.Count);
        Assert.Null(fetch[0]);
    }

    [Fact]
    public void A_return_ends_the_batch_and_what_follows_is_never_a_result_set()
    {
        const string sql = "SELECT * FROM dbo.Orders; RETURN; SELECT * FROM dbo.Customers";
        Assert.Single(Predict(sql)!);
        var beyond = ResultSources.Trace(sql, Sales, 1);
        Assert.Null(beyond.Columns);
        Assert.StartsWith("the batch returned more result sets than its statements account for", beyond.Why);
    }

    // ------------------------------------------------------------------------------- the user's query, and stale metadata

    [Theory]
    [InlineData("SELECT TOP 10 * FROM sales.Regions r")]
    [InlineData("SELECT TOP 10 * FROM sales.Regions AS r")]
    [InlineData("SELECT TOP (10) r.* FROM [sales].[Regions] r")]
    [InlineData("select top 10 * from SALES.REGIONS R;")]
    [InlineData("SELECT TOP 10 * FROM sales.Regions r -- trailing comment")]
    [InlineData("/* lead */ SELECT TOP 10 * FROM sales.Regions r WITH (NOLOCK) WHERE r.RegionId > 0 ORDER BY r.RegionId OPTION (RECOMPILE)")]
    public void Top_n_star_from_a_schema_qualified_table_with_an_alias_traces_every_column(string sql)
    {
        var traced = ResultSources.Trace(sql, Sales, 0);
        Assert.Equal([new ColumnOrigin(3, "RegionId"), new ColumnOrigin(3, "Name")], traced.Columns!.Select(c => c.Origin));
        Assert.Null(traced.Why);
        Assert.False(traced.Stale);
        Assert.Equal("sales.Regions", traced.Tables);
        Assert.Null(ResultSources.Refusal(sql, Sales, 0));
    }

    [Theory]
    [InlineData("SELECT TOP 10 * FROM saas.empEmployeeBasicInfo ebi", "saas.empEmployeeBasicInfo")]
    [InlineData("SELECT * FROM NewTable", "NewTable")]
    [InlineData("SELECT n.* FROM dbo.NewTable n", "dbo.NewTable")]
    public void Star_from_a_table_the_snapshot_does_not_hold_is_stale_not_final(string sql, string table)
    {
        // The table exists on the server (the query returned rows) but was created after the snapshot was read: newer
        // metadata traces it, so the caller reads the metadata again instead of leaving the result read-only.
        var traced = ResultSources.Trace(sql, Sales, 0);
        Assert.Null(traced.Columns);
        Assert.True(traced.Stale);
        Assert.Equal($"{table} is not in this database's cached metadata", traced.Why);
    }

    [Fact]
    public void Named_columns_of_a_table_the_snapshot_does_not_hold_are_stale_too()
    {
        var traced = ResultSources.Trace("SELECT Id, Title FROM dbo.NewTable", Sales, 0);
        Assert.Equal([null, null], traced.Columns!.Select(c => c.Origin));
        Assert.True(traced.Stale);
        Assert.Equal("dbo.NewTable is not in this database's cached metadata", traced.Why);
    }

    [Fact]
    public void A_table_listed_without_its_columns_is_stale()
    {
        var snap = Sales with { Objects = [.. Sales.Objects, new Contract.SnapObject { Id = 70, Schema = "dbo", Name = "Bare", Kind = Contract.ObjectKind.Table }] };
        var traced = ResultSources.Trace("SELECT * FROM dbo.Bare", snap, 0);
        Assert.Null(traced.Columns);
        Assert.True(traced.Stale);
        Assert.Equal("the columns of dbo.Bare are not in this database's cached metadata", traced.Why);
    }

    [Theory]
    // What no metadata refresh would ever add: system views, temporary tables, CTEs, derived tables, functions.
    [InlineData("SELECT * FROM sys.objects")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES")]
    [InlineData("SELECT * FROM sysobjects")]
    [InlineData("SELECT * FROM #t")]
    [InlineData("WITH x AS (SELECT 1 AS n) SELECT * FROM x")]
    [InlineData("SELECT * FROM (SELECT 1 AS n) d")]
    [InlineData("SELECT * FROM dbo.tvf_OrdersFor(1)")]
    [InlineData("SELECT * FROM dbo.tvf_OrdersFor")] // a name the snapshot knows as something else than a table
    public void Sources_that_are_not_tables_are_untraced_but_not_stale(string sql)
    {
        var traced = ResultSources.Trace(sql, Sales, 0);
        Assert.Null(traced.Columns);
        Assert.False(traced.Stale);
        Assert.NotNull(traced.Why);
    }

    // ------------------------------------------------------------------------------- the provider's check against the real result

    sealed class Col : System.Data.Common.DbColumn
    {
        public Col(string name) => ColumnName = name;
    }

    static List<System.Data.Common.DbColumn> Cols(params string[] names) => names.Select(n => (System.Data.Common.DbColumn)new Col(n)).ToList();

    [Fact]
    public void The_provider_returns_the_origins_when_the_result_has_the_predicted_columns()
    {
        var p = new Providers.SqlServer.SqlServerProvider();
        var traced = p.ResultOrigins(Cols("regionid", "NAME"), "SELECT TOP 10 * FROM sales.Regions r", 0, Sales, null);
        Assert.Equal([new ColumnOrigin(3, "RegionId"), new ColumnOrigin(3, "Name")], traced.Columns);
        Assert.False(traced.Stale);
        Assert.Null(p.ResultOrigins(Cols("x"), "SELECT 1 AS x", 0, null, null).Columns); // no snapshot: nothing traced
    }

    [Theory]
    [InlineData("RegionId", "Name", "AddedLater")] // a column added since the snapshot was read
    [InlineData("RegionId")] // a column dropped since
    [InlineData("RegionId", "Title")] // a column renamed since
    public void A_result_whose_columns_differ_from_the_cached_ones_is_stale_and_names_the_table(params string[] actual)
    {
        var traced = new Providers.SqlServer.SqlServerProvider().ResultOrigins(Cols(actual), "SELECT TOP 10 * FROM sales.Regions r", 0, Sales, null);
        Assert.Null(traced.Columns);
        Assert.True(traced.Stale);
        Assert.Equal("the columns this query returned are not the ones cached for sales.Regions — name the columns instead of * if the table has hidden columns", traced.Why);
    }

    [Fact]
    public void The_provider_passes_on_why_a_later_result_set_is_untraced()
    {
        var traced = new Providers.SqlServer.SqlServerProvider().ResultOrigins(Cols("OrderId"), "EXEC dbo.usp_GetCustomerOrders 1; SELECT OrderId FROM Orders", 0, Sales, null);
        Assert.Null(traced.Columns);
        Assert.False(traced.Stale);
        Assert.Contains("after EXEC", traced.Why);
    }

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
