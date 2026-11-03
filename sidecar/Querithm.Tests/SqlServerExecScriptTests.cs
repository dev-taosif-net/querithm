using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Script as EXECUTE for SQL Server procedures: separators survive the type comments, and required parameters are marked.</summary>
public class SqlServerExecScriptTests
{
    const string Proc = """
        CREATE PROCEDURE [dbo].[usp_Onboard]
            @PartName nvarchar(200),
            @EmployeeId bigint = NULL,
            @msg nvarchar(max) OUTPUT,
            @Gender nvarchar(10) = N'x', -- trailing comment
            @IsForeign bit = 0
        AS SELECT 1;
        """;

    static readonly (string, string, bool)[] Params =
    [
        ("@PartName", "nvarchar(200)", false), ("@EmployeeId", "bigint", false), ("@msg", "nvarchar(max)", true),
        ("@Gender", "nvarchar(10)", false), ("@IsForeign", "bit", false),
    ];

    [Fact]
    public void Separators_come_before_the_comment_and_required_parameters_are_marked()
    {
        var sql = SqlServerScripter.ExecProc("[dbo].[usp_Onboard]", Params, SqlServerScripter.ParamDefaults(Proc));
        Assert.Equal("""
            EXEC [dbo].[usp_Onboard]
                @PartName = NULL, -- nvarchar(200), required
                @EmployeeId = NULL, -- bigint, optional (default NULL)
                @msg = @msg OUTPUT, -- nvarchar(max), required
                @Gender = NULL, -- nvarchar(10), optional (default N'x')
                @IsForeign = NULL; -- bit, optional (default 0)

            """.ReplaceLineEndings("\n"), sql);
    }

    [Fact]
    public void Generated_exec_parses_as_one_call_with_every_parameter()
    {
        var sql = "DECLARE @msg nvarchar(max);\n" + SqlServerScripter.ExecProc("[dbo].[usp_Onboard]", Params, SqlServerScripter.ParamDefaults(Proc));
        var script = (Microsoft.SqlServer.TransactSql.ScriptDom.TSqlScript)Querithm.Intelligence.TSql.TSqlParsing.NewParser()
            .Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
        var exec = Assert.IsType<Microsoft.SqlServer.TransactSql.ScriptDom.ExecuteStatement>(script.Batches[0].Statements[1]);
        Assert.Equal(5, ((Microsoft.SqlServer.TransactSql.ScriptDom.ExecutableProcedureReference)exec.ExecuteSpecification.ExecutableEntity).Parameters.Count);
    }

    [Fact]
    public void Encrypted_or_unparsable_definition_omits_the_required_status()
    {
        Assert.Null(SqlServerScripter.ParamDefaults(null));
        Assert.Null(SqlServerScripter.ParamDefaults("CREATE PROCEDURE p @a int = AS"));
        var sql = SqlServerScripter.ExecProc("[dbo].[p]", [("@a", "int", false)], null);
        Assert.Equal("EXEC [dbo].[p]\n    @a = NULL; -- int\n", sql);
    }

    [Fact]
    public void Multi_line_default_stays_inside_the_comment()
    {
        const string def = "CREATE PROCEDURE p @a nvarchar(20) = N'line1\r\nline2', @b int AS SELECT 1";
        var sql = SqlServerScripter.ExecProc("[dbo].[p]", [("@a", "nvarchar(20)", false), ("@b", "int", false)], SqlServerScripter.ParamDefaults(def));
        Assert.Equal("EXEC [dbo].[p]\n    @a = NULL, -- nvarchar(20), optional (default N'line1 line2')\n    @b = NULL; -- int, required\n", sql);
        Querithm.Intelligence.TSql.TSqlParsing.NewParser().Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Procedure_without_parameters_is_a_bare_exec()
    {
        Assert.Equal("EXEC [dbo].[p];\n", SqlServerScripter.ExecProc("[dbo].[p]", [], SqlServerScripter.ParamDefaults("CREATE PROC p AS SELECT 1")));
    }
}
