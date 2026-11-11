using Querithm.Contract;
using Querithm.Providers.Oracle;
using static Querithm.Providers.Oracle.OracleMetadata;

namespace Querithm.Tests;

/// <summary>
/// D4 S11 Script as ▸ for every Oracle kind but packages (OraclePackageTests), from canned definitions and catalog rows:
/// CREATE / ALTER as CREATE OR REPLACE / DROP by real kind / SELECT / ENABLE / DISABLE, the refusals with their reason,
/// the Execute block (named arguments, OUT / IN OUT through locals, result row, ref cursors, booleans, defaults left out),
/// typed values and their refusals, dependencies assembled from ALL_DEPENDENCIES rows, and the batch definition forms.
/// </summary>
public sealed class OracleScriptTests
{
    const string ViewDdl = "CREATE OR REPLACE FORCE EDITIONABLE VIEW \"APP\".\"V\" (\"A\") AS\n  SELECT a FROM t;";
    const string ProcDdl = "CREATE OR REPLACE EDITIONABLE PROCEDURE \"APP\".\"P\" AS BEGIN NULL; END;\n/";
    const string TableDdl = "CREATE TABLE \"APP\".\"T\" (\"A\" NUMBER, PRIMARY KEY (\"A\"));";

    static OracleScripter Scripter(string? objectType = null, IReadOnlyList<OracleArgument>? args = null, string? definition = null) => new(
        (_, o, _) => Task.FromResult<string?>(definition ?? o.Kind switch
        {
            ObjectKind.View => ViewDdl, ObjectKind.Proc => ProcDdl, ObjectKind.Table => TableDdl, ObjectKind.Sequence => "CREATE SEQUENCE \"APP\".\"S\";",
            ObjectKind.Trigger => "CREATE TRIGGER \"APP\".\"TRG\" BEFORE INSERT ON t BEGIN NULL; END;\n/", ObjectKind.Synonym => "CREATE SYNONYM \"APP\".\"SYN\" FOR \"APP\".\"T\";",
            ObjectKind.Type => "CREATE TYPE \"APP\".\"TY\" AS OBJECT (a NUMBER);\n/",
            _ => null,
        }),
        Editable, (_, _, _) => Task.FromResult(objectType), (_, _, _) => Task.FromResult(args ?? []));

    static ObjectRef Ref(ObjectKind kind, string name = "X", string schema = "APP") => new() { Schema = schema, Name = name, Kind = kind };

    static string Script(OracleScripter s, ObjectRef o, ScriptKind kind) => s.ScriptAsync(null!, o, kind, default).GetAwaiter().GetResult();

    [Theory]
    [InlineData(ObjectKind.Table, "X", "APP", ScriptKind.SelectTop, "SELECT *\nFROM APP.X\nFETCH FIRST 1000 ROWS ONLY;")]
    [InlineData(ObjectKind.View, "Mixed", "APP", ScriptKind.SelectTop, "SELECT *\nFROM APP.\"Mixed\"\nFETCH FIRST 1000 ROWS ONLY;")]
    [InlineData(ObjectKind.Trigger, "TRG", "APP", ScriptKind.Enable, "ALTER TRIGGER APP.TRG ENABLE;")]
    [InlineData(ObjectKind.Trigger, "TRG", "APP", ScriptKind.Disable, "ALTER TRIGGER APP.TRG DISABLE;")]
    [InlineData(ObjectKind.Proc, "P", "APP", ScriptKind.Drop, "DROP PROCEDURE APP.P;")]
    [InlineData(ObjectKind.Func, "F", "APP", ScriptKind.Drop, "DROP FUNCTION APP.F;")]
    [InlineData(ObjectKind.Trigger, "TRG", "APP", ScriptKind.Drop, "DROP TRIGGER APP.TRG;")]
    [InlineData(ObjectKind.Type, "TY", "APP", ScriptKind.Drop, "DROP TYPE APP.TY;")]
    [InlineData(ObjectKind.Sequence, "S", "APP", ScriptKind.Drop, "DROP SEQUENCE APP.S;")]
    [InlineData(ObjectKind.Synonym, "SYN", "APP", ScriptKind.Drop, "DROP SYNONYM APP.SYN;")]
    [InlineData(ObjectKind.Synonym, "SYN", "PUBLIC", ScriptKind.Drop, "DROP PUBLIC SYNONYM SYN;")]
    [InlineData(ObjectKind.View, "V", "APP", ScriptKind.Drop, "DROP VIEW APP.V;")]
    public void Statements_name_the_objects_real_kind(ObjectKind kind, string name, string schema, ScriptKind script, string expected) =>
        Assert.Equal(expected, Script(Scripter(), Ref(kind, name, schema), script));

    [Fact]
    public void A_table_drop_says_it_goes_to_the_recycle_bin_and_a_materialized_view_drops_as_one()
    {
        Assert.Equal($"{OracleScripter.RecycleBin}\nDROP TABLE APP.T;", Script(Scripter(), Ref(ObjectKind.Table, "T"), ScriptKind.Drop));
        Assert.Equal("DROP MATERIALIZED VIEW APP.MV;", Script(Scripter("MATERIALIZED VIEW"), Ref(ObjectKind.View, "MV"), ScriptKind.Drop));
        Assert.Equal(OracleScripter.NoReplaceMView, Assert.Throws<NotSupportedException>(() => Script(Scripter("MATERIALIZED VIEW"), Ref(ObjectKind.View, "MV"), ScriptKind.Alter)).Message);
    }

    [Fact]
    public void CREATE_is_the_definition_and_ALTER_its_CREATE_OR_REPLACE()
    {
        var s = Scripter();
        Assert.Equal(ViewDdl, Script(s, Ref(ObjectKind.View, "V"), ScriptKind.Create));
        Assert.Equal(ViewDdl, Script(s, Ref(ObjectKind.View, "V"), ScriptKind.Alter));
        Assert.StartsWith("CREATE OR REPLACE TRIGGER", Script(s, Ref(ObjectKind.Trigger, "TRG"), ScriptKind.Alter));
        Assert.StartsWith("CREATE OR REPLACE SYNONYM", Script(s, Ref(ObjectKind.Synonym, "SYN"), ScriptKind.Alter));
        Assert.StartsWith("CREATE OR REPLACE TYPE", Script(s, Ref(ObjectKind.Type, "TY"), ScriptKind.Alter));
        Assert.Equal($"DROP PROCEDURE APP.P;\n\n{ProcDdl}", Script(s, Ref(ObjectKind.Proc, "P"), ScriptKind.DropCreate));
        Assert.Equal($"{OracleScripter.RecycleBin}\nDROP TABLE APP.T;\n\n{TableDdl}", Script(s, Ref(ObjectKind.Table, "T"), ScriptKind.DropCreate));
    }

    [Fact]
    public void Kinds_without_CREATE_OR_REPLACE_refuse_ALTER_with_the_reason()
    {
        Assert.Equal(OracleScripter.NoReplaceTable, Assert.Throws<NotSupportedException>(() => Script(Scripter(), Ref(ObjectKind.Table, "T"), ScriptKind.Alter)).Message);
        Assert.Equal(OracleScripter.NoReplaceSequence, Assert.Throws<NotSupportedException>(() => Script(Scripter(), Ref(ObjectKind.Sequence, "S"), ScriptKind.Alter)).Message);
    }

    [Fact]
    public void A_gone_definition_and_impossible_statements_say_so()
    {
        var gone = new OracleScripter((_, _, _) => Task.FromResult<string?>(null), Editable);
        Assert.Contains("APP.V", Assert.Throws<InvalidOperationException>(() => Script(gone, Ref(ObjectKind.View, "V"), ScriptKind.Create)).Message);
        Assert.Contains("triggers", Assert.Throws<InvalidOperationException>(() => Script(Scripter(), Ref(ObjectKind.Table, "T"), ScriptKind.Enable)).Message);
        Assert.Contains("cannot be selected from", Assert.Throws<InvalidOperationException>(() => Script(Scripter(), Ref(ObjectKind.Proc, "P"), ScriptKind.SelectTop)).Message);
        Assert.Contains("trigger cannot be executed", Assert.Throws<InvalidOperationException>(() => Script(Scripter(), Ref(ObjectKind.Trigger, "TRG"), ScriptKind.Execute)).Message);
    }

    // ------------------------------------------------------------------------------------------ execute

    static readonly OracleArgument[] ProcArgs =
    [
        new(1, "A", "NUMBER", "IN", false),
        new(2, "B", "VARCHAR2", "OUT", false),
        new(3, "C", "NUMBER", "IN/OUT", false),
        new(4, "D", "REF CURSOR", "OUT", false),
        new(5, "E", "DATE", "IN", true),
        new(6, "FLAG", "PL/SQL BOOLEAN", "OUT", false),
        new(7, "R", "PL/SQL RECORD", "OUT", false, "APP", "PKG", "REC_T"),
    ];

    static ScriptArgument Arg(string name, string? text, bool useDefault = false) =>
        new() { Name = name, Value = useDefault ? new GridValue { UseDefault = true } : text == null ? new GridValue { IsNull = true } : new GridValue { Text = text } };

    [Fact]
    public void The_template_passes_NULL_leaves_defaults_out_and_returns_OUT_values_and_cursors()
    {
        var sql = OracleScripter.ExecuteBlock("APP.P", false, ProcArgs, []);
        Assert.Equal("""
            DECLARE
              Q$B VARCHAR2(32767); -- OUT VARCHAR2
              Q$C NUMBER; -- IN OUT NUMBER
              Q$D SYS_REFCURSOR; -- OUT REF CURSOR
              Q$FLAG BOOLEAN; -- OUT PL/SQL BOOLEAN
              "Q$FLAG$text" VARCHAR2(5);
              Q$R APP.PKG.REC_T; -- OUT APP.PKG.REC_T
              Q$OUT SYS_REFCURSOR;
            BEGIN
              APP.P(
                A => NULL, -- NUMBER, required
                B => Q$B, -- OUT VARCHAR2
                C => Q$C, -- IN OUT NUMBER
                D => Q$D, -- OUT REF CURSOR
                FLAG => Q$FLAG, -- OUT PL/SQL BOOLEAN
                R => Q$R -- OUT APP.PKG.REC_T
                -- E => …, -- DATE, optional: its default applies
              );
              "Q$FLAG$text" := CASE WHEN Q$FLAG THEN 'TRUE' WHEN NOT Q$FLAG THEN 'FALSE' END;
              OPEN Q$OUT FOR SELECT Q$B AS "B", Q$C AS "C", "Q$FLAG$text" AS "FLAG" FROM DUAL;
              DBMS_SQL.RETURN_RESULT(Q$OUT);
              IF Q$D%ISOPEN THEN DBMS_SQL.RETURN_RESULT(Q$D); ELSE DBMS_OUTPUT.PUT_LINE('D was not opened by the call, so it returns no rows.'); END IF;
              -- Not returned (not a SQL type): R (APP.PKG.REC_T)
            END;
            /
            """.ReplaceLineEndings("\n"), sql);
    }

    [Fact]
    public void Dialog_values_become_typed_literals_and_an_IN_OUT_value_starts_its_local()
    {
        var args = new OracleArgument[]
        {
            new(1, "N", "NUMBER", "IN", false), new(2, "D", "DATE", "IN", false), new(3, "T", "TIMESTAMP", "IN", false), new(4, "S", "NVARCHAR2", "IN", false),
            new(5, "RW", "RAW", "IN", false), new(6, "IO", "VARCHAR2", "IN/OUT", false), new(7, "OPT", "NUMBER", "IN", true), new(8, "B", "PL/SQL BOOLEAN", "IN", false),
        };
        var sql = OracleScripter.ExecuteBlock("APP.P", false, args,
            [Arg("N", "1e3"), Arg("D", "2026-01-02 03:04:05"), Arg("T", "2026-01-02 03:04:05.5"), Arg("S", "it's"), Arg("RW", "0xA0FF"), Arg("IO", "start"), Arg("OPT", null, useDefault: true), Arg("B", "true")]);
        Assert.Contains("N => 1000, -- NUMBER, required", sql);
        Assert.Contains("D => TO_DATE('2026-01-02 03:04:05', 'YYYY-MM-DD HH24:MI:SS'),", sql);
        Assert.Contains("T => TIMESTAMP '2026-01-02 03:04:05.5',", sql);
        Assert.Contains("S => N'it''s',", sql);
        Assert.Contains("RW => HEXTORAW('A0FF'),", sql);
        Assert.Contains("Q$IO VARCHAR2(32767) := 'start'; -- IN OUT VARCHAR2", sql);
        Assert.Contains("-- OPT => …", sql);
        Assert.Contains("B => TRUE", sql);
        Assert.Contains("N => NULL", OracleScripter.ExecuteBlock("APP.P", false, args, [Arg("N", null)]));
    }

    [Theory]
    [InlineData("N", "twelve", "not a number")]
    [InlineData("D", "2026-02-30", "not a date")]
    [InlineData("RW", "A0", "binary value")]
    [InlineData("Z", "1", "no parameter Z")]
    public void A_value_the_parameter_cannot_take_is_refused_with_the_reason(string name, string text, string reason)
    {
        var args = new OracleArgument[] { new(1, "N", "NUMBER", "IN", false), new(2, "D", "DATE", "IN", false), new(3, "RW", "RAW", "IN", false) };
        var ex = Assert.Throws<ArgumentException>(() => OracleScripter.ExecuteBlock("APP.P", false, args, [Arg(name, text)]));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void An_object_typed_value_is_not_taken_from_the_dialog()
    {
        var args = new OracleArgument[] { new(1, "O", "OBJECT", "IN", false, "APP", "TY") };
        Assert.Contains("takes text, numbers, dates and RAW values only", Assert.Throws<ArgumentException>(() => OracleScripter.ExecuteBlock("APP.P", false, args, [Arg("O", "x")])).Message);
    }

    [Fact]
    public void A_function_result_comes_back_and_a_procedure_without_arguments_is_a_plain_call()
    {
        var f = OracleScripter.ExecuteBlock("APP.F", true, [new(0, null, "NUMBER", "OUT", false), new(1, "X", "NUMBER", "IN", false)], [Arg("X", "1")]);
        Assert.Contains("Q$RESULT := APP.F(\n    X => 1 -- NUMBER, required\n  );", f);
        Assert.Contains("SELECT Q$RESULT AS \"RESULT\" FROM DUAL", f);
        var cursorFn = OracleScripter.ExecuteBlock("APP.F", true, [new(0, null, "REF CURSOR", "OUT", false)], []);
        Assert.Contains("Q$RESULT := APP.F();", cursorFn);
        Assert.Contains("IF Q$RESULT%ISOPEN THEN DBMS_SQL.RETURN_RESULT(Q$RESULT);", cursorFn);
        Assert.Equal("BEGIN\n  APP.P;\nEND;\n/", OracleScripter.ExecuteBlock("APP.P", false, [], []));
        Assert.Contains("cannot be declared", Assert.Throws<NotSupportedException>(() => OracleScripter.ExecuteBlock("APP.F", true, [new(0, null, "PL/SQL RECORD", "OUT", false)], [])).Message);
    }

    [Fact]
    public void A_parameter_name_that_cannot_be_bare_is_quoted_and_its_local_too()
    {
        var sql = OracleScripter.ExecuteBlock("APP.P", false, [new(1, "out val", "VARCHAR2", "OUT", false)], []);
        Assert.Contains("\"out val\" => \"q$0_out val\"", sql);
        Assert.Contains("\"q$0_out val\" VARCHAR2(32767);", sql);
    }

    [Fact]
    public void The_provider_scripts_every_kind_through_its_scripter()
    {
        var provider = new OracleProvider();
        Assert.IsType<OracleScripter>(provider.Scripter);
        Assert.Equal("SELECT *\nFROM APP.T\nFETCH FIRST 1000 ROWS ONLY;", provider.Scripter.ScriptAsync(null!, Ref(ObjectKind.Table, "T"), ScriptKind.SelectTop, default).GetAwaiter().GetResult());
    }

    // ------------------------------------------------------------------------------------------ dependencies and definitions

    static readonly ObjectRef View = new() { ProfileId = "p", Database = "FREEPDB1", Schema = "APP", Name = "V", Kind = ObjectKind.View };

    [Fact]
    public void Dependencies_keep_the_kinds_Querithm_lists_and_leave_the_object_itself_out()
    {
        var deps = AssembleDependencies(View,
        [
            new(true, "APP", "T", "TABLE", 1), new(true, "APP", "F", "FUNCTION", 2), new(false, "APP", "P", "PROCEDURE", 3), new(false, "APP", "TRG", "TRIGGER", 4),
            new(true, "APP", "X", "NON-EXISTENT", null), new(true, "APP", "V", "VIEW", 9), new(true, "APP", "TY", "TYPE BODY", 5), new(true, "APP", "T", "TABLE", 1),
        ]);
        Assert.Equal(["Uses APP.F Func", "Uses APP.T Table", "Uses APP.TY Type", "UsedBy APP.P Proc", "UsedBy APP.TRG Trigger"],
            deps.Select(d => $"{d.Direction} {d.Object.Schema}.{d.Object.Name} {d.Object.Kind}"));
        Assert.All(deps, d => Assert.Equal("FREEPDB1", d.Object.Database));
        // A materialized view's container table has its own name: not a dependency.
        Assert.Empty(AssembleDependencies(View, [new(true, "APP", "V", "TABLE", 7)]));
        // A package body uses its specification, which is kept.
        var body = new ObjectRef { Schema = "APP", Name = "PKG", Kind = ObjectKind.PackageBody };
        Assert.Equal(ObjectKind.Package, Assert.Single(AssembleDependencies(body, [new(true, "APP", "PKG", "PACKAGE", 8)])).Object.Kind);
    }

    [Theory]
    [InlineData(ObjectKind.View, new[] { "VIEW", "MATERIALIZED VIEW" })]
    [InlineData(ObjectKind.Type, new[] { "TYPE", "TYPE BODY" })]
    [InlineData(ObjectKind.PackageBody, new[] { "PACKAGE BODY" })]
    public void Each_kind_reads_its_dictionary_types(ObjectKind kind, string[] types) => Assert.Equal(types, ObjectTypes(kind));

    [Fact]
    public void A_view_definition_names_its_columns_and_a_unit_is_qualified_and_ends_in_a_slash()
    {
        Assert.Equal("CREATE OR REPLACE VIEW APP.V (A, \"b c\") AS\nSELECT a, b FROM t;", ViewDefinition("APP.V", ["A", "b c"], "SELECT a, b FROM t  \n"));
        Assert.Equal("CREATE OR REPLACE VIEW APP.V AS\nSELECT 1 FROM dual;", ViewDefinition("APP.V", [], "SELECT 1 FROM dual;"));
        Assert.Equal("CREATE OR REPLACE PROCEDURE APP.P AS BEGIN NULL; END;\n/", UnitDefinition("APP.P", "PROCEDURE p AS BEGIN NULL; END;\n"));
        Assert.True(IsWrapped("PROCEDURE p wrapped \na000000\n367\nabcd"));
        Assert.False(IsWrapped("PROCEDURE wrapped_name AS BEGIN NULL; END;"));
        Assert.False(IsWrapped("PROCEDURE wrapped AS BEGIN NULL; END;"));
        Assert.True(IsWrapped("PACKAGE BODY app.pkg wrapped\na000000"));
    }
}
