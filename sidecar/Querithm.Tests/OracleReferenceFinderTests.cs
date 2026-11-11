using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Tests;

/// <summary>
/// D4 S11 <see cref="OracleReferenceFinder"/> on tokens: table, column, routine, sequence and type references in SQL and
/// PL/SQL (triggers, %TYPE / %ROWTYPE, cursor records, calls without parentheses, a unit's own header and END name),
/// quoted vs unquoted names, never inside strings, q'…' literals, comments, hints or over a database link; dynamic SQL;
/// the target at the cursor, local symbols, and how a new name is written.
/// </summary>
public sealed class OracleReferenceFinderTests
{
    static readonly OracleReferenceFinder F = new();

    static readonly Dictionary<(string, string), string[]> Tables = new()
    {
        [("APP", "ITEM")] = ["ID", "PRICE", "QTY"],
        [("APP", "ORDERS")] = ["ID", "ITEM_ID", "QTY"],
        [("APP", "Sales")] = ["ID"],
        [("APP", "SALES")] = ["ID"],
        [("OTHER", "ITEM")] = ["ID"],
    };

    static RefContext Ctx(string schema = "APP") => new()
    {
        DefaultSchema = schema,
        Columns = (s, t) => Tables.TryGetValue((s, t), out var c) ? c : null,
        SchemasOf = n => Tables.Keys.Where(k => k.Item2 == n).Select(k => k.Item1).Distinct().Concat(n is "P" or "F" or "SEQ" ? ["APP"] : []).ToArray(),
    };

    static RefTarget Table(string name = "ITEM", string schema = "APP") => new(RefKind.Table, schema, name);
    static RefTarget Column(string column, string table = "ITEM") => new(RefKind.Column, "APP", table, column);

    /// <summary>The hit spans as text, in order.</summary>
    static string[] Hits(string sql, RefTarget t, RefConfidence? only = null, RefContext? ctx = null) =>
        F.Find(sql, t, ctx ?? Ctx()).Hits.Where(h => only == null || h.Confidence == only).Select(h => sql.Substring(h.Offset, h.Length)).ToArray();

    [Theory]
    [InlineData("SELECT * FROM item", 1)]
    [InlineData("SELECT * FROM app.item i JOIN orders o ON o.item_id = i.id", 1)]
    [InlineData("SELECT * FROM orders o, item", 1)]
    [InlineData("UPDATE item SET qty = 0", 1)]
    [InlineData("INSERT INTO item (id) VALUES (1)", 1)]
    [InlineData("DELETE FROM item WHERE id = 1", 1)]
    [InlineData("DELETE item WHERE id = 1", 1)]
    [InlineData("MERGE INTO item t USING orders s ON (t.id = s.item_id) WHEN MATCHED THEN UPDATE SET t.qty = s.qty", 1)]
    [InlineData("MERGE INTO orders t USING item s ON (t.item_id = s.id) WHEN MATCHED THEN UPDATE SET t.qty = s.qty", 1)]
    [InlineData("ALTER TABLE item ADD (x NUMBER)", 1)]
    [InlineData("TRUNCATE TABLE item", 1)]
    [InlineData("CREATE TABLE orders2 (item_id NUMBER REFERENCES item (id))", 1)]
    [InlineData("CREATE INDEX ix ON item (qty)", 1)]
    [InlineData("SELECT * FROM item@remote_link", 0)]
    [InlineData("SELECT 'item' FROM dual -- FROM item", 0)]
    [InlineData("SELECT q'[FROM item]' FROM dual /* item */", 0)]
    [InlineData("SELECT * FROM other.item", 0)]
    [InlineData("SELECT * FROM \"ITEM\"", 1)]
    [InlineData("SELECT * FROM \"item\"", 0)]
    [InlineData("SELECT item.qty FROM item", 2)]
    public void Table_references(string sql, int count) => Assert.Equal(count, Hits(sql, Table()).Length);

    [Fact]
    public void A_quoted_mixed_case_table_is_not_its_upper_case_neighbour()
    {
        Assert.Equal(["\"Sales\""], Hits("SELECT * FROM \"Sales\" JOIN sales s ON 1 = 1", Table("Sales")));
        Assert.Equal(["sales"], Hits("SELECT * FROM \"Sales\" JOIN sales s ON 1 = 1", Table("SALES")));
    }

    [Fact]
    public void ROWTYPE_and_column_TYPE_declarations_reference_the_table_and_column()
    {
        const string sql = "CREATE OR REPLACE PROCEDURE app.p AS\n  r item%ROWTYPE;\n  q app.item.qty%TYPE;\n  v orders.qty%TYPE;\nBEGIN NULL; END;\n/";
        Assert.Equal(["item", "item"], Hits(sql, Table()));
        Assert.Equal(["qty"], Hits(sql, Column("QTY")));
    }

    [Fact]
    public void Columns_unqualified_qualified_and_in_an_insert_list()
    {
        Assert.Equal(["qty"], Hits("SELECT qty FROM item", Column("QTY"), RefConfidence.Exact));
        Assert.Equal(["qty", "qty"], Hits("SELECT i.qty FROM item i WHERE i.price > 1 AND qty > 0", Column("QTY")));
        Assert.Equal(["qty"], Hits("INSERT INTO item (id, qty) VALUES (1, 2)", Column("QTY")));
        Assert.Equal(["qty"], Hits("UPDATE item SET qty = 0", Column("QTY")));
        // Two tables in scope have QTY: possible, with the reason.
        var both = F.Find("SELECT qty FROM item, orders", Column("QTY"), Ctx()).Hits;
        Assert.Equal(RefConfidence.Possible, Assert.Single(both).Confidence);
        Assert.Contains("2 tables", both[0].Reason);
        // Another table's column of the same name is not this one.
        Assert.Empty(Hits("SELECT o.qty FROM orders o", Column("QTY")));
        // An output alias is not a reference.
        Assert.Empty(Hits("SELECT price AS qty FROM item", Column("QTY")));
    }

    [Fact]
    public void A_select_into_variable_is_not_a_column_and_a_string_is_not_either()
    {
        const string sql = "CREATE OR REPLACE PROCEDURE app.p (qty OUT NUMBER) AS\n  s VARCHAR2(9) := 'qty';\nBEGIN\n  SELECT SUM(price) INTO qty FROM item;\nEND;\n/";
        Assert.Empty(Hits(sql, Column("QTY")));
    }

    [Fact]
    public void A_view_header_column_list_names_the_views_own_columns()
    {
        const string sql = "CREATE OR REPLACE VIEW app.v (qty, total) AS\nSELECT qty, price * qty FROM item;";
        Assert.Equal(2, Hits(sql, Column("QTY")).Length); // the two in the query, not the header's
        Assert.Equal(["v"], Hits(sql, new RefTarget(RefKind.View, "APP", "V")));
    }

    [Fact]
    public void A_triggers_table_NEW_and_OLD_and_UPDATE_OF_columns()
    {
        const string sql = "CREATE OR REPLACE TRIGGER app.trg BEFORE UPDATE OF qty, price ON item FOR EACH ROW\n" +
                           "WHEN (new.qty > 0)\nBEGIN\n  :NEW.qty := NVL(:OLD.qty, 1);\nEND;\n/";
        Assert.Equal(["item"], Hits(sql, Table()));
        Assert.Equal(4, Hits(sql, Column("QTY")).Length); // OF qty, new.qty, :NEW.qty, :OLD.qty
        Assert.Empty(Hits(sql, Column("QTY", "ORDERS")));
    }

    [Fact]
    public void A_cursor_records_field_is_only_possible()
    {
        const string sql = "CREATE OR REPLACE PROCEDURE app.p AS x NUMBER := 0;\nBEGIN\n  FOR r IN (SELECT id, qty FROM item) LOOP\n    x := x + r.qty;\n  END LOOP;\nEND;\n/";
        var hits = F.Find(sql, Column("QTY"), Ctx()).Hits;
        Assert.Equal(2, hits.Count);
        Assert.Equal(RefConfidence.Exact, hits[0].Confidence);
        Assert.Equal(RefConfidence.Possible, hits[1].Confidence);
        Assert.Contains("cursor record R", hits[1].Reason);
    }

    [Fact]
    public void Routine_calls_with_and_without_parentheses_and_package_members_are_not_routines()
    {
        var p = new RefTarget(RefKind.Procedure, "APP", "P");
        const string sql = "CREATE OR REPLACE PROCEDURE app.caller AS\nBEGIN\n  p;\n  p(1);\n  app.p(2);\n  pkg.p(3);\n  IF 1 = 1 THEN p; END IF;\nEND;\n/";
        Assert.Equal(["p", "p", "p", "p"], Hits(sql, p));
        var f = new RefTarget(RefKind.Function, "APP", "F");
        Assert.Equal(["f", "f"], Hits("CREATE OR REPLACE PROCEDURE app.x AS n NUMBER;\nBEGIN\n  n := f;\n  n := f(1) + 1;\nEND;\n/", f));
        Assert.Equal(["f"], Hits("SELECT f(id) FROM item", f));
        // A nested subprogram or a variable of the same name is the unit's own.
        Assert.Empty(Hits("CREATE OR REPLACE PROCEDURE app.x AS\n  PROCEDURE p IS BEGIN NULL; END;\nBEGIN\n  p;\nEND;\n/", p));
        Assert.Empty(Hits("CREATE OR REPLACE PROCEDURE app.x AS\n  p NUMBER;\nBEGIN\n  p := 1;\nEND;\n/", p));
    }

    [Fact]
    public void A_units_own_name_in_its_header_and_after_END_is_the_definition()
    {
        const string sql = "CREATE OR REPLACE EDITIONABLE PROCEDURE APP.P (a NUMBER) AS\nBEGIN\n  NULL;\nEND p;\n/";
        var hits = F.Find(sql, new RefTarget(RefKind.Procedure, "APP", "P"), Ctx()).Hits;
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.Definition));
        Assert.Equal("p", sql.Substring(hits[1].Offset, hits[1].Length));
        // The header of a view is not the procedure of the same name.
        Assert.Empty(F.Find("CREATE OR REPLACE VIEW APP.P AS SELECT 1 FROM dual;", new RefTarget(RefKind.Procedure, "APP", "P"), Ctx()).Hits);
    }

    [Fact]
    public void Sequences_through_NEXTVAL_and_CURRVAL()
    {
        var seq = new RefTarget(RefKind.Sequence, "APP", "SEQ");
        Assert.Equal(["seq", "seq"], Hits("INSERT INTO item (id) VALUES (seq.NEXTVAL); SELECT app.seq.CURRVAL FROM dual", seq));
        Assert.Empty(Hits("SELECT seq FROM item", seq));
    }

    [Theory]
    [InlineData("BEGIN EXECUTE IMMEDIATE 'DELETE FROM item'; END;", true)]
    [InlineData("BEGIN DBMS_SQL.PARSE(c, s, DBMS_SQL.NATIVE); END;", true)]
    [InlineData("DECLARE c SYS_REFCURSOR; BEGIN OPEN c FOR v_sql; END;", true)]
    [InlineData("DECLARE c SYS_REFCURSOR; BEGIN OPEN c FOR SELECT * FROM item; END;", false)]
    public void Dynamic_SQL_is_flagged(string sql, bool dynamic) => Assert.Equal(dynamic, F.Find(sql, Table(), Ctx()).DynamicSql);

    [Fact]
    public void The_target_at_the_cursor()
    {
        static RefTarget? At(string sql, string word, int nth = 0)
        {
            var at = -1;
            for (var i = 0; i <= nth; i++) at = sql.IndexOf(word, at + 1, StringComparison.Ordinal);
            return F.TargetAt(sql, at + 1, Ctx());
        }
        Assert.Equal(Table(), At("SELECT qty FROM item i", "item"));
        Assert.Equal(Column("QTY"), At("SELECT qty FROM item i", "qty"));
        Assert.Equal(Column("QTY"), At("SELECT i.qty FROM item i", "qty"));
        Assert.Equal(Table(), At("SELECT app.item.qty FROM app.item", "item"));
        Assert.Equal(new RefTarget(RefKind.Sequence, "APP", "SEQ"), At("SELECT seq.NEXTVAL FROM dual", "seq"));
        Assert.Equal(new RefTarget(RefKind.Function, "APP", "F"), At("SELECT f(1) FROM dual", "f"));
        Assert.Equal(new RefTarget(RefKind.Procedure, "APP", "P"), At("BEGIN p(1); END;", "p"));
        Assert.Null(At("SELECT 'item' FROM dual", "item"));
        Assert.Null(At("SELECT * FROM item@lnk", "item"));
        Assert.Equal(new RefTarget(RefKind.View, "APP", "V"), At("CREATE OR REPLACE VIEW app.v AS SELECT 1 FROM dual", "v AS"));
    }

    [Fact]
    public void Local_symbols_aliases_CTEs_and_parameters()
    {
        const string alias = "SELECT i.id, i.qty FROM item i WHERE i.price > 0";
        Assert.Equal(4, F.LocalOccurrences(alias, alias.IndexOf("i.id", StringComparison.Ordinal))!.Count);
        const string cte = "WITH c AS (SELECT id FROM item) SELECT c.id FROM c";
        Assert.Equal(3, F.LocalOccurrences(cte, cte.IndexOf("c AS", StringComparison.Ordinal))!.Count);
        const string unit = "CREATE OR REPLACE PROCEDURE app.p (p_qty NUMBER) AS\n  total NUMBER := 0;\nBEGIN\n  total := total + p_qty;\nEND;\n/";
        Assert.Equal(2, F.LocalOccurrences(unit, unit.IndexOf("p_qty", StringComparison.Ordinal))!.Count);
        Assert.Equal(3, F.LocalOccurrences(unit, unit.IndexOf("total", StringComparison.Ordinal))!.Count);
        Assert.Null(F.LocalOccurrences("SELECT qty FROM item", 8));
    }

    [Theory]
    [InlineData("qty", "QUANTITY", "QUANTITY")]
    [InlineData("\"QTY\"", "QUANTITY", "\"QUANTITY\"")]
    [InlineData("qty", "Quantity", "\"Quantity\"")]
    [InlineData("qty", "SELECT", "\"SELECT\"")]
    [InlineData("qty", "NEW NAME", "\"NEW NAME\"")]
    public void A_new_name_is_bare_where_it_can_be(string original, string newName, string expected) => Assert.Equal(expected, F.Rename(original, newName));

    [Theory]
    [InlineData("quantity", "QUANTITY")]
    [InlineData("  Quantity ", "QUANTITY")]
    [InlineData("\"Quantity\"", "Quantity")]
    public void A_typed_new_name_folds_as_Oracle_reads_it(string typed, string catalog) => Assert.Equal(catalog, F.CatalogName(typed));

    [Fact]
    public void Nothing_follows_a_rename_by_itself_and_an_impossible_name_is_refused()
    {
        Assert.False(F.ServerTracked("CREATE VIEW v AS SELECT 1 FROM dual", ObjectKind.View));
        Assert.Throws<ArgumentException>(() => F.Rename("x", "a\"b"));
    }
}
