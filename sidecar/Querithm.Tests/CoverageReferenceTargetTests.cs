using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

/// <summary>What is under the cursor (find usages / rename entry points) and which local symbols share a name, per engine.</summary>
public class ReferenceTargetTests
{
    static RefContext Ctx(string schema, StringComparer names, params (string Table, string[] Columns)[] tables) => new()
    {
        DefaultSchema = schema,
        Columns = (s, t) => tables.FirstOrDefault(x => names.Equals(s, schema) && names.Equals(x.Table, t)).Columns,
        SchemasOf = n => tables.Any(x => names.Equals(x.Table, n)) ? [schema] : [],
    };

    static readonly RefContext Ms = Ctx("dbo", StringComparer.OrdinalIgnoreCase, ("Orders", ["Id", "Qty"]), ("Customers", ["Id", "Name"]));
    static readonly RefContext Pg = Ctx("public", StringComparer.Ordinal, ("orders", ["id", "qty"]), ("customers", ["id", "name"]));

    static (string Text, int Offset) Mark(string sql)
    {
        var i = sql.IndexOf('|');
        Assert.True(i >= 0);
        return (sql.Remove(i, 1), i);
    }

    [Theory]
    [InlineData("SELECT |Qty FROM dbo.Orders", RefKind.Column, "dbo", "Orders", "Qty")]
    [InlineData("SELECT o.|Qty FROM dbo.Orders o", RefKind.Column, "dbo", "Orders", "Qty")]
    [InlineData("SELECT dbo.Orders.|Qty FROM dbo.Orders", RefKind.Column, "dbo", "Orders", "Qty")]
    [InlineData("DELETE o FROM dbo.Orders o WHERE o.|Qty > 1", RefKind.Column, "dbo", "Orders", "Qty")]
    [InlineData("UPDATE o SET o.|Qty = 1 FROM dbo.Orders o", RefKind.Column, "dbo", "Orders", "Qty")]
    [InlineData("INSERT INTO dbo.Orders (|Qty) VALUES (1)", RefKind.Column, "dbo", "Orders", "Qty")]
    [InlineData("SELECT dbo.|Orders.Qty FROM dbo.Orders", RefKind.Table, "dbo", "Orders", null)]
    [InlineData("SELECT x FROM dbo.|Orders", RefKind.Table, "dbo", "Orders", null)]
    [InlineData("EXEC dbo.|usp_a 1", RefKind.Procedure, "dbo", "usp_a", null)]
    [InlineData("SELECT dbo.|fn_a(1)", RefKind.Function, "dbo", "fn_a", null)]
    [InlineData("SELECT * FROM dbo.|tvf_a(1)", RefKind.Function, "dbo", "tvf_a", null)]
    [InlineData("DECLARE @a dbo.|mytype", RefKind.Type, "dbo", "mytype", null)]
    [InlineData("CREATE PROCEDURE dbo.|p1 AS SELECT 1", RefKind.Procedure, "dbo", "p1", null)]
    [InlineData("CREATE VIEW dbo.|v1 AS SELECT 1 AS a", RefKind.View, "dbo", "v1", null)]
    [InlineData("CREATE FUNCTION dbo.|f1() RETURNS int AS BEGIN RETURN 1 END", RefKind.Function, "dbo", "f1", null)]
    public void Tsql_target_at_cursor(string sql, RefKind kind, string schema, string name, string? column)
    {
        var (text, offset) = Mark(sql);
        Assert.Equal(new RefTarget(kind, schema, name, column), new TSqlReferenceFinder().TargetAt(text, offset, Ms));
    }

    [Theory]
    [InlineData("SELECT |o.Qty FROM dbo.Orders o")]
    [InlineData("WITH c AS (SELECT 1 AS a) SELECT |a FROM c")]
    [InlineData("SELECT 1 FROM #t WHERE |q = 1")]
    [InlineData("SELECT |")]
    public void Tsql_names_that_are_not_database_objects_have_no_target(string sql)
    {
        var (text, offset) = Mark(sql);
        Assert.Null(new TSqlReferenceFinder().TargetAt(text, offset, Ms));
    }

    [Theory]
    [InlineData("SELECT |qty FROM public.orders", RefKind.Column, "public", "orders", "qty")]
    [InlineData("SELECT o.|qty FROM public.orders o", RefKind.Column, "public", "orders", "qty")]
    [InlineData("SELECT public.orders.|qty FROM public.orders", RefKind.Column, "public", "orders", "qty")]
    [InlineData("UPDATE public.orders SET |qty = 1", RefKind.Column, "public", "orders", "qty")]
    [InlineData("INSERT INTO public.orders (|qty) VALUES (1)", RefKind.Column, "public", "orders", "qty")]
    [InlineData("DELETE FROM public.orders o WHERE o.|qty > 1", RefKind.Column, "public", "orders", "qty")]
    [InlineData("SELECT x FROM public.|orders", RefKind.Table, "public", "orders", null)]
    [InlineData("SELECT public.|fn_a(1)", RefKind.Function, "public", "fn_a", null)]
    [InlineData("SELECT * FROM public.|tvf_a(1)", RefKind.Function, "public", "tvf_a", null)]
    [InlineData("CREATE TABLE public.|t1 (id int)", RefKind.Table, "public", "t1", null)]
    [InlineData("CREATE VIEW public.|v1 AS SELECT 1", RefKind.View, "public", "v1", null)]
    [InlineData("CREATE FUNCTION public.|f1() RETURNS int AS $$ SELECT 1 $$ LANGUAGE sql", RefKind.Function, "public", "f1", null)]
    [InlineData("CREATE PROCEDURE public.|p1() AS $$ SELECT 1 $$ LANGUAGE sql", RefKind.Procedure, "public", "p1", null)]
    public void Pg_target_at_cursor(string sql, RefKind kind, string schema, string name, string? column)
    {
        var (text, offset) = Mark(sql);
        Assert.Equal(new RefTarget(kind, schema, name, column), new PgReferenceFinder().TargetAt(text, offset, Pg));
    }

    [Theory]
    [InlineData("SELECT |o.qty FROM public.orders o")]
    [InlineData("WITH c AS (SELECT 1 AS a) SELECT |a FROM c")]
    [InlineData("SELECT |")]
    [InlineData("SELECT 'str|ing'")]
    [InlineData("-- com|ment")]
    public void Pg_names_that_are_not_database_objects_have_no_target(string sql)
    {
        var (text, offset) = Mark(sql);
        Assert.Null(new PgReferenceFinder().TargetAt(text, offset, Pg));
    }

    [Theory]
    [InlineData("DECLARE @a int = 1; SELECT |@a, @a", 3)]
    [InlineData("CREATE PROCEDURE p @x int AS SELECT |@x", 2)]
    [InlineData("WITH c AS (SELECT 1 AS a) SELECT * FROM |c JOIN c d ON 1=1", 3)]
    [InlineData("SELECT o.Id FROM dbo.Orders |o WHERE o.Id = 1", 3)]
    [InlineData("SELECT |o.Id FROM dbo.Orders o", 2)]
    [InlineData("SELECT * FROM (SELECT 1 AS a) |d WHERE d.a = 1", 2)]
    public void Tsql_local_symbols_list_every_occurrence(string sql, int count)
    {
        var (text, offset) = Mark(sql);
        var occ = new TSqlReferenceFinder().LocalOccurrences(text, offset)!;
        Assert.Equal(count, occ.Count);
        Assert.Contains(occ, o => o.Offset <= offset && offset <= o.Offset + o.Length);
    }

    [Fact]
    public void Tsql_cursor_on_a_literal_has_no_local_symbol() => Assert.Null(new TSqlReferenceFinder().LocalOccurrences("SELECT 1", 7));

    [Theory]
    [InlineData("DO $$ DECLARE v int; BEGIN SELECT |v; END $$", 2)]
    [InlineData("CREATE FUNCTION f(a int) RETURNS int AS $$ SELECT |a $$ LANGUAGE sql", 2)]
    [InlineData("WITH c AS (SELECT 1 AS a) SELECT * FROM |c", 2)]
    [InlineData("SELECT o.id FROM public.orders |o WHERE o.id = 1", 3)]
    [InlineData("SELECT |o.id FROM public.orders o", 2)]
    public void Pg_local_symbols_list_every_occurrence(string sql, int count)
    {
        var (text, offset) = Mark(sql);
        var occ = new PgReferenceFinder().LocalOccurrences(text, offset)!;
        Assert.Equal(count, occ.Count);
        Assert.Contains(occ, o => o.Offset <= offset && offset <= o.Offset + o.Length);
    }

    [Theory]
    [InlineData("SELECT $1, |$1")]
    [InlineData("SELECT :|name, :name")]
    public void Pg_parameters_are_not_local_symbols(string sql)
    {
        var (text, offset) = Mark(sql);
        Assert.Null(new PgReferenceFinder().LocalOccurrences(text, offset));
    }

    [Fact]
    public void Tsql_finds_types_sequences_views_functions_and_merge_targets()
    {
        const string sql = """
            CREATE VIEW dbo.v_orders AS SELECT Id FROM dbo.Orders;
            GO
            DECLARE @t dbo.Money;
            SELECT NEXT VALUE FOR dbo.seq_a, dbo.fn_a(1), x.v FROM dbo.v_orders x CROSS APPLY dbo.tvf_a(1) y;
            MERGE dbo.Orders AS t USING dbo.Customers AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.Qty = 1;
            """;
        var f = new TSqlReferenceFinder();
        Assert.Single(f.Find(sql, new RefTarget(RefKind.Type, "dbo", "Money"), Ms).Hits);
        Assert.Single(f.Find(sql, new RefTarget(RefKind.Sequence, "dbo", "seq_a"), Ms).Hits);
        Assert.Single(f.Find(sql, new RefTarget(RefKind.Function, "dbo", "fn_a"), Ms).Hits);
        Assert.Single(f.Find(sql, new RefTarget(RefKind.Function, "dbo", "tvf_a"), Ms).Hits);
        var view = f.Find(sql, new RefTarget(RefKind.View, "dbo", "v_orders"), Ms);
        Assert.Equal(2, view.Hits.Count);   // the definition and the use
        Assert.Contains(view.Hits, h => h.Definition);
        var orders = f.Find(sql, new RefTarget(RefKind.Table, "dbo", "Orders"), Ms);
        Assert.Equal(2, orders.Hits.Count);   // the view's source and the MERGE target
        var qty = f.Find(sql, new RefTarget(RefKind.Column, "dbo", "Orders", "Qty"), Ms);
        Assert.Single(qty.Hits);
    }

    [Fact]
    public void Tsql_merge_target_alias_resolves_columns_everywhere_in_the_merge()
    {
        // Regression: the alias of a MERGE target belongs to the merge, so t.Qty used to resolve to no table at all.
        const string sql = "MERGE dbo.Orders AS t USING dbo.Customers AS s ON t.Qty = s.Id WHEN MATCHED AND t.Qty > 1 THEN UPDATE SET t.Qty = 1, Qty = 2 WHEN NOT MATCHED THEN INSERT (Qty) VALUES (3);";
        var scan = new TSqlReferenceFinder().Find(sql, new RefTarget(RefKind.Column, "dbo", "Orders", "Qty"), Ms);
        Assert.Equal(5, scan.Hits.Count);
        Assert.All(scan.Hits, h => Assert.Equal("Qty", sql.Substring(h.Offset, h.Length)));
        Assert.All(scan.Hits, h => Assert.Equal(RefConfidence.Exact, h.Confidence));
        var target = new TSqlReferenceFinder().TargetAt(sql, sql.IndexOf("t.Qty = 1", StringComparison.Ordinal) + 2, Ms);
        Assert.Equal(new RefTarget(RefKind.Column, "dbo", "Orders", "Qty"), target);
        // Customers has no Qty column, so s.Qty is not Orders.Qty.
        Assert.Empty(new TSqlReferenceFinder().Find("MERGE dbo.Orders AS t USING dbo.Customers AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE; SELECT s.Qty FROM dbo.Customers s", new RefTarget(RefKind.Column, "dbo", "Orders", "Qty"), Ms).Hits);
    }

    [Fact]
    public void Text_that_does_not_parse_reports_the_failure_instead_of_guessing()
    {
        var scan = new TSqlReferenceFinder().Find("SELECT FROM WHERE ((", new RefTarget(RefKind.Table, "dbo", "Orders"), Ms);
        Assert.True(scan.ParseFailed);
        Assert.Empty(scan.Hits);
        Assert.Null(new TSqlReferenceFinder().TargetAt("SELECT FROM WHERE ((", 5, Ms));
        Assert.Null(new TSqlReferenceFinder().LocalOccurrences("SELECT FROM WHERE ((", 5));
    }
}
