using Querithm.Contract;
using Querithm.Intelligence.Oracle;

namespace Querithm.Tests;

/// <summary>
/// D4 S02: Oracle tokenizer, SQL*Plus-style splitter, classifier, keywords and identifier quoting, with no server.
/// Everything works on tokens (CLAUDE.md §2).
/// </summary>
public class OracleIntelligenceTests
{
    readonly OracleLanguageService _lang = new();

    static List<OracleToken> Code(string sql) => OracleTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();

    /// <summary>The text of each batch, as the driver receives it.</summary>
    string[] Batches(string sql) => _lang.Classify(sql).Batches.Select(b => sql.Substring(b.StartOffset, b.Length)).ToArray();

    string[] Rules(string sql) => _lang.Classify(sql).Destructive.Select(f => f.Rule).ToArray();

    // ---------------------------------------------------------------- tokenizer

    [Theory]
    [InlineData("q'[it's]'", "it's")]
    [InlineData("q'{a ' b}'", "a ' b")]
    [InlineData("q'(x)y)'", "x)y")]
    [InlineData("q'<a>b>'", "a>b")]
    [InlineData("q'!don't!'", "don't")]
    [InlineData("Q'#a'b#'", "a'b")]
    [InlineData("q'xabcx'", "abc")]
    [InlineData("nq'[national ']'", "national '")]
    [InlineData("NQ'|z|'", "z")]
    [InlineData("'it''s'", "it's")]
    [InlineData("N'abc'", "abc")]
    public void Every_alternative_quote_delimiter_is_one_string(string literal, string value)
    {
        var toks = Code(literal + " x");
        Assert.Equal(2, toks.Count);
        Assert.Equal(OracleTokenKind.String, toks[0].Kind);
        Assert.Equal(literal, toks[0].Text);
        Assert.Equal(value, OracleTokenizer.StringValue(toks[0]));
        Assert.True(toks[1].Is("x"));
    }

    [Fact]
    public void A_semicolon_or_quote_inside_alternative_quoting_ends_nothing()
    {
        var sql = "SELECT q'[a';b]' FROM dual; SELECT 2 FROM dual";
        Assert.Equal(["SELECT q'[a';b]' FROM dual", "SELECT 2 FROM dual"], Batches(sql));
    }

    [Fact]
    public void Quoted_names_are_exact_and_unquoted_fold_to_upper()
    {
        var toks = Code("select \"Mixed \"\"x\" , emp, Emp$#1 from dual");
        Assert.Equal(OracleTokenKind.QuotedIdent, toks[1].Kind);
        Assert.Equal("EMP", OracleIdentifiers.Identity(toks[3]));
        Assert.Equal("EMP$#1", OracleIdentifiers.Identity(toks[5]));
        Assert.Equal("emp", OracleIdentifiers.Identity("\"emp\""));
        Assert.Equal("EMP", OracleIdentifiers.Identity("emp"));
    }

    [Fact]
    public void Hints_and_comments_are_trivia()
    {
        var all = OracleTokenizer.Tokenize("SELECT /*+ FULL(t) */ a --+ INDEX(t)\n/* c */ -- d\nFROM t");
        Assert.Equal(2, all.Count(t => t.Kind == OracleTokenKind.Hint));
        Assert.Equal(2, all.Count(t => t.Kind == OracleTokenKind.Comment));
        Assert.Equal(["SELECT", "a", "FROM", "t"], all.Where(t => !t.IsTrivia).Select(t => t.Text));
    }

    [Fact]
    public void Binds_assignment_and_new_are_told_apart()
    {
        var toks = Code("v := :name + :1 + :\"Odd\" + :new.id");
        Assert.True(toks[1].IsP(":="));
        Assert.Equal([":name", ":1", ":\"Odd\"", ":new"], toks.Where(t => t.Kind == OracleTokenKind.Bind).Select(t => t.Text));
        Assert.Equal(["NAME", "1", "Odd"], OracleLanguageService.Binds("SELECT :name, :1, :\"Odd\", :NAME FROM dual"));
    }

    [Fact]
    public void Trigger_row_records_are_not_ad_hoc_parameters()
    {
        var trigger = "CREATE OR REPLACE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW\nBEGIN\n  :NEW.id := :old.id;\nEND;\n/";
        Assert.Empty(OracleLanguageService.Binds(trigger));
        Assert.Equal(["X"], OracleLanguageService.Binds("BEGIN :x := 1; END;"));
    }

    [Fact]
    public void A_db_link_follows_a_name_and_a_range_is_not_a_number()
    {
        var toks = Code("SELECT * FROM emp@remote.example, \"T\"@\"L\" WHERE 1 = 1");
        Assert.Equal(["@remote.example", "@\"L\""], toks.Where(t => t.Kind == OracleTokenKind.DbLink).Select(t => t.Text));
        var range = Code("FOR i IN 1..10 LOOP");
        Assert.Equal(["1", "..", "10"], range.Skip(3).Take(3).Select(t => t.Text));
        Assert.Equal(OracleTokenKind.Number, Code("1.5e-3f")[0].Kind);
    }

    [Fact]
    public void Unterminated_constructs_run_to_the_end_and_never_throw()
    {
        foreach (var s in new[] { "'abc", "\"abc", "/* abc", "q'[abc", "nq'", ":", "@", "q'" })
            Assert.Equal(s, string.Concat(OracleTokenizer.Tokenize(s).Select(t => t.Text)));
    }

    // ---------------------------------------------------------------- splitter

    [Fact]
    public void Sql_ends_at_a_semicolon_which_is_not_sent()
    {
        Assert.Equal(["SELECT 1 FROM dual", "SELECT 'a;b' FROM dual"], Batches("SELECT 1 FROM dual;\nSELECT 'a;b' FROM dual;\n"));
    }

    [Fact]
    public void A_slash_line_ends_sql_too_and_slash_in_strings_or_comments_does_not()
    {
        var sql = "SELECT 10\n  / 2 FROM dual\n/\nSELECT '\n/\n' FROM dual /*\n/\n*/\n/\n";
        Assert.Equal(["SELECT 10\n  / 2 FROM dual", "SELECT '\n/\n' FROM dual"], Batches(sql)); // the trailing comment is trivia after the statement
    }

    [Fact]
    public void Mixed_sql_and_plsql_script()
    {
        var sql = """
            CREATE TABLE t (id NUMBER);
            BEGIN
              INSERT INTO t VALUES (1);
              COMMIT;
            END;
            /
            DECLARE
              n NUMBER;
            BEGIN
              SELECT COUNT(*) INTO n FROM t;
            END;
            /
            SELECT * FROM t;
            """;
        var b = Batches(sql);
        Assert.Equal(4, b.Length);
        Assert.Equal("CREATE TABLE t (id NUMBER)", b[0]);
        Assert.StartsWith("BEGIN", b[1]);
        Assert.EndsWith("END;", b[1]);
        Assert.StartsWith("DECLARE", b[2]);
        Assert.EndsWith("END;", b[2]);
        Assert.Equal("SELECT * FROM t", b[3]);
        var c = _lang.Classify(sql);
        Assert.Equal([1, 2, 7, 13], c.Batches.Select(x => x.StartLine));
        Assert.Equal([1, 5, 11, 13], c.Batches.Select(x => x.EndLine));
    }

    [Fact]
    public void Package_spec_and_body_are_two_units()
    {
        var sql = """
            CREATE OR REPLACE PACKAGE pkg AS
              PROCEDURE p(x NUMBER);
            END pkg;
            /
            CREATE OR REPLACE EDITIONABLE PACKAGE BODY pkg AS
              PROCEDURE p(x NUMBER) IS
              BEGIN
                NULL;
              END p;
            END pkg;
            /
            """;
        var b = Batches(sql);
        Assert.Equal(2, b.Length);
        Assert.EndsWith("END pkg;", b[0]);
        Assert.StartsWith("CREATE OR REPLACE EDITIONABLE PACKAGE BODY", b[1]);
        Assert.EndsWith("END pkg;", b[1]);
        Assert.True(_lang.Classify(sql).HasDdl);
        Assert.Empty(Rules(sql));
    }

    [Theory]
    [InlineData("CREATE PROCEDURE p IS BEGIN NULL; END;")]
    [InlineData("CREATE OR REPLACE NONEDITIONABLE FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END;")]
    [InlineData("CREATE TYPE BODY tp AS MEMBER FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; END;")]
    [InlineData("CREATE OR REPLACE LIBRARY lib AS '/x.so';")]
    [InlineData("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED j AS public class J { int f() { return 1; } };")]
    [InlineData("CREATE TRIGGER trg FOR INSERT ON t COMPOUND TRIGGER\n  BEFORE STATEMENT IS BEGIN NULL; END BEFORE STATEMENT;\n  AFTER EACH ROW IS BEGIN :NEW.x := 1; END AFTER EACH ROW;\nEND trg;")]
    [InlineData("WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; SELECT f FROM dual;")]
    public void Plsql_units_run_to_the_slash_and_keep_their_semicolons(string unit)
    {
        var b = Batches(unit + "\n/\nSELECT 1 FROM dual");
        Assert.Equal([unit, "SELECT 1 FROM dual"], b);
    }

    [Fact]
    public void A_missing_final_slash_ends_the_unit_at_the_end_of_the_script()
    {
        Assert.Equal(["BEGIN\n  NULL;\nEND;"], Batches("BEGIN\n  NULL;\nEND;\n"));
        Assert.Equal(["<<outer>>\nBEGIN NULL; END;"], Batches("<<outer>>\nBEGIN NULL; END;"));
    }

    [Theory]
    [InlineData("CREATE TYPE pt AS OBJECT (x NUMBER, y NUMBER);", "CREATE TYPE pt AS OBJECT (x NUMBER, y NUMBER)")]
    [InlineData("CREATE OR REPLACE TYPE pt AS OBJECT (x NUMBER, MEMBER FUNCTION len RETURN NUMBER);\n/", "CREATE OR REPLACE TYPE pt AS OBJECT (x NUMBER, MEMBER FUNCTION len RETURN NUMBER)")]
    [InlineData("CREATE TYPE nums AS TABLE OF NUMBER\n/", "CREATE TYPE nums AS TABLE OF NUMBER")]
    public void A_type_spec_without_body_ends_like_sql(string sql, string batch)
    {
        Assert.Equal([batch, "SELECT 1 FROM dual"], Batches(sql + "\nSELECT 1 FROM dual;"));
    }

    [Fact]
    public void Trigger_with_new_ends_at_the_slash()
    {
        var sql = "CREATE OR REPLACE TRIGGER trg\nBEFORE UPDATE ON t FOR EACH ROW\nBEGIN\n  IF :NEW.v < 0 THEN\n    :NEW.v := 0;\n  END IF;\nEND;\n/\nUPDATE t SET v = 1 WHERE id = 1;";
        var b = Batches(sql);
        Assert.Equal(2, b.Length);
        Assert.EndsWith("END;", b[0]);
        Assert.Equal("UPDATE t SET v = 1 WHERE id = 1", b[1]);
        Assert.Empty(Rules(sql));
    }

    [Fact]
    public void Set_serveroutput_toggles_and_is_never_sent()
    {
        var sql = "SET SERVEROUTPUT ON SIZE UNLIMITED\nBEGIN DBMS_OUTPUT.PUT_LINE('x'); END;\n/\nset serverout off;\nSELECT 1 FROM dual;";
        var c = _lang.Classify(sql);
        Assert.Null(c.Refusal);
        Assert.Equal(2, c.Batches.Length);
        Assert.Equal([(0, true), (1, false)], OracleLanguageService.ServerOutputToggles(sql));
    }

    [Theory]
    [InlineData("PROMPT hello", "PROMPT")]
    [InlineData("SET ECHO ON", "SET")]
    [InlineData("SPOOL out.txt", "SPOOL")]
    [InlineData("@install.sql", "@file")]
    [InlineData("@@child.sql", "@file")]
    [InlineData("EXEC dbms_stats.gather_table_stats(user, 'T')", "BEGIN")]
    [InlineData("execute p", "BEGIN")]
    [InlineData("DEFINE x = 1", "DEFINE")]
    [InlineData("WHENEVER SQLERROR EXIT", "WHENEVER")]
    [InlineData("SELECT * FROM t WHERE id = &id", ":id")]
    [InlineData("SELECT &&col FROM t", ":col")]
    [InlineData("SELECT 1 FROM dual;\nSHOW ERRORS", "SHOW")]
    public void Other_client_commands_are_refused_with_a_reason(string sql, string reasonPart)
    {
        var refusal = _lang.Classify(sql).Refusal;
        Assert.NotNull(refusal);
        Assert.Contains(reasonPart, refusal);
    }

    [Theory]
    [InlineData("SET TRANSACTION READ ONLY")]
    [InlineData("SET ROLE ALL")]
    [InlineData("SET CONSTRAINTS ALL DEFERRED")]
    [InlineData("SELECT 'a&b', 'PROMPT' FROM dual -- &x")]
    [InlineData("SELECT a FROM t\nWHERE x = 1")]
    public void Sql_that_looks_like_a_client_command_is_sql(string sql)
    {
        var c = _lang.Classify(sql);
        Assert.Null(c.Refusal);
        Assert.Single(c.Batches);
    }

    [Fact]
    public void A_client_command_continues_on_a_trailing_dash()
    {
        var st = OracleSplitter.Split("PROMPT one -\ntwo\nSELECT 1 FROM dual");
        Assert.Equal(OracleStatementKind.ClientCommand, st[0].Kind);
        Assert.Equal("PROMPT one -\ntwo", st[0].Command!.Text);
        Assert.Equal(OracleStatementKind.Sql, st[1].Kind);
    }

    [Fact]
    public void Folding_ranges_follow_statements()
    {
        var sql = "SELECT 1\nFROM dual;\nBEGIN\n  NULL;\nEND;\n/\nSELECT 2 FROM dual;";
        var f = _lang.Folding(new FoldingParams { DocId = "d", Text = sql, Dialect = Dialect.Oracle });
        Assert.Equal([(1, 2), (3, 5)], f.Select(r => (r.StartLine, r.EndLine)));
    }

    // ---------------------------------------------------------------- classifier

    [Theory]
    [InlineData("DROP TABLE t", "drop")]
    [InlineData("DROP TABLE t PURGE", "drop")]
    [InlineData("DROP TABLE t CASCADE CONSTRAINTS PURGE", "drop")]
    [InlineData("DROP USER u CASCADE", "drop")]
    [InlineData("TRUNCATE TABLE t REUSE STORAGE", "truncate")]
    [InlineData("DELETE FROM t", "deleteWithoutWhere")]
    [InlineData("DELETE t", "deleteWithoutWhere")]
    [InlineData("DELETE FROM t WHERE", "deleteWithoutWhere")]
    [InlineData("DELETE FROM t /* WHERE id = 1 */", "deleteWithoutWhere")]
    [InlineData("DELETE FROM (SELECT * FROM t WHERE id = 1)", "deleteWithoutWhere")]
    [InlineData("UPDATE t SET v = (SELECT 1 FROM dual WHERE 1 = 1)", "updateWithoutWhere")]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.v = s.v WHERE s.v > 0", "merge")]
    [InlineData("ALTER TABLE t DROP COLUMN v", "alterDrop")]
    [InlineData("ALTER TABLE t DROP CONSTRAINT pk", "alterDrop")]
    [InlineData("ALTER TABLE t TRUNCATE PARTITION p1", "alterDrop")]
    [InlineData("ALTER TABLE t SET UNUSED (v)", "alterDrop")]
    [InlineData("FLASHBACK TABLE t TO BEFORE DROP", "flashback")]
    [InlineData("FLASHBACK TABLE t TO TIMESTAMP SYSTIMESTAMP - INTERVAL '1' HOUR", "flashback")]
    [InlineData("PURGE RECYCLEBIN", "purge")]
    [InlineData("PURGE TABLE t", "purge")]
    [InlineData("WITH x AS (SELECT 1 FROM dual) DELETE FROM t", "deleteWithoutWhere")]
    public void Each_destructive_form_is_found(string sql, string rule)
    {
        Assert.Contains(rule, Rules(sql));
        Assert.Contains(rule, Rules(sql + ";"));
    }

    [Theory]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("UPDATE t SET v = 1 WHERE id = :id")]
    [InlineData("ALTER TABLE t ADD (w NUMBER)")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("SELECT 'DROP TABLE t' FROM dual")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = hr")]
    [InlineData("BEGIN v.DELETE; v.DELETE(1); END;")]
    [InlineData("BEGIN FOR r IN (SELECT * FROM t FOR UPDATE) LOOP NULL; END LOOP; END;")]
    public void Safe_statements_are_not_destructive(string sql) => Assert.Empty(Rules(sql));

    [Theory]
    [InlineData("BEGIN\n  EXECUTE IMMEDIATE 'DROP TABLE t PURGE';\nEND;", "drop")]
    [InlineData("BEGIN\n  EXECUTE IMMEDIATE 'DELETE ' || 'FROM t';\nEND;", "deleteWithoutWhere")]
    [InlineData("BEGIN\n  EXECUTE IMMEDIATE q'[TRUNCATE TABLE t]';\nEND;", "truncate")]
    [InlineData("BEGIN\n  EXECUTE IMMEDIATE v_sql;\nEND;", "dynamicSql")]
    [InlineData("BEGIN\n  EXECUTE IMMEDIATE 'DROP TABLE ' || v_name;\nEND;", "dynamicSql")]
    [InlineData("DECLARE c INTEGER := DBMS_SQL.OPEN_CURSOR; BEGIN DBMS_SQL.PARSE(c, v, DBMS_SQL.NATIVE); END;", "dynamicSql")]
    [InlineData("BEGIN DBMS_UTILITY.EXEC_DDL_STATEMENT('DROP TABLE t'); END;", "dynamicSql")]
    [InlineData("BEGIN\n  DELETE FROM t;\nEND;", "deleteWithoutWhere")]
    [InlineData("BEGIN\n  FORALL i IN 1..n UPDATE t SET v = 0;\nEND;", "updateWithoutWhere")]
    [InlineData("BEGIN\n  MERGE INTO t USING s ON (1 = 1) WHEN MATCHED THEN UPDATE SET v = 1;\nEND;", "merge")]
    public void Dml_and_dynamic_sql_in_a_block_count(string sql, string rule) => Assert.Contains(rule, Rules(sql + "\n/"));

    [Fact]
    public void Dml_with_where_in_a_block_is_not_destructive_and_creating_code_runs_none()
    {
        Assert.Empty(Rules("BEGIN\n  DELETE FROM t WHERE id = 1;\n  EXECUTE IMMEDIATE 'UPDATE t SET v = 1 WHERE id = 2';\nEND;\n/"));
        Assert.Empty(Rules("CREATE OR REPLACE PROCEDURE p IS BEGIN DELETE FROM t; EXECUTE IMMEDIATE v; END;\n/"));
    }

    [Theory]
    [InlineData("CREATE TABLE t (id NUMBER)", true)]
    [InlineData("ALTER TABLE t ADD (w NUMBER)", true)]
    [InlineData("GRANT SELECT ON t TO u", true)]
    [InlineData("COMMENT ON TABLE t IS 'x'", true)]
    [InlineData("RENAME t TO t2", true)]
    [InlineData("TRUNCATE TABLE t", true)]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = hr", false)]
    [InlineData("ALTER SYSTEM FLUSH SHARED_POOL", false)]
    [InlineData("SELECT 1 FROM dual", false)]
    [InlineData("INSERT INTO t VALUES (1)", false)]
    [InlineData("BEGIN EXECUTE IMMEDIATE 'CREATE TABLE x (a NUMBER)'; END;", true)]
    public void Ddl_is_detected_for_the_refresh_and_its_implicit_commit(string sql, bool ddl) =>
        Assert.Equal(ddl, _lang.Classify(sql).HasDdl);

    [Theory]
    [InlineData("COMMIT", false, true)]
    [InlineData("COMMIT WORK", false, true)]
    [InlineData("ROLLBACK", false, true)]
    [InlineData("ROLLBACK TO SAVEPOINT a", false, false)]
    [InlineData("ROLLBACK WORK TO a", false, false)]
    [InlineData("SAVEPOINT a", false, false)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", true, false)]
    [InlineData("BEGIN UPDATE t SET v = 1 WHERE id = 1; COMMIT; END;", false, true)]
    public void Transaction_statements(string sql, bool begins, bool ends)
    {
        var c = _lang.Classify(sql);
        Assert.Equal((begins, ends), (c.BeginsTransaction, c.EndsTransaction));
    }

    [Fact]
    public void Current_schema_is_completion_context_not_a_database_switch()
    {
        var sql = "ALTER SESSION SET CURRENT_SCHEMA = hr;\nSELECT * FROM emp;\nALTER SESSION SET CURRENT_SCHEMA = \"Mixed\";\nSELECT 1 FROM dual;";
        Assert.Null(_lang.Classify(sql).UseDatabase);
        Assert.Null(OracleLanguageService.CurrentSchemaAt(sql, 0));
        Assert.Equal("HR", OracleLanguageService.CurrentSchemaAt(sql, sql.IndexOf("SELECT * FROM emp", StringComparison.Ordinal)));
        Assert.Equal("Mixed", OracleLanguageService.CurrentSchemaAt(sql, sql.Length));
    }

    [Fact]
    public void A_finding_names_its_line()
    {
        var f = Assert.Single(_lang.Classify("SELECT 1 FROM dual;\n\nDROP TABLE t;").Destructive);
        Assert.Equal(3, f.Line);
        Assert.Equal("DROP TABLE t", f.Statement);
    }

    // ---------------------------------------------------------------- keywords and quoting

    [Theory]
    [InlineData("EMP", "EMP")]
    [InlineData("emp", "\"emp\"")]
    [InlineData("SELECT", "\"SELECT\"")]
    [InlineData("LEVEL", "\"LEVEL\"")]
    [InlineData("A B", "\"A B\"")]
    [InlineData("1X", "\"1X\"")]
    [InlineData("X$#_1", "X$#_1")]
    public void Quote_if_needed_writes_a_name_that_reaches_the_same_object(string name, string written)
    {
        Assert.Equal(written, OracleIdentifiers.QuoteIfNeeded(name));
        Assert.Equal(name, OracleIdentifiers.Identity(written));
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a\0b")]
    [InlineData("")]
    public void A_name_oracle_cannot_hold_is_refused(string name)
    {
        Assert.NotNull(OracleIdentifiers.Invalid(name));
        Assert.Throws<ArgumentException>(() => OracleIdentifiers.Quote(name));
    }

    [Fact]
    public void A_name_over_128_bytes_is_refused()
    {
        Assert.Null(OracleIdentifiers.Invalid(new string('A', 128)));
        Assert.NotNull(OracleIdentifiers.Invalid(new string('A', 129)));
        Assert.NotNull(OracleIdentifiers.Invalid(new string('é', 65))); // 130 UTF-8 bytes
    }

    [Theory]
    [InlineData("SELECT", true)]
    [InlineData("rownum", true)]
    [InlineData("VARCHAR2", true)]
    [InlineData("BEGIN", true)]
    [InlineData("EMP", false)]
    [InlineData("NAME", false)]
    public void Reserved_words(string word, bool reserved) => Assert.Equal(reserved, OracleKeywords.IsReserved(word));

    [Fact]
    public void Identity_matches_the_providers_ordinal_name_match()
    {
        var traits = new Querithm.Providers.Oracle.OracleProvider().Traits;
        Assert.True(traits.NameMatch.Equals(OracleIdentifiers.Identity("emp"), OracleIdentifiers.Identity("EMP")));
        Assert.False(traits.NameMatch.Equals(OracleIdentifiers.Identity("\"emp\""), OracleIdentifiers.Identity("emp")));
    }
}
