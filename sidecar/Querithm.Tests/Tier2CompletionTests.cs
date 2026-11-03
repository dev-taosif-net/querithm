using System.Diagnostics;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql;
using Querithm.Intelligence.TSql.Completion;
using Xunit.Abstractions;

namespace Querithm.Tests;

sealed class FakeSchema(SchemaSnapshot? snap, params string[] databases) : ISchemaSource
{
    public SchemaSnapshot? Get(string? profileId, string? database) => profileId == null ? null : snap;
    public IReadOnlyList<string> Databases(string? profileId) => databases;

    public static SchemaSnapshot SalesDb() => new()
    {
        ProfileId = "p1", Database = "SalesDB", Status = SnapshotStatus.Fresh, Fks = [],
        Objects =
        [
            new SnapObject
            {
                Id = 1, Schema = "dbo", Name = "Orders", Kind = ObjectKind.Table,
                Columns =
                [
                    new SnapColumn { Name = "OrderId", Type = "bigint", Pk = true, Identity = true },
                    new SnapColumn { Name = "CustomerId", Type = "int" },
                    new SnapColumn { Name = "OrderDate", Type = "datetime2" },
                    new SnapColumn { Name = "TotalAmount", Type = "decimal(18,2)" },
                    new SnapColumn { Name = "Status", Type = "nvarchar(20)", Nullable = true },
                ],
            },
            new SnapObject
            {
                Id = 2, Schema = "dbo", Name = "Customers", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "CustomerId", Type = "int", Pk = true }, new SnapColumn { Name = "FullName", Type = "nvarchar(200)" }],
            },
            new SnapObject
            {
                Id = 3, Schema = "sales", Name = "Regions", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "RegionId", Type = "int" }, new SnapColumn { Name = "Name", Type = "nvarchar(50)" }],
            },
            new SnapObject
            {
                Id = 4, Schema = "dbo", Name = "usp_GetCustomerOrders", Kind = ObjectKind.Proc,
                Params =
                [
                    new SnapParam { Name = "@CustomerId", Type = "int", Ordinal = 1 },
                    new SnapParam { Name = "@StartDate", Type = "datetime2", Ordinal = 2 },
                    new SnapParam { Name = "@MinTotal", Type = "decimal(18,2)", Ordinal = 3, Output = true },
                ],
            },
            new SnapObject
            {
                Id = 5, Schema = "dbo", Name = "fn_CustomerTier", Kind = ObjectKind.Func, ReturnType = "nvarchar(10)",
                Params = [new SnapParam { Name = "@CustomerId", Type = "int", Ordinal = 1 }, new SnapParam { Name = "@AsOf", Type = "date", Ordinal = 2 }],
            },
            new SnapObject
            {
                Id = 6, Schema = "dbo", Name = "tvf_OrdersFor", Kind = ObjectKind.Tvf,
                Params = [new SnapParam { Name = "@CustomerId", Type = "int", Ordinal = 1 }],
                Columns = [new SnapColumn { Name = "OrderId", Type = "bigint" }, new SnapColumn { Name = "Total", Type = "money" }],
            },
            new SnapObject
            {
                Id = 7, Schema = "dbo", Name = "vw_OpenOrders", Kind = ObjectKind.View,
                Columns = [new SnapColumn { Name = "OrderId", Type = "bigint" }],
            },
        ],
    };
}

/// <summary>
/// Wall-clock latency gates run alone: with the monitor's integration tests in parallel collections, the 12k-line proc's
/// p95 rose from &lt; 150 ms (run alone, twice) to 245 ms on the dev box — CPU contention, not the completion path.
/// </summary>
[CollectionDefinition("timing", DisableParallelization = true)]
public class TimingCollection;

[Collection("timing")]
public class Tier2CompletionTests(ITestOutputHelper output)
{
    static readonly ISchemaSource Sales = new FakeSchema(FakeSchema.SalesDb(), "SalesDB", "master");
    static readonly ISchemaSource NoSchema = new FakeSchema(null);

    static (string Text, int Offset) Mark(string sql)
    {
        var i = sql.IndexOf('|');
        Assert.True(i >= 0, "test SQL needs a | cursor marker");
        return (sql.Remove(i, 1), i);
    }

    static CompletionItem[] Complete(string sql, ISchemaSource? schema = null, string? profile = "p1")
    {
        var (text, offset) = Mark(sql);
        return TSqlCompletion.Complete(new CompleteParams { DocId = "t2-" + Guid.NewGuid(), Text = text, Offset = offset, Dialect = Dialect.Tsql, ProfileId = profile, Database = "SalesDB" }, schema ?? Sales);
    }

    static SignatureHelp Sig(string sql)
    {
        var (text, offset) = Mark(sql);
        return TSqlSignature.Help(new SignatureParams { DocId = "t2s-" + Guid.NewGuid(), Text = text, Offset = offset, Dialect = Dialect.Tsql, ProfileId = "p1", Database = "SalesDB" }, Sales);
    }

    static CompletionItem Item(CompletionItem[] items, string label)
    {
        var hit = items.FirstOrDefault(i => i.Label == label);
        Assert.True(hit != null, $"expected '{label}' in [{string.Join(", ", items.Select(i => i.Label).Take(60))}]");
        return hit!;
    }

    static void HasColumn(CompletionItem[] items, string label, string? type)
    {
        var i = Item(items, label);
        Assert.Equal(CompletionKind.Column, i.Kind);
        Assert.Equal(type, i.Detail);
    }

    [Fact]
    public void Cte_projection_columns_with_inferred_types_via_alias()
    {
        var items = Complete("""
            WITH recent AS (
              SELECT o.OrderId, o.TotalAmount AS Amount, CAST(o.OrderDate AS date) AS Day, 1 AS One, N'x' AS Tag
              FROM dbo.Orders o WHERE o.Status = N'Open'
            )
            SELECT r.| FROM recent r
            """);
        HasColumn(items, "OrderId", "bigint");
        HasColumn(items, "Amount", "decimal(18,2)");
        HasColumn(items, "Day", "date");
        HasColumn(items, "One", "int");
        HasColumn(items, "Tag", "nvarchar(1)");
        Assert.DoesNotContain(items, i => i.Label == "CustomerId");
    }

    [Fact]
    public void Cte_explicit_column_list_renames_projection()
    {
        var items = Complete("WITH t (Id, Total) AS (SELECT OrderId, TotalAmount FROM dbo.Orders)\nSELECT t.| FROM t");
        HasColumn(items, "Id", "bigint");
        HasColumn(items, "Total", "decimal(18,2)");
    }

    [Fact]
    public void Derived_table_columns()
    {
        var items = Complete("SELECT d.| FROM (SELECT CustomerId, COUNT(*) AS N, SUM(TotalAmount) AS Total, 'x' AS Tag FROM dbo.Orders GROUP BY CustomerId) AS d");
        HasColumn(items, "CustomerId", "int");
        HasColumn(items, "N", "int");
        HasColumn(items, "Total", "decimal(18,2)");
        HasColumn(items, "Tag", "varchar(1)");
    }

    [Fact]
    public void Select_star_in_cte_expands_to_underlying_columns()
    {
        var items = Complete("WITH c AS (SELECT * FROM dbo.Customers), oc AS (SELECT o.*, c.FullName FROM dbo.Orders o JOIN c ON c.CustomerId = o.CustomerId)\nSELECT x.| FROM oc x");
        HasColumn(items, "OrderId", "bigint");
        HasColumn(items, "Status", "nvarchar(20)");
        HasColumn(items, "FullName", "nvarchar(200)");
        var cte = Complete("WITH c AS (SELECT * FROM dbo.Customers)\nSELECT c.| FROM c");
        HasColumn(cte, "CustomerId", "int");
        HasColumn(cte, "FullName", "nvarchar(200)");
    }

    [Fact]
    public void Temp_table_columns_and_from_context()
    {
        const string decl = "CREATE TABLE #work (Id int NOT NULL PRIMARY KEY, Amount decimal(18,2), Note nvarchar(100));\nINSERT INTO #work (Id) VALUES (1);\n";
        var items = Complete(decl + "SELECT w.| FROM #work w");
        HasColumn(items, "Id", "int");
        HasColumn(items, "Amount", "decimal(18,2)");
        HasColumn(items, "Note", "nvarchar(100)");
        var from = Complete(decl + "SELECT * FROM |");
        Assert.Equal(CompletionKind.TempTable, Item(from, "#work").Kind);
        Assert.Equal(CompletionKind.Table, Item(from, "Orders").Kind);
        Assert.Equal(CompletionKind.View, Item(from, "vw_OpenOrders").Kind);
        Item(from, "sales.Regions");
    }

    [Fact]
    public void Temp_table_from_an_earlier_batch_is_visible()
    {
        var items = Complete("CREATE TABLE #t (A int, B date)\nGO\nSELECT x.| FROM #t x");
        HasColumn(items, "A", "int");
        HasColumn(items, "B", "date");
    }

    [Fact]
    public void Select_into_temp_table_projection()
    {
        var items = Complete("SELECT o.OrderId, o.Status, GETDATE() AS LoadedAt INTO #s FROM dbo.Orders o;\nSELECT s.| FROM #s s");
        HasColumn(items, "OrderId", "bigint");
        HasColumn(items, "Status", "nvarchar(20)");
        HasColumn(items, "LoadedAt", "datetime");
    }

    [Fact]
    public void Table_variable_columns()
    {
        var items = Complete("DECLARE @t TABLE (Id int, Name nvarchar(50));\nINSERT INTO @t VALUES (1, N'a');\nSELECT t.| FROM @t AS t");
        HasColumn(items, "Id", "int");
        HasColumn(items, "Name", "nvarchar(50)");
        var from = Complete("DECLARE @t TABLE (Id int);\nSELECT * FROM |");
        Item(from, "@t");
    }

    [Fact]
    public void Output_clause_inserted_and_deleted()
    {
        var upd = Complete("UPDATE dbo.Orders SET Status = N'Closed'\nOUTPUT deleted.|, inserted.Status\nWHERE OrderId = 1");
        HasColumn(upd, "Status", "nvarchar(20)");
        HasColumn(upd, "TotalAmount", "decimal(18,2)");
        var ins = Complete("INSERT INTO dbo.Orders (CustomerId, OrderDate) OUTPUT inserted.| VALUES (1, SYSDATETIME())");
        HasColumn(ins, "OrderId", "bigint");
        var unq = Complete("DELETE FROM dbo.Orders OUTPUT | WHERE OrderId = 1");
        Item(unq, "deleted.OrderId");
        Assert.DoesNotContain(unq, i => i.Label == "inserted.OrderId");
    }

    [Fact]
    public void Merge_source_and_target_aliases_and_output()
    {
        const string merge = """
            MERGE dbo.Orders AS t
            USING (SELECT CustomerId, CAST(5 AS money) AS Amt FROM dbo.Customers) AS s ON t.CustomerId = s.CustomerId
            WHEN MATCHED THEN UPDATE SET t.TotalAmount = s.Amt
            WHEN NOT MATCHED THEN INSERT (CustomerId, TotalAmount) VALUES (s.CustomerId, s.Amt)
            OUTPUT $action, inserted.OrderId;
            """;
        var src = Complete(merge.Replace("SET t.TotalAmount = s.Amt", "SET t.TotalAmount = s.|"));
        HasColumn(src, "CustomerId", "int");
        HasColumn(src, "Amt", "money");
        var tgt = Complete(merge.Replace("SET t.TotalAmount", "SET t.|"));
        HasColumn(tgt, "TotalAmount", "decimal(18,2)");
        var outp = Complete(merge.Replace("OUTPUT $action, inserted.OrderId", "OUTPUT $action, inserted.|"));
        HasColumn(outp, "OrderId", "bigint");
        var on = Complete(merge.Replace("ON t.CustomerId = s.CustomerId", "ON |"));
        Item(on, "t.OrderId");
        Item(on, "s.Amt");
        var cols = Complete(merge.Replace("INSERT (CustomerId, TotalAmount)", "INSERT (CustomerId, |)"));
        HasColumn(cols, "TotalAmount", "decimal(18,2)");
        Assert.DoesNotContain(cols, i => i.Label == "CustomerId");
    }

    [Fact]
    public void Cross_apply_operand_columns_and_correlation()
    {
        const string sql = """
            SELECT x.| FROM dbo.Customers c
            CROSS APPLY (SELECT TOP 1 o.OrderDate AS LastDate, o.TotalAmount FROM dbo.Orders o WHERE o.CustomerId = c.CustomerId ORDER BY o.OrderDate DESC) x
            OUTER APPLY dbo.tvf_OrdersFor(c.CustomerId) f
            """;
        var items = Complete(sql);
        HasColumn(items, "LastDate", "datetime2");
        HasColumn(items, "TotalAmount", "decimal(18,2)");
        var tvf = Complete(sql.Replace("SELECT x.|", "SELECT f.|"));
        HasColumn(tvf, "Total", "money");
        var inner = Complete(sql.Replace("SELECT x.|", "SELECT x.LastDate").Replace("o.CustomerId = c.CustomerId", "o.CustomerId = c.|"));
        HasColumn(inner, "FullName", "nvarchar(200)");
    }

    [Fact]
    public void Columns_of_all_sources_are_alias_prefixed()
    {
        var items = Complete("SELECT | FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId");
        HasColumn(items, "o.OrderId", "bigint");
        HasColumn(items, "c.FullName", "nvarchar(200)");
        Assert.Equal(CompletionKind.Alias, Item(items, "o").Kind);
        Assert.Equal(CompletionKind.Func, Item(items, "dbo.fn_CustomerTier").Kind);
        var single = Complete("SELECT | FROM dbo.Orders");
        HasColumn(single, "OrderId", "bigint");
    }

    [Fact]
    public void Incomplete_predicates_and_trailing_garbage_still_resolve()
    {
        var where = Complete("SELECT * FROM dbo.Orders o WHERE |");
        HasColumn(where, "OrderId", "bigint");
        var and = Complete("SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId WHERE o.Status = N'x' AND c.|");
        HasColumn(and, "FullName", "nvarchar(200)");
        var broken = Complete("SELECT o.| FROM dbo.Orders o WHERE o.Status =");
        HasColumn(broken, "Status", "nvarchar(20)");
        var partial = Complete("SELECT o.Cu| FROM dbo.Orders o");
        HasColumn(partial, "CustomerId", "int");
        var bracket = Complete("SELECT o.[Tot| FROM dbo.Orders o");
        HasColumn(bracket, "TotalAmount", "decimal(18,2)");
        var subq = Complete("SELECT * FROM dbo.Customers c WHERE c.CustomerId IN (SELECT o.| FROM dbo.Orders o");
        HasColumn(subq, "CustomerId", "int");
        var update = Complete("UPDATE o SET | FROM dbo.Orders o WHERE o.OrderId = 1");
        Item(update, "OrderId");
    }

    [Fact]
    public void Procedure_body_parameters_variables_and_cursors()
    {
        const string proc = """
            CREATE OR ALTER PROCEDURE dbo.usp_Test
              @CustomerId int,
              @Since datetime2 = NULL,
              @Total decimal(18,2) OUTPUT
            AS
            BEGIN
              SET NOCOUNT ON;
              DECLARE @count int = 0, @label nvarchar(40);
              DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT OrderId FROM dbo.Orders WHERE CustomerId = @CustomerId;
              IF @count = 0
              BEGIN
                SELECT * FROM dbo.Orders WHERE CustomerId = |
              END
            END
            """;
        var items = Complete(proc);
        var p = Item(items, "@CustomerId");
        Assert.Equal(CompletionKind.Parameter, p.Kind);
        Assert.Equal("int", p.Detail);
        Assert.Equal("decimal(18,2) OUTPUT", Item(items, "@Total").Detail);
        Assert.Equal("datetime2", Item(items, "@Since").Detail);
        var v = Item(items, "@count");
        Assert.Equal(CompletionKind.Variable, v.Kind);
        Assert.Equal("int", v.Detail);
        Assert.Equal("nvarchar(40)", Item(items, "@label").Detail);
        HasColumn(items, "CustomerId", "int");

        var open = Complete(proc.Replace("SELECT * FROM dbo.Orders WHERE CustomerId = |", "OPEN |"));
        Assert.Equal(CompletionKind.Cursor, Item(open, "cur").Kind);
        var fetch = Complete(proc.Replace("SELECT * FROM dbo.Orders WHERE CustomerId = |", "OPEN cur; FETCH NEXT FROM |"));
        Item(fetch, "cur");
        var into = Complete(proc.Replace("SELECT * FROM dbo.Orders WHERE CustomerId = |", "FETCH NEXT FROM cur INTO |"));
        Item(into, "@count");

        // Variables declared after the cursor or in another batch are not in scope.
        var later = Complete("DECLARE @a int;\nSELECT |;\nDECLARE @b int;");
        Item(later, "@a");
        Assert.DoesNotContain(later, i => i.Label == "@b");
        var otherBatch = Complete("DECLARE @a int;\nGO\nSELECT |");
        Assert.DoesNotContain(otherBatch, i => i.Label == "@a");
    }

    [Fact]
    public void Function_parameters_and_returns_table_variable()
    {
        const string fn = """
            CREATE FUNCTION dbo.fn_Big(@Min money)
            RETURNS @r TABLE (Id int, Amt money)
            AS
            BEGIN
              INSERT INTO @r SELECT OrderId, TotalAmount FROM dbo.Orders WHERE TotalAmount > |
              RETURN
            END
            """;
        var items = Complete(fn);
        Assert.Equal("money", Item(items, "@Min").Detail);
        var cols = Complete(fn.Replace("INSERT INTO @r SELECT", "SELECT r.| FROM @r r; INSERT INTO @r SELECT").Replace("TotalAmount > |", "TotalAmount > 0"));
        HasColumn(cols, "Amt", "money");
    }

    [Fact]
    public void Trigger_inserted_pseudo_table()
    {
        var items = Complete("CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER UPDATE AS\nBEGIN\n  SELECT i.| FROM inserted i JOIN deleted d ON d.OrderId = i.OrderId\nEND");
        HasColumn(items, "TotalAmount", "decimal(18,2)");
    }

    [Fact]
    public void Pivot_and_unpivot_output_columns()
    {
        var pivot = Complete("SELECT p.| FROM (SELECT CustomerId, Status, TotalAmount FROM dbo.Orders) src PIVOT (SUM(TotalAmount) FOR Status IN ([Open], [Closed])) AS p");
        HasColumn(pivot, "CustomerId", "int");
        HasColumn(pivot, "Open", "decimal(18,2)");
        Assert.DoesNotContain(pivot, i => i.Label == "Status");
        var unpivot = Complete("SELECT u.| FROM (SELECT OrderId, CAST(1 AS int) AS A, CAST(2 AS int) AS B FROM dbo.Orders) s UNPIVOT (Val FOR Col IN (A, B)) AS u");
        HasColumn(unpivot, "Val", "int");
        HasColumn(unpivot, "Col", "nvarchar(128)");
        HasColumn(unpivot, "OrderId", "bigint");
    }

    [Fact]
    public void Exec_parameter_items_exclude_supplied_ones()
    {
        var items = Complete("EXEC dbo.usp_GetCustomerOrders @CustomerId = 5, |");
        Assert.DoesNotContain(items, i => i.Label == "@CustomerId" && i.Kind == CompletionKind.Parameter);
        var start = Item(items, "@StartDate");
        Assert.Equal(CompletionKind.Parameter, start.Kind);
        Assert.True(start.IsSnippet);
        Assert.Equal("@StartDate = ${1}", start.InsertText);
        Assert.Equal("datetime2", start.Detail);
        var min = Item(items, "@MinTotal");
        Assert.Equal("@MinTotal = ${1} OUTPUT", min.InsertText);
        Assert.Equal("decimal(18,2) OUTPUT", min.Detail);

        var first = Complete("EXEC usp_GetCustomerOrders |");
        Assert.Equal("@CustomerId = ${1}", Item(first, "@CustomerId").InsertText);
        var positional = Complete("EXEC dbo.usp_GetCustomerOrders 5, |");
        Assert.DoesNotContain(positional, i => i.Label == "@CustomerId");
        Item(positional, "@StartDate");
        var later = Complete("DECLARE @m decimal(18,2);\nEXEC dbo.usp_GetCustomerOrders |, @MinTotal = @m OUTPUT");
        Item(later, "@CustomerId");
        Assert.DoesNotContain(later, i => i.Label == "@MinTotal" && i.Kind == CompletionKind.Parameter);
        var value = Complete("DECLARE @c int;\nEXEC dbo.usp_GetCustomerOrders @CustomerId = |");
        Item(value, "@c");
        Assert.DoesNotContain(value, i => i.Kind == CompletionKind.Parameter);
        var procs = Complete("EXEC |");
        Assert.Equal(CompletionKind.Proc, Item(procs, "usp_GetCustomerOrders").Kind);
    }

    [Fact]
    public void Exec_defaults_come_from_a_create_procedure_in_the_document()
    {
        var items = Complete("CREATE PROCEDURE dbo.usp_GetCustomerOrders @CustomerId int, @StartDate datetime2 = '2020-01-01', @MinTotal decimal(18,2) OUTPUT AS SELECT 1\nGO\nEXEC dbo.usp_GetCustomerOrders |");
        Assert.Contains("default", Item(items, "@StartDate").Detail);
        var sig = Sig("CREATE PROCEDURE dbo.usp_GetCustomerOrders @CustomerId int, @StartDate datetime2 = '2020-01-01', @MinTotal decimal(18,2) OUTPUT AS SELECT 1\nGO\nEXEC dbo.usp_GetCustomerOrders 1, |");
        Assert.Contains("@StartDate datetime2 = '2020-01-01'", sig.Signatures[0].Label);
    }

    [Fact]
    public void Declare_offers_types()
    {
        var items = Complete("DECLARE @x |");
        Assert.Equal(CompletionKind.Type, Item(items, "int").Kind);
        Item(items, "nvarchar");
        Item(items, "TABLE");
        var cast = Complete("SELECT CAST(1 AS |)");
        Item(cast, "decimal");
        var col = Complete("CREATE TABLE #t (Id |)");
        Item(col, "bigint");
        var param = Complete("CREATE PROCEDURE dbo.p @a |\nAS SELECT 1");
        Item(param, "datetime2");
    }

    [Fact]
    public void Insert_column_list_offers_target_columns_minus_listed()
    {
        var items = Complete("INSERT INTO dbo.Orders (CustomerId, |) VALUES (1, 2)");
        HasColumn(items, "TotalAmount", "decimal(18,2)");
        Assert.DoesNotContain(items, i => i.Label == "CustomerId");
    }

    [Fact]
    public void Cte_names_in_from_context()
    {
        var items = Complete("WITH a AS (SELECT 1 AS x), b AS (SELECT x FROM a)\nSELECT * FROM |");
        Assert.Equal(CompletionKind.Cte, Item(items, "a").Kind);
        Item(items, "b");
        var inside = Complete("WITH a AS (SELECT 1 AS x), b AS (SELECT x FROM |)\nSELECT * FROM b");
        Item(inside, "a");
    }

    [Fact]
    public void Qualified_schema_and_database()
    {
        var items = Complete("SELECT * FROM sales.|");
        Item(items, "Regions");
        Assert.DoesNotContain(items, i => i.Label == "Orders");
        var db = Complete("SELECT * FROM SalesDB.dbo.|");
        Item(db, "Customers");
        var use = Complete("USE |");
        Assert.Equal(CompletionKind.Database, Item(use, "master").Kind);
    }

    [Fact]
    public void Without_a_snapshot_ast_items_are_still_offered()
    {
        var items = Complete("DECLARE @n int;\nCREATE TABLE #t (A int);\nWITH c AS (SELECT CAST(1 AS bigint) AS Id, @n AS N)\nSELECT c.| FROM c", NoSchema, profile: null);
        HasColumn(items, "Id", "bigint");
        HasColumn(items, "N", "int");
        var expr = Complete("DECLARE @n int;\nSELECT |", NoSchema, profile: null);
        Item(expr, "@n");
        var temp = Complete("CREATE TABLE #t (A int);\nSELECT * FROM |", NoSchema, profile: null);
        Item(temp, "#t");
    }

    [Fact]
    public void Nothing_inside_strings_or_comments()
    {
        Assert.Empty(Complete("SELECT 'o.|' FROM dbo.Orders o"));
        Assert.Empty(Complete("SELECT 1 -- o.|\nFROM dbo.Orders o"));
        Assert.Empty(Complete("/* SELECT o.| */ SELECT 1"));
    }

    // ------------------------------------------------------------------ signature help

    [Fact]
    public void Signature_for_exec_positional_and_named()
    {
        var pos = Sig("EXEC dbo.usp_GetCustomerOrders 5, |");
        var s = Assert.Single(pos.Signatures);
        Assert.Equal("dbo.usp_GetCustomerOrders(@CustomerId int, @StartDate datetime2, @MinTotal decimal(18,2) OUTPUT)", s.Label);
        Assert.Equal(3, s.Parameters.Length);
        Assert.Equal("@MinTotal decimal(18,2) OUTPUT", s.Parameters[2].Label);
        Assert.Equal(1, pos.ActiveParameter);
        Assert.Equal(0, Sig("EXEC dbo.usp_GetCustomerOrders |").ActiveParameter);
        Assert.Equal(2, Sig("EXEC dbo.usp_GetCustomerOrders @MinTotal = |").ActiveParameter);
        Assert.Equal(2, Sig("EXEC dbo.usp_GetCustomerOrders @CustomerId = 1, @StartDate = '2020-01-01', |").ActiveParameter);
        Assert.Equal(1, Sig("EXEC dbo.usp_GetCustomerOrders @CustomerId = 1, |").ActiveParameter);
        Assert.Equal(0, Sig("EXEC dbo.usp_GetCustomerOrders @MinTotal = @x OUTPUT, @Cust|").ActiveParameter);
        Assert.Empty(Sig("EXEC dbo.usp_GetCustomerOrd|").Signatures);
    }

    [Fact]
    public void Signature_for_builtin_and_user_functions()
    {
        var d = Sig("SELECT DATEADD(day, |, GETDATE())");
        Assert.StartsWith("DATEADD(datepart, number int, date)", d.Signatures[0].Label);
        Assert.Equal(1, d.ActiveParameter);
        var nested = Sig("SELECT ISNULL(LEN(o.Status), |) FROM dbo.Orders o");
        Assert.StartsWith("ISNULL(", nested.Signatures[0].Label);
        Assert.Equal(1, nested.ActiveParameter);
        var inner = Sig("SELECT ISNULL(LEN(|), 0)");
        Assert.StartsWith("LEN(", inner.Signatures[0].Label);
        var conv = Sig("SELECT CONVERT(varchar(10), GETDATE(), |)");
        Assert.Equal(2, conv.ActiveParameter);
        var udf = Sig("SELECT dbo.fn_CustomerTier(o.CustomerId, |) FROM dbo.Orders o");
        Assert.Equal("dbo.fn_CustomerTier(@CustomerId int, @AsOf date) → nvarchar(10)", udf.Signatures[0].Label);
        Assert.Equal(1, udf.ActiveParameter);
        var tvf = Sig("SELECT * FROM dbo.tvf_OrdersFor(|)");
        Assert.StartsWith("dbo.tvf_OrdersFor(@CustomerId int)", tvf.Signatures[0].Label);
        foreach (var name in new[] { "DATEDIFF", "CAST", "COALESCE", "SUBSTRING", "CHARINDEX", "FORMAT", "IIF", "LEFT", "RIGHT", "REPLACE", "STRING_AGG", "ROUND" })
            Assert.NotEmpty(Sig($"SELECT {name}(|").Signatures);
        Assert.Empty(Sig("SELECT * FROM t WHERE x IN (|)").Signatures);
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public void Broken_sql_never_throws_at_any_offset()
    {
        string[] inputs =
        [
            "SELECT", "SELECT o.", "SELECT * FROM", "SELECT * FROM dbo.Orders o WHERE (o.", "WITH", "WITH c AS (", "WITH c AS (SELECT * FROM c) SELECT c.",
            "MERGE", "MERGE dbo.Orders t USING", "EXEC", "EXEC @", "EXEC dbo.", "EXEC dbo.p @a = , , @", "DECLARE", "DECLARE @", "DECLARE @t TABLE (",
            "CREATE PROCEDURE", "CREATE PROCEDURE p @a", "CREATE PROC p @a int = ( AS BEGIN SELECT @", "CASE WHEN THEN END END END", "))))((((", "[[[", "\"\"\"",
            "'unterminated", "/* unterminated", "SELECT [o.", "SELECT N'", "GO\nGO 5\nSELECT", "INSERT INTO (", "UPDATE SET FROM WHERE", "SELECT * FROM (SELECT * FROM (SELECT",
            "SELECT a.b.c.d.e.f.", "DECLARE c CURSOR FOR", "OPEN", "FETCH NEXT FROM c INTO @", "IF EXISTS (SELECT 1 FROM dbo.Orders o WHERE o.", "SELECT CAST(1 AS", "@@",
            "SELECT * FROM dbo.Orders PIVOT (SUM(", "SELECT x FROM t CROSS APPLY (", "SELECT * FROM OPENJSON(@j) WITH (", "ELSE END BEGIN TRY END CATCH", "é中.",
        ];
        foreach (var input in inputs)
        {
            for (var off = 0; off <= input.Length; off++)
            {
                var cp = new CompleteParams { DocId = "fuzz", Text = input, Offset = off, ProfileId = "p1", Database = "SalesDB" };
                var ex = Record.Exception(() => TSqlCompletion.Complete(cp, Sales));
                Assert.Null(ex);
                var sp = new SignatureParams { DocId = "fuzz", Text = input, Offset = off, ProfileId = "p1", Database = "SalesDB" };
                Assert.Null(Record.Exception(() => TSqlSignature.Help(sp, Sales)));
            }
        }
        // Out-of-range offsets are clamped.
        Assert.NotNull(TSqlCompletion.Complete(new CompleteParams { DocId = "x", Text = "SELECT 1", Offset = 999 }, Sales));
        Assert.NotNull(TSqlCompletion.Complete(new CompleteParams { DocId = "x", Text = "SELECT 1", Offset = -5 }, Sales));
    }

    [Fact]
    public void Incremental_lexing_matches_a_full_lex()
    {
        var rnd = new Random(7);
        string[] pieces = ["'", "--", "/*", "*/", "\n", " ", "[", "]", "N'", "x", "@", "#", "(", ")", ";", "GO", "\"", "1.5e", "SELECT ", "END"];
        var text = BigProc(out _)[..20_000] + "\n-- tail comment\nSELECT 'a''b', [c]]d], N'x' /* nested /* c */ */ FROM t;";
        var lexed = SqlLexer.Lex(text);
        for (var i = 0; i < 400; i++)
        {
            var at = rnd.Next(text.Length + 1);
            var cut = rnd.Next(4) == 0 ? Math.Min(rnd.Next(1, 40), text.Length - at) : 0;
            var ins = rnd.Next(3) == 0 ? "" : pieces[rnd.Next(pieces.Length)];
            var next = text.Remove(at, cut).Insert(at, ins);
            var inc = SqlLexer.LexIncremental(text, lexed, next);
            var full = SqlLexer.Lex(next);
            Assert.Equal(full.Tokens, inc.Tokens);
            Assert.Equal(full.Opaque, inc.Opaque);
            text = next;
            lexed = inc;
        }
    }

    // ------------------------------------------------------------------ performance (§14 metrics 5 and 6)

    internal static string BigProc(out int markerOffset)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CREATE OR ALTER PROCEDURE dbo.usp_Big");
        sb.AppendLine("  @CustomerId int,\n  @StartDate datetime2 = NULL,\n  @MinTotal decimal(18,2) OUTPUT");
        sb.AppendLine("AS\nBEGIN\n  SET NOCOUNT ON;");
        sb.AppendLine("  DECLARE @x int = 0, @name nvarchar(100), @total decimal(18,2);");
        sb.AppendLine("  CREATE TABLE #work (Id int, Amount decimal(18,2), Note nvarchar(100));");
        var lines = 9;
        var cte = 0;
        var merges = 0;
        var block = 0;
        while (lines < 11_900)
        {
            block++;
            if (cte < 40 && block % 7 == 1)
            {
                cte++;
                // A CTE statement of ~150 lines: a wide select list and a join.
                sb.AppendLine($"  ;WITH c{cte} AS (");
                sb.AppendLine("    SELECT o.OrderId, o.CustomerId, o.OrderDate, o.Status,");
                for (var k = 0; k < 140; k++) sb.AppendLine($"      CASE WHEN o.TotalAmount > {k} THEN o.TotalAmount * {k} ELSE 0 END AS Amt{k},");
                sb.AppendLine("      o.TotalAmount\n    FROM dbo.Orders o\n    WHERE o.OrderDate >= @StartDate\n  )");
                sb.AppendLine($"  INSERT INTO #work (Id, Amount, Note) SELECT c.CustomerId, c.TotalAmount, c.Status FROM c{cte} c WHERE c.TotalAmount > @MinTotal;");
                lines += 148;
                continue;
            }
            if (merges < 6 && block % 50 == 25)
            {
                merges++;
                sb.AppendLine("  MERGE dbo.Orders AS t\n  USING (SELECT Id, Amount FROM #work) AS s ON t.OrderId = s.Id\n  WHEN MATCHED THEN UPDATE SET t.TotalAmount = s.Amount");
                sb.AppendLine("  WHEN NOT MATCHED THEN INSERT (CustomerId, TotalAmount) VALUES (s.Id, s.Amount)\n  OUTPUT $action, inserted.OrderId, deleted.TotalAmount;");
                lines += 6;
                continue;
            }
            sb.AppendLine($"  IF @x > {block}\n  BEGIN\n    UPDATE w SET w.Amount = w.Amount + 1 FROM #work w WHERE w.Id = @CustomerId;\n    SET @x = @x + 1;\n  END");
            sb.AppendLine($"  SELECT @total = SUM(o.TotalAmount) FROM dbo.Orders o JOIN dbo.Customers cu ON cu.CustomerId = o.CustomerId WHERE o.Status = N'S{block}';");
            lines += 6;
        }
        sb.Append("  SELECT w.Id, w.Amount FROM #work w JOIN dbo.Orders o ON o.OrderId = w.Id WHERE w.");
        markerOffset = sb.Length;
        sb.AppendLine("Amount > 0;\nEND");
        return sb.ToString();
    }

    static double P(List<double> xs, double q)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[Math.Clamp((int)Math.Ceiling(q * s.Count) - 1, 0, s.Count - 1)];
    }

    [Fact]
    public void Twelve_thousand_line_proc_completion_latency()
    {
        var text = BigProc(out var marker);
        var lineCount = text.Count(c => c == '\n') + 1;
        Assert.InRange(lineCount, 11_500, 12_500);
        output.WriteLine($"proc: {lineCount} lines, {text.Length:N0} chars");

        // Full ScriptDom parse for reference (metric 6 is about lang.diagnostics, which owns the full parse).
        var parseTimes = new List<double>();
        for (var r = 0; r < 4; r++)
        {
            var sw0 = Stopwatch.StartNew();
            var parser = TSqlParsing.NewParser();
            parser.Parse(new StringReader(text), out var errs);
            parseTimes.Add(sw0.Elapsed.TotalMilliseconds);
            Assert.Empty(errs);
        }
        output.WriteLine($"full ScriptDom parse (reference): first {parseTimes[0]:F1} ms, warm median {P(parseTimes.Skip(1).ToList(), 0.5):F1} ms");

        // Warm-up.
        for (var i = 0; i < 5; i++)
            TSqlCompletion.Complete(new CompleteParams { DocId = "big-warm", Text = text, Offset = marker, ProfileId = "p1", Database = "SalesDB" }, Sales);

        // Typing at the end of the proc: every keystroke is a new text version (document re-lexed, statement re-parsed).
        const string typed = "Amount";
        var times = new List<double>();
        CompletionItem[] last = [];
        for (var round = 0; round < 5; round++)
        {
            for (var k = 0; k <= typed.Length; k++)
            {
                var t = text.Insert(marker, typed[..k] + new string(' ', round));
                var p = new CompleteParams { DocId = "big", Text = t, Offset = marker + k, ProfileId = "p1", Database = "SalesDB" };
                var sw = Stopwatch.StartNew();
                last = TSqlCompletion.Complete(p, Sales);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        output.WriteLine($"tier-2 completion while typing ({times.Count} requests): p50 {P(times, 0.5):F1} ms, p95 {P(times, 0.95):F1} ms, max {times.Max():F1} ms");
        HasColumn(last, "Amount", "decimal(18,2)");

        // Cursor jumping to different statements (declaration cache warm, enclosing statement differs each time).
        var jumps = new List<double>();
        var rnd = new Random(42);
        for (var i = 0; i < 40; i++)
        {
            var off = rnd.Next(text.Length / 10, text.Length - 10);
            var sw = Stopwatch.StartNew();
            TSqlCompletion.Complete(new CompleteParams { DocId = "big-jump", Text = text, Offset = off, ProfileId = "p1", Database = "SalesDB" }, Sales);
            jumps.Add(sw.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"tier-2 completion at random offsets: p50 {P(jumps, 0.5):F1} ms, p95 {P(jumps, 0.95):F1} ms");

        var sigTimes = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var t = text.Insert(marker, "Amount > 0; EXEC dbo.usp_GetCustomerOrders 1, " + new string(' ', i));
            var sw = Stopwatch.StartNew();
            var h = TSqlSignature.Help(new SignatureParams { DocId = "big-sig", Text = t, Offset = marker + 46 + i, ProfileId = "p1", Database = "SalesDB" }, Sales);
            sigTimes.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(1, h.ActiveParameter);
        }
        output.WriteLine($"signature help: p50 {P(sigTimes, 0.5):F1} ms, p95 {P(sigTimes, 0.95):F1} ms");

        // Shared CI runners are several times noisier than a dev box (p95 of 40 cold jumps is nearly the max), so they get a wider
        // gate that still catches an order-of-magnitude regression; the strict 150 ms budget applies locally and in pnpm bench.
        // Slow runners (macos-15-intel: warm full parse ~2 s vs ~0.4 s on a dev box) scale it by their own full-parse time:
        // a keystroke that re-parsed the whole proc would cost at least that, so half of it still catches the regression.
        var budget = Environment.GetEnvironmentVariable("CI") is { Length: > 0 }
            ? Math.Max(500, P(parseTimes.Skip(1).ToList(), 0.5) / 2)
            : 150;
        Assert.True(P(times, 0.95) < budget, $"p95 {P(times, 0.95):F1} ms");
        Assert.True(P(jumps, 0.95) < budget, $"p95 {P(jumps, 0.95):F1} ms");
    }

    [Fact]
    public void Twelve_thousand_line_proc_scope_is_resolved()
    {
        var text = BigProc(out var marker);
        var items = TSqlCompletion.Complete(new CompleteParams { DocId = "big-scope", Text = text, Offset = marker, ProfileId = "p1", Database = "SalesDB" }, Sales);
        HasColumn(items, "Amount", "decimal(18,2)");
        // Unqualified context at the same place: params, variables, both sources.
        var t2 = text.Remove(marker - 2, 2);
        var items2 = TSqlCompletion.Complete(new CompleteParams { DocId = "big-scope2", Text = t2, Offset = marker - 2, ProfileId = "p1", Database = "SalesDB" }, Sales);
        Assert.Equal(CompletionKind.Parameter, Item(items2, "@MinTotal").Kind);
        Item(items2, "@total");
        HasColumn(items2, "w.Note", "nvarchar(100)");
        HasColumn(items2, "o.Status", "nvarchar(20)");
    }
}
