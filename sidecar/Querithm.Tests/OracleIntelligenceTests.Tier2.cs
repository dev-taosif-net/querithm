using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Tests;

/// <summary>
/// D4 S09: tier-2 completion and signature help under Oracle's rules — upper-case folding, quoted names, schema-qualified
/// names, private and PUBLIC synonyms, the CURRENT_SCHEMA of the script, nothing inside strings / q'…' / comments / hints /
/// SQL*Plus lines, the SQL inside PL/SQL blocks, named arguments, standalone routines and package members.
/// </summary>
public partial class OracleIntelligenceTests
{
    static readonly OracleLanguageService L = new();

    static SnapColumn Col(string name, string type, int ordinal) => new() { Name = name, Type = type, Ordinal = ordinal };

    static readonly SchemaSnapshot Catalog = new()
    {
        ProfileId = "ora", Database = "FREEPDB1", Fks = [],
        Objects =
        [
            new SnapObject { Id = 1, Schema = "APP", Name = "EMP", Kind = ObjectKind.Table, Columns = [Col("EMP_ID", "NUMBER(10)", 1), Col("ENAME", "VARCHAR2(40)", 2), Col("DEPT_ID", "NUMBER(10)", 3)] },
            new SnapObject { Id = 2, Schema = "APP", Name = "DEPT", Kind = ObjectKind.Table, Columns = [Col("DEPT_ID", "NUMBER(10)", 1), Col("DNAME", "VARCHAR2(30)", 2)] },
            new SnapObject { Id = 3, Schema = "APP", Name = "Mixed", Kind = ObjectKind.Table, Columns = [Col("ID", "NUMBER", 1), Col("lower col", "DATE", 2)] },
            new SnapObject { Id = 4, Schema = "APP", Name = "SYN_T", Kind = ObjectKind.Synonym, TargetObjectId = 20 },
            new SnapObject { Id = 5, Schema = "PUBLIC", Name = "PUB_T", Kind = ObjectKind.Synonym, TargetObjectId = 20 },
            new SnapObject { Id = 6, Schema = "APP", Name = "CHAIN", Kind = ObjectKind.Synonym, TargetObjectId = 4 },
            new SnapObject { Id = 7, Schema = "APP", Name = "REMOTE_T", Kind = ObjectKind.Synonym }, // across a database link: no target
            new SnapObject
            {
                Id = 8, Schema = "APP", Name = "RAISE_SAL", Kind = ObjectKind.Proc,
                Params = [new SnapParam { Name = "P_EMP", Type = "NUMBER", Ordinal = 1 }, new SnapParam { Name = "P_PCT", Type = "NUMBER", Ordinal = 2, HasDefault = true }],
            },
            new SnapObject { Id = 9, Schema = "APP", Name = "F_TAX", Kind = ObjectKind.Func, ReturnType = "NUMBER", Params = [new SnapParam { Name = "P_AMOUNT", Type = "NUMBER", Ordinal = 1 }] },
            new SnapObject
            {
                Id = 10, Schema = "APP", Name = "PKG", Kind = ObjectKind.Package,
                Members = ["P(A IN NUMBER, B OUT VARCHAR2)", "P(A IN VARCHAR2)", "F(X IN NUMBER) RETURN NUMBER", "Q", "R(T IN OUT APP.T_REC)"],
            },
            new SnapObject { Id = 11, Schema = "PUBLIC", Name = "UTIL", Kind = ObjectKind.Synonym, TargetObjectId = 10 },
            new SnapObject { Id = 12, Schema = "APP", Name = "PKG", Kind = ObjectKind.PackageBody, SpecObjectId = 10 },
            new SnapObject { Id = 20, Schema = "OWN", Name = "SHARED_T", Kind = ObjectKind.Table, Columns = [Col("A", "NUMBER", 1), Col("B", "VARCHAR2(10)", 2)] },
            new SnapObject { Id = 21, Schema = "OWN", Name = "EMP", Kind = ObjectKind.Table, Columns = [Col("X", "NUMBER", 1)] },
        ],
    };

    sealed class OneCatalog(SchemaSnapshot? snap) : ISchemaSource
    {
        public SchemaSnapshot? Get(string? profileId, string? database) => snap;
        public IReadOnlyList<string> Databases(string? profileId) => ["FREEPDB1"];
    }

    static CompletionItem[] Complete(string marked, string? schema = "APP")
    {
        var at = marked.IndexOf('|');
        return L.Complete(new CompleteParams
        {
            DocId = Guid.NewGuid().ToString(), Text = marked.Remove(at, 1), Offset = at, Dialect = Dialect.Oracle, ProfileId = "ora", Database = "FREEPDB1", Schema = schema,
        }, new OneCatalog(Catalog));
    }

    static string[] Labels(string marked, string? schema = "APP") => Complete(marked, schema).Select(c => c.Label).Order(StringComparer.Ordinal).ToArray();

    static SignatureHelp Sig(string marked)
    {
        var at = marked.IndexOf('|');
        return L.Signature(new SignatureParams
        {
            DocId = Guid.NewGuid().ToString(), Text = marked.Remove(at, 1), Offset = at, Dialect = Dialect.Oracle, ProfileId = "ora", Database = "FREEPDB1", Schema = "APP",
        }, new OneCatalog(Catalog));
    }

    [Theory]
    [InlineData("SELECT e.| FROM emp e")]
    [InlineData("SELECT e.| FROM EMP e")]
    [InlineData("select e.| from Emp E")]
    [InlineData("SELECT e.| FROM app.emp e")]
    [InlineData("SELECT e.| FROM \"APP\".\"EMP\" e")]
    public void Unquoted_names_fold_to_upper_case_and_reach_the_dictionary_spelling(string marked) =>
        Assert.Equal(["DEPT_ID", "EMP_ID", "ENAME"], Labels(marked));

    [Fact]
    public void A_quoted_name_is_exact_and_names_that_need_quotes_get_them()
    {
        var items = Complete("SELECT m.| FROM \"Mixed\" m");
        Assert.Equal(["ID", "lower col"], items.Select(i => i.Label).Order(StringComparer.Ordinal));
        Assert.Equal("\"lower col\"", items.Single(i => i.Label == "lower col").InsertText);
        Assert.Null(items.Single(i => i.Label == "ID").InsertText);
        // Unquoted, Mixed folds to MIXED, which does not exist: nothing (tier 1 alone answers).
        Assert.Empty(Labels("SELECT m.| FROM mixed m"));
    }

    [Fact]
    public void From_offers_the_current_schema_bare_public_synonyms_bare_and_other_schemas_qualified()
    {
        var items = Complete("SELECT * FROM |");
        var labels = items.Select(i => i.Label).ToHashSet();
        Assert.Superset(new HashSet<string> { "EMP", "DEPT", "Mixed", "SYN_T", "PUB_T", "CHAIN", "REMOTE_T", "OWN.SHARED_T", "OWN.EMP", "OWN" }, labels);
        Assert.Equal("\"Mixed\"", items.Single(i => i.Label == "Mixed").InsertText);
        // PUBLIC is no schema anyone writes; routines and packages are not rows.
        Assert.DoesNotContain("PUBLIC", labels);
        Assert.DoesNotContain("PUBLIC.PUB_T", labels);
        Assert.DoesNotContain("RAISE_SAL", labels);
        Assert.DoesNotContain("PKG", labels);
        Assert.DoesNotContain("UTIL", labels); // a synonym of a package
        Assert.Equal(CompletionKind.Table, items.Single(i => i.Label == "SYN_T").Kind);
        var own = Labels("SELECT * FROM own.|");
        Assert.Contains("SHARED_T", own);
        Assert.Contains("EMP", own);
        Assert.DoesNotContain("DEPT", own);
    }

    [Theory]
    [InlineData("SELECT s.| FROM syn_t s")]
    [InlineData("SELECT p.| FROM pub_t p")]
    [InlineData("SELECT c.| FROM chain c")] // a synonym of a synonym
    [InlineData("SELECT x.| FROM own.shared_t x")]
    [InlineData("SELECT pub_t.| FROM pub_t")]
    public void A_synonym_reads_its_targets_columns(string marked) => Assert.Equal(["A", "B"], Labels(marked));

    [Fact]
    public void A_synonym_whose_target_is_not_listed_offers_no_columns() => Assert.Empty(Labels("SELECT r.| FROM remote_t r"));

    [Fact]
    public void Alter_session_current_schema_moves_unqualified_names_for_the_text_after_it()
    {
        Assert.Equal(["DEPT_ID", "EMP_ID", "ENAME"], Labels("SELECT e.| FROM emp e;\nALTER SESSION SET CURRENT_SCHEMA = own;\nSELECT f. FROM emp f;"));
        Assert.Equal(["X"], Labels("SELECT e. FROM emp e;\nALTER SESSION SET CURRENT_SCHEMA = own;\nSELECT f.| FROM emp f;"));
        // "OWN" with quotes is the same schema; a quoted lower-case name would be another.
        Assert.Equal(["X"], Labels("ALTER SESSION SET CURRENT_SCHEMA = \"OWN\";\nSELECT e.| FROM emp e"));
        // Without a login schema every name is qualified, and nothing resolves bare.
        Assert.Contains("APP.EMP", Labels("SELECT * FROM |", schema: null));
    }

    [Fact]
    public void A_login_schema_that_cannot_be_an_oracle_name_is_ignored() =>
        Assert.Contains("APP.EMP", Labels("SELECT * FROM |", schema: "BAD\"NAME"));

    [Theory]
    [InlineData("SELECT 'e.|' FROM emp e")]
    [InlineData("SELECT N'e.|' FROM emp e")]
    [InlineData("SELECT q'[e.|]' FROM emp e")]
    [InlineData("SELECT q'{it's e.|}' FROM emp e")]
    [InlineData("SELECT e.ename FROM emp e WHERE e.ename = q'[it's e.|")] // unterminated q'[…: still a string to the end
    [InlineData("SELECT e.ename FROM emp e WHERE e.ename = 'it''s e.|")]
    [InlineData("SELECT e.ename FROM emp e -- e.|")]
    [InlineData("SELECT e.ename /* e.| */ FROM emp e")]
    [InlineData("SELECT /*+ INDEX(e.| */ e.ename FROM emp e")]
    [InlineData("PROMPT e.|")]
    [InlineData("SELECT * FROM emp@remote.|")]
    public void Nothing_is_offered_inside_strings_comments_hints_links_or_sqlplus_lines(string marked) => Assert.Empty(Complete(marked));

    [Fact]
    public void A_sqlplus_line_before_a_statement_leaves_it_alone() =>
        Assert.Equal(["DEPT_ID", "EMP_ID", "ENAME"], Labels("SET SERVEROUTPUT ON\nPROMPT go\nSELECT e.| FROM emp e;"));

    [Theory]
    [InlineData("BEGIN\n  SELECT e.| INTO v FROM emp e;\nEND;\n/")]
    [InlineData("DECLARE\n  v NUMBER;\nBEGIN\n  IF v > 0 THEN\n    SELECT e.| INTO v FROM emp e WHERE ROWNUM = 1;\n  END IF;\nEND;")]
    [InlineData("BEGIN\n  FOR r IN (SELECT e.| FROM emp e) LOOP\n    NULL;\n  END LOOP;\nEND;")]
    [InlineData("CREATE OR REPLACE PROCEDURE p(a NUMBER) IS\n  v NUMBER;\nBEGIN\n  DELETE FROM emp e WHERE e.| = a;\nEND;\n/")]
    [InlineData("CREATE OR REPLACE PACKAGE BODY pkg AS\n  PROCEDURE p IS\n  BEGIN\n    UPDATE emp e SET e.ename = 'x' WHERE e.| = 1;\n  END;\nEND;\n/")]
    [InlineData("BEGIN\n  <<outer>>\n  BEGIN\n    SELECT CASE WHEN e.ename IS NULL THEN 1 ELSE 2 END INTO v FROM emp e WHERE e.| = 1;\n  END;\nEND;")]
    public void The_sql_inside_plsql_blocks_completes_like_sql(string marked) => Assert.Equal(["DEPT_ID", "EMP_ID", "ENAME"], Labels(marked));

    [Fact]
    public void A_cursor_query_in_a_declare_section_completes_like_sql() =>
        Assert.Equal(["DEPT_ID", "DNAME"], Labels("DECLARE\n  CURSOR c IS SELECT d.| FROM dept d;\nBEGIN\n  NULL;\nEND;"));

    [Fact]
    public void Right_after_a_block_word_a_new_statement_starts()
    {
        Assert.Empty(Complete("BEGIN\n  |"));
        Assert.Empty(Complete("BEGIN\n  SELECT 1 INTO v FROM dual;\n  |"));
    }

    [Fact]
    public void Calls_offer_named_arguments_without_the_ones_already_given()
    {
        Assert.Equal(["P_EMP =>", "P_PCT =>"], Labels("BEGIN raise_sal(|").Where(l => l.EndsWith("=>")).ToArray());
        Assert.Equal(["P_EMP =>"], Labels("BEGIN raise_sal(p_pct => 5, |").Where(l => l.EndsWith("=>")).ToArray());
        Assert.Equal(["A =>", "B =>"], Labels("BEGIN pkg.p(|").Where(l => l.EndsWith("=>")).ToArray());
    }

    [Fact]
    public void Signature_help_shows_standalone_routines_with_the_active_parameter_by_position_or_name()
    {
        var s = Sig("BEGIN raise_sal(1, |");
        var only = Assert.Single(s.Signatures);
        Assert.Equal("APP.RAISE_SAL(P_EMP NUMBER, P_PCT NUMBER DEFAULT …)", only.Label);
        Assert.Equal("procedure", only.Documentation);
        Assert.Equal(1, s.ActiveParameter);
        Assert.Equal(1, Sig("BEGIN raise_sal(p_pct => |").ActiveParameter);
        Assert.Equal(0, Sig("BEGIN raise_sal(p_pct => 1, p_emp => |").ActiveParameter);
        var f = Assert.Single(Sig("SELECT f_tax(|) FROM dual").Signatures);
        Assert.Equal("APP.F_TAX(P_AMOUNT NUMBER)", f.Label);
        Assert.Equal("returns NUMBER", f.Documentation);
    }

    [Fact]
    public void Signature_help_shows_package_members_one_signature_per_overload()
    {
        var p = Sig("BEGIN pkg.p(|");
        Assert.Equal(["APP.PKG.P(A NUMBER, B OUT VARCHAR2)", "APP.PKG.P(A VARCHAR2)"], p.Signatures.Select(x => x.Label));
        Assert.Equal(0, p.ActiveParameter);
        Assert.Equal(0, Sig("BEGIN pkg.p(1, |").ActiveSignature); // only the first overload has a second parameter
        Assert.Equal(1, Sig("BEGIN pkg.p(1, |").ActiveParameter);
        Assert.Equal(1, Sig("BEGIN pkg.p(b => |").ActiveParameter);
        var f = Assert.Single(Sig("SELECT app.pkg.f(|) FROM dual").Signatures);
        Assert.Equal("APP.PKG.F(X NUMBER)", f.Label);
        Assert.Equal("returns NUMBER", f.Documentation);
        // Through a PUBLIC synonym of the package.
        Assert.Equal("APP.PKG.F(X NUMBER)", Assert.Single(Sig("SELECT util.f(|) FROM dual").Signatures).Label);
        Assert.Equal("APP.PKG.R(T IN OUT APP.T_REC)", Assert.Single(Sig("BEGIN pkg.r(|").Signatures).Label);
        Assert.Equal("APP.PKG.Q()", Assert.Single(Sig("BEGIN pkg.q(|").Signatures).Label);
        Assert.Empty(Sig("BEGIN pkg.nothing(|").Signatures);
        Assert.Empty(Sig("BEGIN own.pkg.p(|").Signatures); // no package PKG in OWN
    }

    [Theory]
    [InlineData("P(A IN NUMBER, B OUT VARCHAR2)", "P", ObjectKind.Proc, null, "A:NUMBER:False|B:OUT VARCHAR2:True")]
    [InlineData("F(X IN NUMBER) RETURN NUMBER", "F", ObjectKind.Func, "NUMBER", "X:NUMBER:False")]
    [InlineData("Q", "Q", ObjectKind.Proc, null, "")]
    [InlineData("G RETURN VARCHAR2", "G", ObjectKind.Func, "VARCHAR2", "")]
    [InlineData("R(T IN OUT APP.T_REC)", "R", ObjectKind.Proc, null, "T:IN OUT APP.T_REC:True")]
    public void Member_lines_read_back_as_routines(string line, string name, ObjectKind kind, string? returns, string parameters)
    {
        var pkg = new SnapObject { Id = 1, Schema = "APP", Name = "PKG", Kind = ObjectKind.Package };
        var r = OracleCompletion.ParseMember(pkg, line)!;
        Assert.Equal(name, r.Name);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(returns, r.ReturnType);
        Assert.Equal(parameters, string.Join("|", r.Params!.Select(p => $"{p.Name}:{p.Type}:{p.Output}")));
        Assert.Null(OracleCompletion.ParseMember(pkg, "P(A IN NUMBER"));
    }

    [Fact]
    public void Without_a_snapshot_tier_2_offers_nothing_and_never_throws()
    {
        Assert.Empty(L.Complete(new CompleteParams { DocId = "d", Text = "SELECT e. FROM emp e", Offset = 9, Dialect = Dialect.Oracle }, new OneCatalog(null)));
        Assert.Empty(L.Signature(new SignatureParams { DocId = "d", Text = "BEGIN p(", Offset = 8, Dialect = Dialect.Oracle }, new OneCatalog(null)).Signatures);
        Assert.Empty(L.Complete(new CompleteParams { DocId = "d", Text = "", Offset = 5, Dialect = Dialect.Oracle }, new OneCatalog(Catalog)));
    }

    [Fact]
    public void Forget_drops_the_documents_tier_2_cached()
    {
        var at = "SELECT e. FROM emp e".IndexOf('.') + 1;
        L.Complete(new CompleteParams { DocId = "ora-forget", Text = "SELECT e. FROM emp e", Offset = at, Dialect = Dialect.Oracle, Schema = "APP" }, new OneCatalog(Catalog));
        Assert.True(DocumentCaches.Holds("ora-forget"));
        L.Forget("ora-forget");
        Assert.False(DocumentCaches.Holds("ora-forget"));
    }
}
