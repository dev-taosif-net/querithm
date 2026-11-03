using Querithm.Contract;
using Querithm.Intelligence.PgSql;

namespace Querithm.Tests;

/// <summary>The tolerant PostgreSQL tokenizer: lossless, never throws, and keeps quoting rules exact.</summary>
public class PgTokenizerTests
{
    static List<PgToken> T(string sql) => PgTokenizer.Tokenize(sql);

    [Fact]
    public void Empty_text_has_no_tokens_and_no_statements()
    {
        Assert.Empty(T(""));
        Assert.Empty(PgTokenizer.Statements(T("")));
    }

    [Theory]
    [InlineData("SELECT a.b, \"Mixed\"\"Case\" FROM t WHERE x::int >= $1 -- tail")]
    [InlineData("/* a /* nested */ still */ SELECT E'\\n', 'it''s', $q$ body $q$;")]
    [InlineData("INSERT INTO t VALUES (1.5e10, .5, -3);\r\nSELECT :name")]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT $body$ never closed")]
    [InlineData("/* never closed /* nested")]
    public void Tokens_cover_the_text_exactly(string sql)
    {
        var tokens = T(sql);
        Assert.Equal(sql, string.Concat(tokens.Select(t => t.Text)));
        var at = 0;
        foreach (var t in tokens)
        {
            Assert.Equal(at, t.Offset);
            Assert.Equal(t.Text.Length, t.Length);
            at += t.Length;
        }
    }

    [Theory]
    [InlineData("'abc", PgTokenKind.String)]
    [InlineData("\"abc", PgTokenKind.QuotedIdent)]
    [InlineData("$x$ abc", PgTokenKind.DollarString)]
    [InlineData("/* abc", PgTokenKind.Comment)]
    [InlineData("-- abc", PgTokenKind.Comment)]
    public void Unterminated_constructs_run_to_the_end(string sql, PgTokenKind kind)
    {
        var t = Assert.Single(T(sql));
        Assert.Equal(kind, t.Kind);
        Assert.Equal(sql, t.Text);
    }

    [Fact]
    public void Doubled_quotes_stay_inside_strings_and_identifiers()
    {
        var t = T("'it''s' \"a\"\"b\"").Where(x => !x.IsTrivia).ToList();
        Assert.Equal((PgTokenKind.String, "'it''s'"), (t[0].Kind, t[0].Text));
        Assert.Equal((PgTokenKind.QuotedIdent, "\"a\"\"b\""), (t[1].Kind, t[1].Text));
    }

    [Fact]
    public void Lowercase_e_prefix_is_an_escape_string()
    {
        var t = Assert.Single(T("e'a\\'b'"));
        Assert.Equal(PgTokenKind.String, t.Kind);
    }

    [Fact]
    public void Plain_strings_do_not_treat_backslash_as_escape()
    {
        // standard_conforming_strings: 'a\' is a complete literal.
        var t = T("'a\\' x").Where(x => !x.IsTrivia).ToList();
        Assert.Equal("'a\\'", t[0].Text);
        Assert.Equal(PgTokenKind.Word, t[1].Kind);
    }

    [Fact]
    public void Escape_string_ending_in_backslash_does_not_throw()
    {
        var t = Assert.Single(T("E'abc\\"));
        Assert.Equal(PgTokenKind.String, t.Kind);
    }

    [Theory]
    [InlineData("$1", PgTokenKind.Param)]
    [InlineData("$12", PgTokenKind.Param)]
    [InlineData(":name", PgTokenKind.Param)]
    [InlineData(":_x1", PgTokenKind.Param)]
    [InlineData("$$ body $$", PgTokenKind.DollarString)]
    [InlineData("$fn_1$ body $fn_1$", PgTokenKind.DollarString)]
    [InlineData("a$b", PgTokenKind.Word)]
    [InlineData("_under", PgTokenKind.Word)]
    [InlineData("1.5e10", PgTokenKind.Number)]
    [InlineData(".5", PgTokenKind.Number)]
    [InlineData("::", PgTokenKind.Operator)]
    [InlineData("<>", PgTokenKind.Operator)]
    [InlineData("->>", PgTokenKind.Operator)]
    public void Single_token_kinds(string sql, PgTokenKind kind)
    {
        var t = Assert.Single(T(sql));
        Assert.Equal(kind, t.Kind);
    }

    [Fact]
    public void Dollar_tag_must_match_exactly_to_close()
    {
        var t = T("$a$ $b$ ; $a$ x").Where(x => !x.IsTrivia).ToList();
        Assert.Equal("$a$ $b$ ; $a$", t[0].Text);
        Assert.Equal("x", t[1].Text);
    }

    [Fact]
    public void Operators_stop_before_comments()
    {
        var t = T("a+--c\nb*/*x*/").ToList();
        Assert.Contains(t, x => x.Kind == PgTokenKind.Operator && x.Text == "+");
        Assert.Contains(t, x => x.Kind == PgTokenKind.Comment && x.Text == "--c");
        Assert.Contains(t, x => x.Kind == PgTokenKind.Operator && x.Text == "*");
        Assert.Contains(t, x => x.Kind == PgTokenKind.Comment && x.Text == "/*x*/");
    }

    [Fact]
    public void Lines_advance_through_multiline_tokens()
    {
        var t = T("SELECT 'a\nb',\n$$\n\n$$, /* c\n */ x").Where(x => !x.IsTrivia).ToList();
        Assert.Equal(1, t[0].Line);                      // SELECT
        Assert.Equal(3, t.First(x => x.Kind == PgTokenKind.DollarString).Line);
        Assert.Equal(6, t.Single(x => x.Text == "x").Line);
    }

    [Fact]
    public void Is_matches_words_case_insensitively_but_not_other_kinds()
    {
        var t = T("select \"select\" 'select'").Where(x => !x.IsTrivia).ToList();
        Assert.True(t[0].Is("SELECT"));
        Assert.False(t[1].Is("SELECT"));
        Assert.False(t[2].Is("SELECT"));
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2", 2)]
    [InlineData("SELECT 1;;; ;", 1)]
    [InlineData(";;", 0)]
    [InlineData("-- only a comment;\n/* ; */", 0)]
    [InlineData("SELECT f(1; 2); SELECT 3", 2)]
    [InlineData("SELECT ';'; SELECT $$;$$", 2)]
    [InlineData("SELECT 1)); SELECT 2", 2)]
    public void Statements_split_on_top_level_semicolons(string sql, int count)
    {
        Assert.Equal(count, PgTokenizer.Statements(T(sql)).Count);
    }

    // Regression: SQL Server runs newline-separated statements, PostgreSQL rejected them as one statement.
    [Theory]
    [InlineData("SELECT * FROM hr.employees emp\nSELECT * FROM hr.departments dep")]
    [InlineData("SELECT 1\nSELECT 2")]
    [InlineData("SELECT * FROM t ORDER BY a DESC\nSELECT 2")]
    [InlineData("SELECT * FROM t WHERE x IS NULL\nUPDATE t SET a = 1 WHERE b = 2")]
    [InlineData("INSERT INTO t VALUES (1) RETURNING *\nDELETE FROM t WHERE a = 1")]
    [InlineData("INSERT INTO t (a) SELECT 1\nSELECT 2")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x\nWITH y AS (SELECT 2) SELECT * FROM y")]
    [InlineData("CREATE TABLE t (id int)\nINSERT INTO t VALUES (1)")]
    [InlineData("DROP TABLE IF EXISTS a\nDROP INDEX CONCURRENTLY ix")]
    [InlineData("ALTER TABLE t DROP COLUMN c\nALTER TABLE u ADD COLUMN d int")]
    [InlineData("SET search_path = hr\nSELECT 1")]
    [InlineData("SELECT 1\nSET search_path TO hr")]
    [InlineData("SHOW search_path\nSELECT 1")]
    [InlineData("BEGIN\nUPDATE t SET a = 1 WHERE id = 2")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2\nCOMMIT")]
    [InlineData("SELECT 1\nDO $$ BEGIN END $$")]
    [InlineData("PREPARE p (int) AS SELECT $1\nEXECUTE p (1)")]
    [InlineData("EXPLAIN SELECT 1\nSELECT 2")]
    [InlineData("SELECT CASE WHEN a THEN 1 END FROM t\nTRUNCATE TABLE t")]
    [InlineData("SELECT 1\nCOMMENT ON TABLE t IS 'x'")]
    [InlineData("(SELECT 1)\nSELECT 2")]
    public void Statements_split_before_a_new_statement_without_semicolon(string sql)
    {
        Assert.Equal(2, PgTokenizer.Statements(T(sql)).Count);
    }

    // Statement keywords that continue the statement (or are column names) must not split it.
    [Theory]
    [InlineData("SELECT 1 UNION SELECT 2")]
    [InlineData("SELECT 1 UNION ALL\nSELECT 2")]
    [InlineData("INSERT INTO t\nSELECT * FROM u")]
    [InlineData("INSERT INTO t WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("WITH x AS (SELECT 1)\nSELECT * FROM x")]
    [InlineData("WITH d AS (SELECT 1) DELETE FROM t WHERE id IN (SELECT * FROM d)")]
    [InlineData("CREATE VIEW v AS\nSELECT 1")]
    [InlineData("CREATE VIEW v AS WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("CREATE TABLE t AS SELECT 1 WITH NO DATA")]
    [InlineData("EXPLAIN ANALYZE SELECT 1")]
    [InlineData("EXPLAIN (ANALYZE, BUFFERS) DELETE FROM t")]
    [InlineData("VACUUM ANALYZE t")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT * FROM t FOR NO KEY UPDATE SKIP LOCKED")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id) DO UPDATE SET a = 1")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT DO NOTHING")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = 1 WHEN NOT MATCHED THEN INSERT (id) VALUES (s.id)")]
    [InlineData("WITH s AS (SELECT 1 AS id) MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE")]
    [InlineData("CREATE TRIGGER g AFTER INSERT OR UPDATE OF a ON t FOR EACH ROW EXECUTE FUNCTION f()")]
    [InlineData("CREATE TRIGGER g INSTEAD OF DELETE ON v FOR EACH ROW EXECUTE PROCEDURE f()")]
    [InlineData("CREATE RULE r AS ON INSERT TO t DO INSTEAD INSERT INTO u VALUES (1)")]
    [InlineData("CREATE POLICY p ON t FOR SELECT USING (true)")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DELETE ROWS")]
    [InlineData("CREATE FUNCTION f() RETURNS int SET search_path = hr AS $$ SELECT 1 $$ LANGUAGE sql")]
    [InlineData("CREATE SCHEMA hr\nCREATE TABLE hr.t (id int)")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES u (a) ON DELETE CASCADE ON UPDATE SET NULL")]
    [InlineData("ALTER TABLE t ALTER COLUMN c DROP DEFAULT")]
    [InlineData("ALTER TABLE t ALTER index TYPE text")]
    [InlineData("ALTER TABLE t DROP index")]
    [InlineData("ALTER TABLE t DROP CONSTRAINT c, DROP COLUMN d")]
    [InlineData("ALTER PUBLICATION p DROP TABLE t")]
    [InlineData("ALTER EXTENSION e DROP FUNCTION f()")]
    [InlineData("ALTER GROUP g DROP USER u")]
    [InlineData("ALTER ROLE r SET search_path TO hr")]
    [InlineData("ALTER DEFAULT PRIVILEGES IN SCHEMA s GRANT SELECT ON TABLES TO r")]
    [InlineData("GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE ON t TO r")]
    [InlineData("GRANT r TO u WITH GRANT OPTION")]
    [InlineData("REVOKE GRANT OPTION FOR SELECT ON t FROM r")]
    [InlineData("DECLARE c CURSOR FOR SELECT 1")]
    [InlineData("PREPARE p AS SELECT 1")]
    [InlineData("CREATE TABLE t2 AS EXECUTE p")]
    [InlineData("ROLLBACK TO SAVEPOINT s")]
    [InlineData("RELEASE SAVEPOINT s")]
    [InlineData("START TRANSACTION ISOLATION LEVEL SERIALIZABLE")]
    [InlineData("CREATE SEQUENCE s START WITH 1")]
    [InlineData("SELECT * FROM t OFFSET 5 FETCH NEXT 10 ROWS ONLY")]
    [InlineData("SELECT comment, update, start, begin FROM t")]
    [InlineData("SELECT t.update, t.delete FROM t")]
    [InlineData("SELECT x AS comment FROM t")]
    [InlineData("SELECT a = SELECT")]
    public void Statement_keywords_that_continue_the_statement_do_not_split(string sql)
    {
        Assert.Single(PgTokenizer.Statements(T(sql)));
    }
}

/// <summary>PostgreSQL classification for the production guard, batches and folding — all on tokens.</summary>
public class PgLanguageServiceTests
{
    static readonly PgLanguageService Pg = new();

    [Theory]
    [InlineData("delete from orders", "deleteWithoutWhere")]
    [InlineData("UPDATE t SET x = 1 FROM u", "updateWithoutWhere")]
    [InlineData("DELETE FROM t USING (SELECT 1 WHERE true) s", "deleteWithoutWhere")]
    [InlineData("WITH old AS (SELECT 1) UPDATE t SET x = 1", "updateWithoutWhere")]
    [InlineData("drop index if exists ix", "drop")]
    [InlineData("TRUNCATE TABLE a, b", "truncate")]
    [InlineData("ALTER TABLE t ADD COLUMN c int", "alter")]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT 1", "alter")]
    public void Destructive_rules(string sql, string rule)
    {
        Assert.Equal(rule, Assert.Single(Pg.Classify(sql).Destructive).Rule);
    }

    [Theory]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t WHERE id IN (SELECT * FROM x)")]
    [InlineData("CREATE TABLE t (id int)")]
    [InlineData("SELECT * FROM t; -- DROP TABLE t")]
    [InlineData("INSERT INTO t SELECT * FROM u")]
    public void Safe_statements_have_no_findings(string sql) => Assert.Empty(Pg.Classify(sql).Destructive);

    [Fact]
    public void Delete_inside_a_cte_is_flagged()
    {
        Assert.Single(Pg.Classify("WITH d AS (DELETE FROM orders RETURNING *) SELECT count(*) FROM d").Destructive);
    }

    [Fact]
    public void Delete_inside_a_do_block_is_flagged()
    {
        Assert.NotEmpty(Pg.Classify("DO $$ BEGIN DELETE FROM orders; END $$").Destructive);
    }

    [Fact]
    public void Explain_analyze_delete_is_flagged()
    {
        Assert.NotEmpty(Pg.Classify("EXPLAIN ANALYZE DELETE FROM orders").Destructive);
    }

    [Theory]
    [InlineData("WITH a AS (SELECT 1), d AS (DELETE FROM orders RETURNING *) SELECT * FROM d", "deleteWithoutWhere")]
    [InlineData("WITH d (id) AS (UPDATE orders SET x = 1 RETURNING id) SELECT 1", "updateWithoutWhere")]
    [InlineData("WITH d AS NOT MATERIALIZED (DELETE FROM orders RETURNING *) SELECT 1", "deleteWithoutWhere")]
    [InlineData("WITH o AS (WITH d AS (DELETE FROM orders RETURNING *) SELECT * FROM d) SELECT * FROM o", "deleteWithoutWhere")]
    [InlineData("WITH d AS (DELETE FROM orders RETURNING *) INSERT INTO archive SELECT * FROM d", "deleteWithoutWhere")]
    [InlineData("EXPLAIN ANALYSE DELETE FROM orders", "deleteWithoutWhere")]
    [InlineData("EXPLAIN ANALYZE VERBOSE UPDATE orders SET x = 1", "updateWithoutWhere")]
    [InlineData("EXPLAIN (ANALYZE, BUFFERS) DELETE FROM orders", "deleteWithoutWhere")]
    [InlineData("explain (format json, analyze true) delete from orders", "deleteWithoutWhere")]
    [InlineData("EXPLAIN ANALYZE WITH d AS (DELETE FROM orders RETURNING *) SELECT 1", "deleteWithoutWhere")]
    [InlineData("DO LANGUAGE plpgsql $body$ BEGIN IF true THEN DROP TABLE t; END IF; END $body$", "drop")]
    [InlineData("DO 'BEGIN TRUNCATE t; END'", "truncate")]
    [InlineData("DO $$ DECLARE n int; BEGIN SELECT 1 INTO n; LOOP UPDATE t SET x = n; EXIT; END LOOP; END $$", "updateWithoutWhere")]
    [InlineData("DO $$ BEGIN <<outer>> BEGIN DELETE FROM t; END; EXCEPTION WHEN others THEN NULL; END $$", "deleteWithoutWhere")]
    [InlineData("DO $$ BEGIN CREATE OR REPLACE VIEW v AS SELECT 1; END $$ LANGUAGE plpgsql", "alter")]
    public void Statements_that_run_inside_another_are_flagged(string sql, string rule)
    {
        Assert.Equal(rule, Assert.Single(Pg.Classify(sql).Destructive).Rule);
    }

    [Theory]
    [InlineData("WITH d AS (DELETE FROM orders WHERE id = 1 RETURNING *) SELECT * FROM d")]
    [InlineData("WITH d (a, b) AS (SELECT 1, 2) SELECT * FROM d")]
    [InlineData("EXPLAIN DELETE FROM orders")]
    [InlineData("EXPLAIN VERBOSE DELETE FROM orders")]
    [InlineData("EXPLAIN (ANALYZE false) DELETE FROM orders")]
    [InlineData("EXPLAIN (ANALYZE off, COSTS) DELETE FROM orders")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM orders WHERE id = 1")]
    [InlineData("DO $$ BEGIN DELETE FROM orders WHERE id = 1; INSERT INTO t VALUES (1); END $$")]
    [InlineData("DO $$ DECLARE r record; BEGIN SELECT * INTO r FROM t FOR UPDATE; END $$")]
    [InlineData("DO $$ BEGIN CREATE TABLE t (id int REFERENCES u ON DELETE CASCADE ON UPDATE CASCADE); END $$")]
    [InlineData("DO $$ BEGIN RAISE NOTICE 'DELETE FROM t'; END $$")]
    public void Statements_inside_others_that_are_safe_are_not_flagged(string sql) => Assert.Empty(Pg.Classify(sql).Destructive);

    [Fact]
    public void Do_block_finding_points_at_the_line_inside_the_body_and_says_it_runs_now()
    {
        var c = Pg.Classify("SELECT 1;\nDO $$\nBEGIN\n  DELETE FROM orders;\nEND $$");
        var f = Assert.Single(c.Destructive);
        Assert.Equal(4, f.Line);
        Assert.Contains("DO block", f.Description);
        Assert.StartsWith("DO $$", f.Statement);
        Assert.False(c.BeginsTransaction);
        Assert.False(c.EndsTransaction);
    }

    [Fact]
    public void Explain_analyze_finding_says_the_statement_runs()
    {
        Assert.Contains("EXPLAIN ANALYZE runs the statement", Assert.Single(Pg.Classify("EXPLAIN ANALYZE TRUNCATE t").Destructive).Description);
    }

    [Theory]
    [InlineData("DO")]
    [InlineData("DO $$")]
    [InlineData("DO $$ BEGIN DELETE")]
    [InlineData("DO LANGUAGE plpgsql")]
    [InlineData("DO E'")]
    [InlineData("EXPLAIN")]
    [InlineData("EXPLAIN (ANALYZE")]
    [InlineData("WITH d AS (")]
    [InlineData("WITH d AS (DELETE FROM t")]
    public void Unfinished_nested_statements_do_not_throw(string sql) => Pg.Classify(sql);

    [Fact]
    public void Finding_carries_line_snippet_and_object_kind()
    {
        var f = Assert.Single(Pg.Classify("SELECT 1;\n\n  DROP   TABLE\n  orders;").Destructive);
        Assert.Equal(3, f.Line);
        Assert.Equal("DROP TABLE orders", f.Statement);
        Assert.Contains("DROP TABLE", f.Description);
    }

    [Fact]
    public void Long_statement_snippet_is_truncated()
    {
        var f = Assert.Single(Pg.Classify("DELETE FROM " + new string('x', 400)).Destructive);
        Assert.Equal(201, f.Statement.Length);
        Assert.EndsWith("…", f.Statement);
    }

    [Theory]
    [InlineData("BEGIN; UPDATE t SET x = 1 WHERE id = 2; COMMIT;", true, true)]
    [InlineData("START TRANSACTION", true, false)]
    [InlineData("ROLLBACK", false, true)]
    [InlineData("END", false, true)]
    [InlineData("ABORT", false, true)]
    [InlineData("SELECT 'BEGIN'", false, false)]
    public void Transaction_boundaries(string sql, bool begins, bool ends)
    {
        var c = Pg.Classify(sql);
        Assert.Equal((begins, ends), (c.BeginsTransaction, c.EndsTransaction));
    }

    [Theory]
    [InlineData("COMMENT ON TABLE t IS 'x'")]
    [InlineData("GRANT SELECT ON t TO r")]
    [InlineData("REVOKE ALL ON t FROM r")]
    [InlineData("CREATE INDEX ix ON t (a)")]
    public void Ddl_without_findings(string sql)
    {
        var c = Pg.Classify(sql);
        Assert.True(c.HasDdl);
        Assert.Empty(c.Destructive);
    }

    [Theory]
    [InlineData("\\c sales", "sales")]
    [InlineData("\\connect other", "other")]
    [InlineData("\\c", null)]
    public void Psql_connect_meta_command_sets_the_database(string sql, string? db)
    {
        Assert.Equal(db, Pg.Classify(sql).UseDatabase);
    }

    [Fact]
    public void One_batch_per_statement_with_trimmed_offsets_and_lines()
    {
        var sql = "SELECT 1;\n\n  SELECT\n  2 ;\n-- trailing comment";
        var b = Pg.Classify(sql).Batches;
        Assert.Equal(2, b.Length);
        Assert.Equal("SELECT 1", sql.Substring(b[0].StartOffset, b[0].Length));
        Assert.Equal("SELECT\n  2", sql.Substring(b[1].StartOffset, b[1].Length));
        Assert.Equal((3, 4), (b[1].StartLine, b[1].EndLine));
        Assert.Equal([0, 1], b.Select(x => x.Index));
    }

    [Fact]
    public void Statements_without_semicolons_become_separate_batches()
    {
        var sql = "SELECT * FROM hr.employees emp\nSELECT * FROM hr.departments dep";
        var b = Pg.Classify(sql).Batches;
        Assert.Equal(2, b.Length);
        Assert.Equal("SELECT * FROM hr.employees emp", sql.Substring(b[0].StartOffset, b[0].Length));
        Assert.Equal("SELECT * FROM hr.departments dep", sql.Substring(b[1].StartOffset, b[1].Length));
        Assert.Equal([(1, 1), (2, 2)], b.Select(x => (x.StartLine, x.EndLine)));
    }

    [Fact]
    public void Destructive_statement_after_a_select_without_semicolon_is_flagged()
    {
        var f = Assert.Single(Pg.Classify("SELECT * FROM orders\nDELETE FROM orders").Destructive);
        Assert.Equal(("deleteWithoutWhere", 2), (f.Rule, f.Line));
    }

    [Fact]
    public void Format_is_left_to_the_client_and_diagnostics_are_empty()
    {
        var r = Pg.Format(new FormatParams { Text = "select 1", Dialect = Dialect.Pg });
        Assert.Equal(("select 1", "client"), (r.Text, r.Error));
        Assert.Empty(Pg.Diagnostics(new DiagnosticsParams { DocId = "d", Text = "SELECT FROM WHERE", Dialect = Dialect.Pg }));
    }

    [Fact]
    public void Folding_covers_comments_dollar_bodies_parentheses_and_statements()
    {
        var sql = "/* a\n b */\nCREATE FUNCTION f() RETURNS int AS $$\nBEGIN\n  RETURN 1;\nEND\n$$ LANGUAGE plpgsql;\nSELECT (\n 1\n);";
        var r = Pg.Folding(new FoldingParams { DocId = "d", Text = sql, Dialect = Dialect.Pg });
        Assert.Contains(r, x => (x.StartLine, x.EndLine, x.Kind) == (1, 2, "comment"));
        Assert.Contains(r, x => (x.StartLine, x.EndLine) == (3, 7) && x.Kind == null); // dollar body / statement
        Assert.Contains(r, x => (x.StartLine, x.EndLine) == (8, 10));                  // parentheses
        Assert.Equal(r.Length, r.Select(x => (x.StartLine, x.EndLine)).Distinct().Count());
        Assert.Equal(r.OrderBy(x => x.StartLine).Select(x => x.StartLine), r.Select(x => x.StartLine));
    }

    [Fact]
    public void Single_line_text_has_no_folds()
    {
        Assert.Empty(Pg.Folding(new FoldingParams { DocId = "d", Text = "SELECT (1), /* c */ 2;", Dialect = Dialect.Pg }));
    }
}
