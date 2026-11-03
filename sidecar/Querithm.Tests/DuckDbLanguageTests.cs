using System.Numerics;
using Querithm.Contract;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.PgSql;
using Querithm.Providers.DuckDb;

namespace Querithm.Tests;

/// <summary>Phase D3: DuckDB tokens (parity with PgTokenizer on DuckDB's lexical differences), classification, origins, value text.</summary>
public class DuckDbLanguageTests
{
    static readonly DuckDbLanguageService L = new();

    static string[] Kinds(string sql) => DuckDbTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).Select(t => $"{t.Kind}:{t.Text}").ToArray();

    [Theory]
    [InlineData("SELECT 7 // 2", "Word:SELECT|Number:7|Operator://|Number:2")]
    [InlineData("SELECT a == b", "Word:SELECT|Word:a|Operator:==|Word:b")]
    [InlineData("SELECT x::INT, list_transform(l, x -> x + 1)", "Word:SELECT|Word:x|Operator:::|Word:INT|Punct:,|Word:list_transform|Punct:(|Word:l|Punct:,|Word:x|Operator:->|Word:x|Operator:+|Number:1|Punct:)")]
    [InlineData("SELECT $name, $1, ?", "Word:SELECT|Param:$name|Punct:,|Param:$1|Punct:,|Operator:?")]
    [InlineData("SELECT E'a\\'b', $$x;y$$, $tag$z$tag$", "Word:SELECT|String:E'a\\'b'|Punct:,|DollarString:$$x;y$$|Punct:,|DollarString:$tag$z$tag$")]
    [InlineData("SELECT /* a /* nested */ ; */ 1", "Word:SELECT|Number:1")]
    [InlineData("SELECT {'a': 1, 'b': [1, 2]}", "Word:SELECT|Operator:{|String:'a'|Operator::|Number:1|Punct:,|String:'b'|Operator::|Punct:[|Number:1|Punct:,|Number:2|Punct:]|Operator:}")]
    [InlineData("SELECT \"MixedCase\" FROM \"t\"\"q\"", "Word:SELECT|QuotedIdent:\"MixedCase\"|Word:FROM|QuotedIdent:\"t\"\"q\"")]
    public void Tokens_cover_duckdb_lexical_forms(string sql, string expected) => Assert.Equal(expected.Split('|'), Kinds(sql));

    [Fact]
    public void Statements_split_on_semicolons_outside_strings_and_bodies_only()
    {
        var c = L.Classify("SELECT 1; SELECT ';', $$a;b$$;\nSELECT 2 SELECT 3");
        Assert.Equal(3, c.Batches.Length);
        Assert.Equal(2, c.Batches[2].StartLine);
    }

    [Theory]
    [InlineData("DELETE FROM t", "deleteWithoutWhere")]
    [InlineData("UPDATE t SET a = 1", "updateWithoutWhere")]
    [InlineData("DROP TABLE t", "drop")]
    [InlineData("TRUNCATE t", "truncate")]
    [InlineData("ALTER TABLE t ADD COLUMN c INT", "alter")]
    [InlineData("CREATE OR REPLACE TABLE t AS SELECT 1", "alter")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t", "deleteWithoutWhere")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t", "deleteWithoutWhere")]
    [InlineData("EXPLAIN (ANALYZE, FORMAT JSON) UPDATE t SET a = 1", "updateWithoutWhere")]
    public void Destructive_statements_are_found(string sql, string rule) => Assert.Equal(rule, Assert.Single(L.Classify(sql).Destructive).Rule);

    [Theory]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("UPDATE t SET a = (SELECT 1 WHERE true) WHERE id = 2")]
    [InlineData("EXPLAIN DELETE FROM t")]
    [InlineData("SELECT * FROM t")]
    [InlineData("FROM t")]
    public void Safe_statements_are_not_destructive(string sql) => Assert.Empty(L.Classify(sql).Destructive);

    [Fact]
    public void Transactions_and_ddl_are_classified()
    {
        Assert.True(L.Classify("BEGIN TRANSACTION").BeginsTransaction);
        Assert.True(L.Classify("COMMIT").EndsTransaction);
        Assert.True(L.Classify("ABORT").EndsTransaction);
        Assert.True(L.Classify("CREATE TABLE t (a INT)").HasDdl);
        Assert.False(L.Classify("SELECT 1").HasDdl);
    }

    [Theory]
    [InlineData("CREATE VIEW v AS SELECT * FROM enable_logging()", "enable_logging")]
    [InlineData("SELECT * FROM main.\"Query\"('x')", "Query")]
    [InlineData("SELECT (SELECT count(*) FROM arrow_scan(1, 2, 3))", "arrow_scan")]
    [InlineData("CREATE TABLE query (a INT)", null)]
    [InlineData("INSERT INTO query (a) VALUES (1)", null)]
    [InlineData("SELECT * FROM t AS query(a, b)", null)]
    [InlineData("CREATE MACRO query(a) AS a + 1", null)]
    [InlineData("SELECT query FROM t", null)]
    public void Stored_definitions_are_checked_for_refused_calls(string sql, string? fn) => Assert.Equal(fn, DuckDbLanguageService.RefusedCall(sql));

    static SchemaSnapshot Snap() => new()
    {
        ProfileId = "p", Database = "db", Fks = [],
        Objects =
        [
            new SnapObject
            {
                Id = 7, Schema = "main", Name = "t", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "id", Type = "INTEGER", Ordinal = 1, Pk = true }, new SnapColumn { Name = "v", Type = "VARCHAR", Ordinal = 2 }],
            },
        ],
    };

    [Theory]
    [InlineData("SELECT * FROM t", "id,v")]
    [InlineData("FROM t WHERE id > 1", "id,v")]
    [InlineData("SELECT v, id FROM main.t ORDER BY id", "v,id")]
    [InlineData("SELECT x.v AS w, x.* FROM t x LIMIT 5", "v,id,v")]
    [InlineData("SELECT \"V\", id + 1 FROM \"T\";", "v,-")]
    public void Origins_are_predicted_for_plain_single_table_selects(string sql, string expected)
    {
        var want = expected.Split(',');
        var origins = DuckDbResultSources.Predict(sql, Snap(), "main", want.Select((_, i) => $"c{i}").ToList());
        Assert.NotNull(origins);
        Assert.Equal(want, origins!.Select(o => o == null ? "-" : o.Column));
        Assert.All(origins.Where(o => o != null), o => Assert.Equal(7, o!.ObjectId));
    }

    [Theory]
    [InlineData("SELECT * FROM t JOIN u ON t.id = u.id")]
    [InlineData("SELECT DISTINCT v FROM t")]
    [InlineData("SELECT v FROM t GROUP BY v")]
    [InlineData("SELECT count(*) FROM t")]
    [InlineData("SELECT * FROM t UNION ALL SELECT * FROM t")]
    [InlineData("SELECT * FROM other")]
    [InlineData("SELECT * FROM db.main.t")]
    [InlineData("SELECT u.* FROM t")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM t")]
    public void Anything_else_predicts_nothing(string sql) => Assert.Null(DuckDbResultSources.Predict(sql, Snap(), "main", ["id", "v"]));

    [Theory]
    [InlineData("12345678901234567890123456789012345678", 18, "12345678901234567890.123456789012345678")]
    [InlineData("-1", 18, "-0.000000000000000001")]
    [InlineData("150", 2, "1.50")]
    [InlineData("0", 2, "0.00")]
    [InlineData("42", 0, "42")]
    public void Decimals_are_exact(string unscaled, int scale, string text) => Assert.Equal(text, DuckDbValues.Decimal(BigInteger.Parse(unscaled), scale));

    [Theory]
    [InlineData(14, 3, 14_706_789_000L, "1 year 2 months 3 days 04:05:06.789")]
    [InlineData(-1, 2, -1_500_000L, "-1 month 2 days -00:00:01.5")]
    [InlineData(0, 0, 0L, "00:00:00")]
    [InlineData(0, 1, 0L, "1 day")]
    [InlineData(0, 0, 90_000_000_000L, "25:00:00")]
    public void Intervals_read_as_duckdb_writes_them(int months, int days, long micros, string text) => Assert.Equal(text, DuckDbValues.Interval(months, days, micros));

    [Fact]
    public void Nested_values_read_as_literals()
    {
        Assert.Equal("[1, NULL, 3]", DuckDbValues.Format(new List<int?> { 1, null, 3 }));
        Assert.Equal("{'a': 1, 'b': ['x', NULL]}", DuckDbValues.Format(new Dictionary<string, object> { ["a"] = 1, ["b"] = new List<string?> { "x", null } }));
        Assert.Equal("{'k'=1}", DuckDbValues.Format(new Dictionary<string, int> { ["k"] = 1 }));
        Assert.Equal("['it''s']", DuckDbValues.Format(new List<string> { "it's" }));
        Assert.Equal(typeof(List<List<int?>>), DuckDbValues.WithNullableValues(typeof(List<List<int>>)));
        Assert.Equal(typeof(Dictionary<string, int?>), DuckDbValues.WithNullableValues(typeof(Dictionary<string, int>)));
        Assert.Null(DuckDbValues.WithNullableValues(typeof(List<string>)));
    }

    static readonly SchemaSnapshot Shop = new()
    {
        ProfileId = "p", Database = "shop", Fks = [],
        Objects =
        [
            new SnapObject { Id = 1, Schema = "main", Name = "Orders", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "Id", Type = "INTEGER", Ordinal = 1, Pk = true }, new SnapColumn { Name = "CustomerId", Type = "INTEGER", Ordinal = 2 }, new SnapColumn { Name = "order", Type = "VARCHAR", Ordinal = 3 }] },
            new SnapObject { Id = 2, Schema = "crm", Name = "region", Kind = ObjectKind.Table, Columns = [new SnapColumn { Name = "code", Type = "VARCHAR", Ordinal = 1 }] },
            new SnapObject { Id = 3, Schema = "main", Name = "twice", Kind = ObjectKind.Func, Params = [new SnapParam { Name = "x", Type = "", Ordinal = 1 }] },
        ],
    };

    static CompletionItem[] Complete(string marked)
    {
        var at = marked.IndexOf('|');
        return L.Complete(new CompleteParams { DocId = Guid.NewGuid().ToString(), Text = marked.Remove(at, 1), Offset = at, Dialect = Dialect.DuckDb, ProfileId = "p", Database = "shop" },
            new FakeSchema(Shop, "shop"));
    }

    [Fact]
    public void Tier_two_resolves_aliases_case_insensitively_and_inserts_names_as_duckdb_reads_them()
    {
        var cols = Complete("SELECT o.| FROM orders o");
        Assert.Equal(["CustomerId", "Id", "order"], cols.Select(c => c.Label).Order(StringComparer.Ordinal));
        Assert.Null(cols.Single(c => c.Label == "CustomerId").InsertText); // case kept, no quotes needed
        Assert.Equal("\"order\"", cols.Single(c => c.Label == "order").InsertText);
        Assert.Contains(Complete("SELECT \"ORDERS\".| FROM \"ORDERS\""), c => c.Label == "CustomerId");
        var relations = Complete("SELECT * FROM |").Select(c => c.Label).ToList();
        Assert.Contains("Orders", relations);
        Assert.Contains("crm.region", relations);
    }

    [Fact]
    public void Use_and_set_schema_move_the_default_schema_for_completion()
    {
        var after = Complete("USE crm;\nSELECT * FROM |").Select(c => c.Label).ToList();
        Assert.Contains("region", after);
        Assert.Contains("main.Orders", after);
        Assert.Contains("region", Complete("SET schema = 'crm'; SELECT * FROM |").Select(c => c.Label));
        Assert.Contains("region", Complete("USE shop.crm; SELECT * FROM |").Select(c => c.Label));
        Assert.Contains("Orders", Complete("USE shop; SELECT * FROM |").Select(c => c.Label)); // a catalog, not a schema
        Assert.Contains("Orders", Complete("SELECT * FROM | ; USE crm;").Select(c => c.Label)); // a USE after the cursor does not count
    }

    [Fact]
    public void Dollar_quoted_text_is_a_string_in_duckdb_and_gets_no_completion()
    {
        Assert.Empty(Complete("SELECT $$ SELECT o.| FROM orders o $$"));
        Assert.Empty(Complete("SELECT 'o.|' FROM orders o"));
    }

    [Fact]
    public void Signature_help_shows_a_macros_parameters()
    {
        const string text = "SELECT twice(";
        var help = L.Signature(new SignatureParams { DocId = "d", Text = text, Offset = text.Length, Dialect = Dialect.DuckDb, ProfileId = "p", Database = "shop" }, new FakeSchema(Shop, "shop"));
        Assert.Equal("main.twice(x)", Assert.Single(help.Signatures).Label);
    }

    [Theory]
    [InlineData("[x]", "x")]
    [InlineData("[x, \"Y z\"]", "x|Y z")]
    [InlineData("[lower(x), y]", "(expression)|y")]
    public void Index_expressions_name_their_columns(string text, string expected) => Assert.Equal(expected.Split('|'), DuckDbMetadata.IndexColumns(text));

    [Fact]
    public void Modify_opens_views_and_macros_as_create_or_replace()
    {
        var m = new DuckDbMetadata();
        Assert.Equal("CREATE OR REPLACE VIEW v AS SELECT 1;", m.ToEditableScript("CREATE VIEW v AS SELECT 1;", null!));
        Assert.Equal("CREATE OR REPLACE TEMP VIEW v AS SELECT 1;", m.ToEditableScript("CREATE TEMP VIEW v AS SELECT 1", null!));
        Assert.Equal("CREATE OR REPLACE MACRO \"main\".\"m\"(a) AS a + 1;", m.ToEditableScript("CREATE MACRO \"main\".\"m\"(a) AS a + 1;", null!));
        Assert.Equal("CREATE TABLE t (a INT);", m.ToEditableScript("CREATE TABLE t (a INT);", null!));
    }
}
