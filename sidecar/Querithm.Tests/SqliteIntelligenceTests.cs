using Querithm.Contract;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Tests;

/// <summary>Phase D1: SQLite tokens, statement boundaries and classification — tokens only, never regex over SQL.</summary>
public class SqliteIntelligenceTests
{
    static readonly SqliteLanguageService L = new();

    [Fact]
    public void Tokenizes_quoting_strings_blobs_comments_and_parameters()
    {
        var t = SqliteTokenizer.Tokenize("SELECT \"a\"\"b\", [c d], `e`, 'it''s', X'0A', ?1, :n, @m, $o -- c\n/* x */ 1.5e-3;").Where(x => !x.IsTrivia).ToList();
        Assert.Equal(["a\"b", "c d", "e"], t.Where(x => x.Kind == SqliteTokenKind.QuotedIdent).Select(SqliteTokenizer.Unquote));
        Assert.Equal("'it''s'", t.Single(x => x.Kind == SqliteTokenKind.String).Text);
        Assert.Equal("X'0A'", t.Single(x => x.Kind == SqliteTokenKind.Blob).Text);
        Assert.Equal(["?1", ":n", "@m", "$o"], t.Where(x => x.Kind == SqliteTokenKind.Param).Select(x => x.Text));
        Assert.Equal("1.5e-3", t.Single(x => x.Kind == SqliteTokenKind.Number).Text);
        Assert.Equal("\"we\"\"ird\"", SqliteTokenizer.Quote("we\"ird"));
    }

    [Theory]
    [InlineData("SELECT ?, ?", "SELECT ?1, ?2")]
    [InlineData("SELECT ?5, ?, :a, ?", "SELECT ?5, ?6, :a, ?7")]
    [InlineData("SELECT '?', \"?\", ? -- ?", "SELECT '?', \"?\", ?1 -- ?")]
    [InlineData("SELECT :a", "SELECT :a")]
    public void Bare_parameters_are_numbered_as_sqlite_numbers_them(string sql, string expected) =>
        Assert.Equal(expected, SqliteTokenizer.NumberAnonymousParameters(sql));

    [Fact]
    public void Splits_statements_but_keeps_trigger_bodies_whole()
    {
        var sql = "CREATE TABLE a(x); CREATE TRIGGER tr AFTER INSERT ON a BEGIN UPDATE a SET x = CASE WHEN x > 0 THEN 1 ELSE 0 END; DELETE FROM a WHERE x = 2; END; SELECT ';' FROM a";
        var parts = SqliteSplitter.Split(SqliteTokenizer.Tokenize(sql));
        Assert.Equal(3, parts.Count);
        Assert.StartsWith("CREATE TRIGGER", string.Concat(parts[1].Select(x => x.Text)).Trim());
        Assert.EndsWith("END;", string.Concat(parts[1].Select(x => x.Text)).Trim());
        Assert.Equal(3, L.Classify(sql).Batches.Length);
    }

    [Theory]
    [InlineData("DROP TABLE t", "drop")]
    [InlineData("DELETE FROM t", "deleteWithoutWhere")]
    [InlineData("DELETE FROM t WHERE id IN (SELECT id FROM u)", null)]
    [InlineData("DELETE FROM t AS x WHERE (SELECT 1) = 1", null)]
    [InlineData("UPDATE t SET v = (SELECT v FROM u WHERE u.id = 1)", "updateWithoutWhere")]
    [InlineData("WITH d AS (SELECT 1) DELETE FROM t", "deleteWithoutWhere")]
    [InlineData("ALTER TABLE t ADD COLUMN c", "alter")]
    [InlineData("SELECT * FROM t -- DROP TABLE t", null)]
    [InlineData("SELECT 'DELETE FROM t'", null)]
    public void Destructive_statements_are_found_on_tokens(string sql, string? rule)
    {
        var c = L.Classify(sql);
        Assert.Equal(rule, c.Destructive.SingleOrDefault()?.Rule);
        Assert.Null(c.Refusal);
    }

    [Fact]
    public void Ddl_and_transaction_statements_are_reported()
    {
        Assert.True(L.Classify("CREATE INDEX ix ON t(v)").HasDdl);
        Assert.False(L.Classify("SELECT 1").HasDdl);
        Assert.True(L.Classify("BEGIN IMMEDIATE").BeginsTransaction);
        Assert.True(L.Classify("END TRANSACTION").EndsTransaction);
        Assert.True(L.Classify("ROLLBACK").EndsTransaction);
        Assert.False(L.Classify("ROLLBACK TO sp").EndsTransaction);
    }

    [Theory]
    [InlineData("ATTACH DATABASE 'other.db' AS o")]
    [InlineData("attach 'file:/tmp/x.db?mode=rwc' as x")]
    [InlineData("ATTACH (SELECT 'x' || '.db') AS y")]
    [InlineData("DETACH o")]
    [InlineData("VACUUM INTO '/tmp/copy.db'")]
    [InlineData("VACUUM main INTO :target")]
    [InlineData("SELECT load_extension('evil')")]
    [InlineData("SELECT 1; SELECT LOAD_EXTENSION ('evil', 'init')")]
    [InlineData("PRAGMA journal_mode = WAL")]
    [InlineData("PRAGMA main.trusted_schema = ON")]
    [InlineData("PRAGMA writable_schema(1)")]
    [InlineData("PRAGMA temp_store_directory = '/tmp'")]
    public void The_file_policy_refuses_these_outright(string sql) => Assert.NotNull(L.Classify(sql).Refusal);

    [Theory]
    [InlineData("PRAGMA journal_mode")]
    [InlineData("PRAGMA table_info(t)")]
    [InlineData("SELECT 'ATTACH x' AS s")]
    [InlineData("VACUUM")]
    [InlineData("CREATE TABLE attach (x)")]
    public void Reads_and_look_alikes_are_not_refused(string sql) => Assert.Null(L.Classify(sql).Refusal);

    // ------------------------------------------------------------------------------------------- tier 2 (Phase D1)

    static readonly SchemaSnapshot Sales = new()
    {
        ProfileId = "p", Database = "main", Fks = [],
        Objects =
        [
            new SnapObject { Id = 1, Schema = "main", Name = "Order Items", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "id", Type = "INTEGER", Ordinal = 1, Pk = true }, new SnapColumn { Name = "Qty", Type = "INTEGER", Ordinal = 2 }, new SnapColumn { Name = "group", Type = "TEXT", Ordinal = 3 }] },
            new SnapObject { Id = 2, Schema = "main", Name = "customer", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "id", Type = "INTEGER", Ordinal = 1, Pk = true }, new SnapColumn { Name = "name", Type = "TEXT", Ordinal = 2 }] },
        ],
    };

    static CompletionItem[] Complete(string marked)
    {
        var at = marked.IndexOf('|');
        return L.Complete(new CompleteParams { DocId = Guid.NewGuid().ToString(), Text = marked.Remove(at, 1), Offset = at, Dialect = Dialect.Sqlite, ProfileId = "p", Database = "main" },
            new FakeSchema(Sales, "main"));
    }

    static string[] Labels(string marked) => Complete(marked).Select(c => c.Label).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Tier_two_resolves_aliases_case_insensitively_in_every_quote_style_and_inserts_names_sqlite_reads_back()
    {
        var cols = Complete("SELECT oi.| FROM [Order Items] oi");
        Assert.Equal(["Qty", "group", "id"], cols.Select(c => c.Label).Order(StringComparer.Ordinal));
        Assert.Equal("\"group\"", cols.Single(c => c.Label == "group").InsertText); // a keyword gets quotes
        Assert.Null(cols.Single(c => c.Label == "Qty").InsertText);
        Assert.Equal(["Qty", "group", "id"], Labels("SELECT x.| FROM `order items` AS x"));
        Assert.Equal(["Qty", "group", "id"], Labels("SELECT \"ORDER ITEMS\".| FROM \"Order Items\""));
        var relations = Complete("SELECT * FROM customer c JOIN |");
        Assert.Equal("\"Order Items\"", relations.Single(c => c.Label == "Order Items").InsertText);
        Assert.Contains(relations, c => c.Label == "customer");
    }

    [Fact]
    public void Statements_end_at_semicolons_but_a_trigger_body_is_one_statement()
    {
        Assert.Empty(Complete("SELECT * FROM customer c; SELECT c.|"));
        Assert.Equal(["id", "name"], Labels("CREATE TRIGGER tr AFTER INSERT ON customer BEGIN DELETE FROM customer WHERE id < 0; SELECT x.| FROM customer x; END;"));
        Assert.Empty(Complete("SELECT 'c.|' FROM customer c"));
        Assert.Empty(Complete("SELECT c.id -- c.|\nFROM customer c"));
    }

    [Fact]
    public void Insert_and_replace_column_lists_offer_the_columns_not_yet_listed()
    {
        Assert.Equal(["id", "name"], Labels("INSERT INTO customer (|"));
        Assert.Equal(["name"], Labels("REPLACE INTO customer (id, |"));
        Assert.Equal(["name"], Labels("INSERT OR REPLACE INTO customer (id, |"));
    }

    [Fact]
    public void Signature_help_knows_the_built_in_functions_with_or_without_a_snapshot()
    {
        foreach (var profile in new[] { "p", null })
        {
            const string text = "SELECT substr(name, ";
            var help = L.Signature(new SignatureParams { DocId = "s", Text = text, Offset = text.Length, Dialect = Dialect.Sqlite, ProfileId = profile, Database = "main" }, new FakeSchema(Sales, "main"));
            var sig = Assert.Single(help.Signatures);
            Assert.Equal("substr(X, Y, [Z])", sig.Label);
            Assert.Equal(1, help.ActiveParameter);
            Assert.False(string.IsNullOrEmpty(sig.Documentation));
        }
        const string nested = "SELECT strftime('%Y', date(";
        Assert.StartsWith("date(", Assert.Single(L.Signature(new SignatureParams { DocId = "s", Text = nested, Offset = nested.Length, Dialect = Dialect.Sqlite }, new FakeSchema(null)).Signatures).Label);
        // SQLite calls take no named arguments: none are offered.
        Assert.DoesNotContain(Complete("SELECT substr(|"), c => c.Label.EndsWith("=>", StringComparison.Ordinal));
    }
}
