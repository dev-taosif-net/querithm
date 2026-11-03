using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;

namespace Querithm.Tests;

/// <summary>Phase D2 find usages on MySQL tokens: tables, columns, routines, triggers; strings and comments never match.</summary>
public sealed class MySqlReferenceFinderTests
{
    static readonly MySqlReferenceFinder F = new();

    static readonly RefContext Ctx = new()
    {
        DefaultSchema = "shop",
        Columns = (s, t) => (s, t) switch
        {
            ("shop", "orders") => ["id", "customer_id", "total"],
            ("shop", "customer") => ["id", "Email"],
            _ => null,
        },
        SchemasOf = n => n is "orders" or "customer" ? ["shop"] : [],
    };

    static string[] Texts(string sql, RefTarget target) => F.Find(sql, target, Ctx).Hits.Select(h => sql.Substring(h.Offset, h.Length)).ToArray();

    [Fact]
    public void Tables_are_found_after_from_join_update_insert_and_as_qualifiers_never_in_strings()
    {
        const string sql = """
            SELECT o.total, orders.id FROM orders o JOIN `shop`.`orders` x ON x.id = o.id WHERE note = 'orders' -- orders
            ; UPDATE orders SET total = 0; INSERT INTO orders (id) VALUES (1); REPLACE orders VALUES (2, 3, 4); SELECT * FROM other.orders
            """;
        var hits = F.Find(sql, new RefTarget(RefKind.Table, "shop", "orders"), Ctx).Hits;
        // FROM orders, `shop`.`orders`, UPDATE, INSERT INTO, REPLACE — not 'orders' the string, the comment or other.orders.
        Assert.Equal(5, hits.Count(h => sql.Substring(h.Offset, h.Length) is "orders" or "`orders`"));
        Assert.DoesNotContain(hits, h => sql.Substring(h.Offset - 6, 6) == "other.");
        Assert.All(hits, h => Assert.Equal(RefConfidence.Exact, h.Confidence));
    }

    [Fact]
    public void Table_names_are_exact_but_column_names_fold_case()
    {
        Assert.Empty(Texts("SELECT * FROM Orders", new RefTarget(RefKind.Table, "shop", "orders")));
        Assert.Equal(["EMAIL", "c.email"[2..]], Texts("SELECT EMAIL FROM customer c WHERE c.email <> ''", new RefTarget(RefKind.Column, "shop", "customer", "Email")));
    }

    [Fact]
    public void Columns_resolve_through_aliases_and_ambiguous_ones_are_possible()
    {
        var target = new RefTarget(RefKind.Column, "shop", "orders", "id");
        var scan = F.Find("SELECT o.id, c.id, id FROM orders o JOIN customer c ON c.id = o.customer_id", target, Ctx);
        var sql = "SELECT o.id, c.id, id FROM orders o JOIN customer c ON c.id = o.customer_id";
        Assert.Contains(scan.Hits, h => h.Offset == sql.IndexOf("o.id", StringComparison.Ordinal) + 2 && h.Confidence == RefConfidence.Exact);
        Assert.DoesNotContain(scan.Hits, h => h.Offset == sql.IndexOf("c.id", StringComparison.Ordinal) + 2);
        Assert.Contains(scan.Hits, h => h.Offset == sql.IndexOf(", id ", StringComparison.Ordinal) + 2 && h.Confidence == RefConfidence.Possible);
    }

    [Fact]
    public void A_stored_program_body_makes_bare_columns_possible_and_prepared_sql_dynamic()
    {
        const string proc = """
            CREATE DEFINER=`root`@`%` PROCEDURE `shop`.`p`(IN total INT)
            BEGIN
              DECLARE n INT;
              SELECT COUNT(*) INTO n FROM orders WHERE total > 0;
              SET @s = 'SELECT 1'; PREPARE q FROM @s; EXECUTE q;
            END
            """;
        var scan = F.Find(proc, new RefTarget(RefKind.Column, "shop", "orders", "total"), Ctx);
        Assert.True(scan.DynamicSql);
        Assert.Contains(scan.Hits, h => h.Confidence == RefConfidence.Possible && h.Reason!.Contains("local variable"));
        Assert.Contains(F.Find(proc, new RefTarget(RefKind.Table, "shop", "orders"), Ctx).Hits, h => proc.Substring(h.Offset, h.Length) == "orders");
        // The header names the procedure itself (a definition hit).
        Assert.Contains(F.Find(proc, new RefTarget(RefKind.Procedure, "shop", "p"), Ctx).Hits, h => h.Definition);
    }

    [Fact]
    public void A_trigger_names_its_table_and_new_old_columns()
    {
        const string trg = "CREATE TRIGGER `shop`.`t` BEFORE UPDATE ON `shop`.`orders` FOR EACH ROW SET NEW.total = IFNULL(NEW.total, OLD.Total)";
        Assert.Equal(["`orders`"], Texts(trg, new RefTarget(RefKind.Table, "shop", "orders")));
        Assert.Equal(["total", "total", "Total"], Texts(trg, new RefTarget(RefKind.Column, "shop", "orders", "total")));
        Assert.Empty(Texts(trg, new RefTarget(RefKind.Column, "shop", "customer", "total")));
    }

    [Fact]
    public void Routines_are_found_where_they_are_called()
    {
        Assert.Equal(["p_count"], Texts("CALL p_count(@n)", new RefTarget(RefKind.Procedure, "shop", "p_count")));
        Assert.Equal(["F_DOUBLE"], Texts("SELECT F_DOUBLE(2), f_double AS x FROM dual", new RefTarget(RefKind.Function, "shop", "f_double")));
    }

    [Fact]
    public void The_target_at_the_cursor_and_local_symbols()
    {
        const string sql = "SELECT o.total FROM orders o WHERE o.id = 1";
        Assert.Equal(new RefTarget(RefKind.Column, "shop", "orders", "total"), F.TargetAt(sql, sql.IndexOf("total", StringComparison.Ordinal), Ctx));
        Assert.Equal(new RefTarget(RefKind.Table, "shop", "orders"), F.TargetAt(sql, sql.IndexOf("orders", StringComparison.Ordinal), Ctx));
        var alias = F.LocalOccurrences(sql, sql.IndexOf(" o ", StringComparison.Ordinal) + 1);
        Assert.Equal(3, alias!.Count);
        Assert.Equal(new RefTarget(RefKind.Column, "shop", "customer", "Email"), F.TargetAt("SELECT email FROM customer", 8, Ctx)); // the declared spelling
    }

    [Theory]
    [InlineData("orders", "orders_v2", "orders_v2")]
    [InlineData("orders", "order", "`order`")]
    [InlineData("orders", "new name", "`new name`")]
    [InlineData("`orders`", "orders_v2", "`orders_v2`")]
    public void Renames_keep_the_quoting_and_quote_reserved_words(string original, string newName, string expected) => Assert.Equal(expected, F.Rename(original, newName));
}
