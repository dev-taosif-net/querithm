using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;

namespace Querithm.Tests;

/// <summary>Phase D2: MySQL / MariaDB lexing, client DELIMITER splitting and classification, on tokens only.</summary>
public class MySqlIntelligenceTests
{
    static readonly MySqlLanguageService L = new();

    [Fact]
    public void Comments_need_a_space_after_the_dashes_and_hash_comments_count()
    {
        var code = MySqlTokenizer.Tokenize("SELECT 1--2, 3 -- note\n# hash\nFROM t").Where(t => !t.IsTrivia).Select(t => t.Text).ToList();
        Assert.Equal(["SELECT", "1", "--", "2", ",", "3", "FROM", "t"], code); // "1--2" is 1 minus minus 2
    }

    [Fact]
    public void Double_quotes_are_strings_unless_ansi_quotes()
    {
        Assert.Equal(MySqlTokenKind.String, MySqlTokenizer.Tokenize("\"x\"").Single().Kind);
        Assert.Equal(MySqlTokenKind.QuotedIdent, MySqlTokenizer.Tokenize("\"x\"", new MySqlLexMode(AnsiQuotes: true)).Single().Kind);
        Assert.Equal("a`b", MySqlTokenizer.Unquote(MySqlTokenizer.Tokenize("`a``b`").Single()));
        Assert.Equal("`we``ird`", MySqlTokenizer.Quote("we`ird"));
    }

    [Fact]
    public void Backslash_escapes_follow_the_sql_mode()
    {
        // 'a\'b' is one string by default; under NO_BACKSLASH_ESCAPES the backslash is a character and the string ends early.
        Assert.Single(MySqlTokenizer.Tokenize(@"'a\'b'"));
        Assert.NotEqual(1, MySqlTokenizer.Tokenize(@"'a\'b'", new MySqlLexMode(NoBackslashEscapes: true)).Count);
    }

    [Theory]
    [InlineData("/*!50000 DROP TABLE t */", "drop")]
    [InlineData("/*M! DROP TABLE t */", "drop")]
    [InlineData("/*!80000 TRUNCATE TABLE t */", "truncate")]
    [InlineData("/* DROP TABLE t */ SELECT 1", null)]
    [InlineData("SELECT /*+ MAX_EXECUTION_TIME(1000) */ * FROM t", null)]
    [InlineData("DELETE FROM t WHERE id IN (SELECT id FROM u)", null)]
    [InlineData("DELETE FROM t", "deleteWithoutWhere")]
    [InlineData("UPDATE t SET v = (SELECT max(v) FROM u WHERE u.id = 1)", "updateWithoutWhere")]
    [InlineData("ALTER TABLE t ADD c INT", "alter")]
    public void Executable_comments_are_classified_as_code(string sql, string? rule) =>
        Assert.Equal(rule, L.Classify(sql).Destructive.SingleOrDefault()?.Rule);

    [Fact]
    public void Delimiter_keeps_stored_program_bodies_whole()
    {
        var sql = """
            DELIMITER $$
            CREATE PROCEDURE p()
            BEGIN
              DELETE FROM log;
              SELECT 'a;b';
            END$$
            DELIMITER ;
            SELECT 1;
            SELECT 2
            """;
        var c = L.Classify(sql);
        Assert.Equal(3, c.Batches.Length);
        Assert.Equal(2, c.Batches[0].StartLine);
        Assert.EndsWith("END", sql.Substring(c.Batches[0].StartOffset, c.Batches[0].Length));
        Assert.True(c.HasDdl);
        Assert.Empty(c.Destructive); // the DELETE is inside the procedure body, which CREATE PROCEDURE does not run
        Assert.Equal(8, c.Batches[1].StartLine);
    }

    [Fact]
    public void Use_transactions_and_local_infile_are_recognised()
    {
        Assert.Equal("Sales Db", L.Classify("USE `Sales Db`").UseDatabase);
        Assert.True(L.Classify("START TRANSACTION").BeginsTransaction);
        Assert.True(L.Classify("BEGIN").BeginsTransaction);
        Assert.True(L.Classify("COMMIT").EndsTransaction);
        Assert.False(L.Classify("ROLLBACK TO SAVEPOINT s").EndsTransaction);
        Assert.NotNull(L.Classify("LOAD DATA LOCAL INFILE '/etc/passwd' INTO TABLE t").Refusal);
        Assert.Null(L.Classify("LOAD DATA INFILE 'x.csv' INTO TABLE t").Refusal);
        Assert.Null(L.Classify("SELECT 'LOAD DATA LOCAL'").Refusal);
        Assert.True(L.Classify("RENAME TABLE a TO b").HasDdl);
    }

    [Fact]
    public void User_and_system_variables_are_tokens_of_their_own()
    {
        var t = MySqlTokenizer.Tokenize("SET @x = @@session.sql_mode").Where(x => !x.IsTrivia).ToList();
        Assert.Equal(["@x", "@@session.sql_mode"], t.Where(x => x.Kind == MySqlTokenKind.Variable).Select(x => x.Text));
    }

    // ------------------------------------------------------------------------------------------- tier 2 (Phase D2)

    static readonly SchemaSnapshot Shop = new()
    {
        ProfileId = "p", Database = "shop", Fks = [],
        Objects =
        [
            new SnapObject { Id = 1, Schema = "shop", Name = "orders", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "id", Type = "int", Ordinal = 1, Pk = true }, new SnapColumn { Name = "CustomerId", Type = "int", Ordinal = 2 }, new SnapColumn { Name = "order", Type = "varchar(20)", Ordinal = 3 }] },
            new SnapObject { Id = 2, Schema = "shop", Name = "Order Lines", Kind = ObjectKind.Table, Columns = [new SnapColumn { Name = "qty", Type = "int", Ordinal = 1 }] },
            new SnapObject { Id = 3, Schema = "shop", Name = "add_order", Kind = ObjectKind.Proc,
                Params = [new SnapParam { Name = "p_customer", Type = "int", Ordinal = 1 }, new SnapParam { Name = "p_qty", Type = "int", Ordinal = 2 }] },
        ],
    };

    static readonly SchemaSnapshot Crm = new()
    {
        ProfileId = "p", Database = "crm", Fks = [],
        Objects = [new SnapObject { Id = 9, Schema = "crm", Name = "region", Kind = ObjectKind.Table, Columns = [new SnapColumn { Name = "code", Type = "char(2)", Ordinal = 1 }] }],
    };

    /// <summary>One snapshot per database, as the sidecar keeps them for a MySQL profile.</summary>
    sealed class PerDatabase : ISchemaSource
    {
        public SchemaSnapshot? Get(string? profileId, string? database) => database switch { "shop" => Shop, "crm" => Crm, _ => null };
        public IReadOnlyList<string> Databases(string? profileId) => ["shop", "crm"];
    }

    static CompletionItem[] Complete(string marked, MySqlLanguageService? service = null)
    {
        var at = marked.IndexOf('|');
        return (service ?? L).Complete(new CompleteParams { DocId = Guid.NewGuid().ToString(), Text = marked.Remove(at, 1), Offset = at, Dialect = Dialect.MySql, ProfileId = "p", Database = "shop" },
            new PerDatabase());
    }

    static string[] Labels(string marked, MySqlLanguageService? service = null) => Complete(marked, service).Select(c => c.Label).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Tier_two_resolves_backticked_names_case_insensitively_and_inserts_them_in_backticks()
    {
        var cols = Complete("SELECT o.| FROM `ORDERS` o");
        Assert.Equal(["CustomerId", "id", "order"], cols.Select(c => c.Label).Order(StringComparer.Ordinal));
        Assert.Equal("`order`", cols.Single(c => c.Label == "order").InsertText); // a reserved word
        var relations = Complete("SELECT * FROM orders o JOIN |");
        Assert.Equal("`Order Lines`", relations.Single(c => c.Label == "Order Lines").InsertText);
        Assert.Equal(["qty"], Labels("SELECT l.| FROM `Order Lines` AS l"));
        // "…" is a string unless ANSI_QUOTES is on.
        Assert.Empty(Complete("SELECT \"o.|\" FROM orders o"));
        Assert.Equal(["CustomerId", "id", "order"], Labels("SELECT o.| FROM \"orders\" o", new MySqlLanguageService(new MySqlLexMode(AnsiQuotes: true))));
    }

    [Fact]
    public void Use_moves_completion_to_that_databases_snapshot()
    {
        Assert.Contains("region", Labels("USE crm;\nSELECT * FROM |"));
        Assert.DoesNotContain("orders", Labels("USE `crm`;\nSELECT * FROM |"));
        Assert.Equal(["code"], Labels("USE crm; SELECT r.| FROM region r"));
        Assert.Contains("orders", Labels("SELECT * FROM | ; USE crm;")); // a USE after the cursor does not count
    }

    [Fact]
    public void Statements_follow_the_client_delimiter()
    {
        Assert.Empty(Complete("SELECT * FROM orders o; SELECT o.|"));
        Assert.Equal(["CustomerId", "id", "order"], Labels("DELIMITER $$\nCREATE PROCEDURE p() BEGIN DELETE FROM orders WHERE id < 0; SELECT o.| FROM orders o; END$$\nDELIMITER ;"));
        Assert.Equal(["qty"], Labels("INSERT INTO `Order Lines` (|"));
        Assert.Equal(["CustomerId", "order"], Labels("REPLACE INTO orders (id, |"));
    }

    [Fact]
    public void Signature_help_shows_a_stored_procedures_parameters_and_no_named_arguments_are_offered()
    {
        const string text = "CALL add_order(1, ";
        var help = L.Signature(new SignatureParams { DocId = "s", Text = text, Offset = text.Length, Dialect = Dialect.MySql, ProfileId = "p", Database = "shop" }, new PerDatabase());
        Assert.Equal("shop.add_order(p_customer int, p_qty int)", Assert.Single(help.Signatures).Label);
        Assert.Equal(1, help.ActiveParameter);
        Assert.DoesNotContain(Complete("CALL add_order(|"), c => c.Label.EndsWith("=>", StringComparison.Ordinal));
        Assert.Empty(L.Signature(new SignatureParams { DocId = "s", Text = "USE crm; CALL add_order(", Offset = 24, Dialect = Dialect.MySql, ProfileId = "p", Database = "shop" }, new PerDatabase()).Signatures);
    }
}
