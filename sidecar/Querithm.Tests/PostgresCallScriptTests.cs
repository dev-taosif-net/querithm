using Querithm.Providers.Postgres;
using static Querithm.Providers.Postgres.PostgresScripter;

namespace Querithm.Tests;

/// <summary>Script as EXECUTE for PostgreSQL routines: one argument per line, separators survive the comments, required arguments are marked.</summary>
public class PostgresCallScriptTests
{
    [Fact]
    public void Procedure_lists_inputs_and_out_arguments_by_name()
    {
        var sql = CallRoutine("public.onboard", true,
        [
            new("part_name", "text", 'i', false), new("employee_id", "bigint", 'i', true),
            new("msg", "text", 'o', false), new("\"Gender\"", "text", 'b', true),
        ]);
        Assert.Equal("""
            CALL public.onboard(
                part_name => NULL, -- text, required
                employee_id => NULL, -- bigint, optional
                msg => NULL, -- text, OUT
                "Gender" => NULL -- text, optional
            );

            """.ReplaceLineEndings("\n"), sql);
    }

    [Fact]
    public void Function_takes_only_its_inputs()
    {
        var sql = CallRoutine("public.f", false, [new("x", "integer", 'i', false), new("total", "integer", 'o', false), new("v", "integer", 't', false)]);
        Assert.Equal("SELECT * FROM public.f(\n    x => NULL -- integer, required\n);\n", sql);
    }

    [Fact]
    public void Unnamed_or_variadic_arguments_are_positional()
    {
        Assert.Equal("SELECT * FROM public.f(\n    NULL, -- integer, required\n    NULL -- b text, optional\n);\n",
            CallRoutine("public.f", false, [new(null, "integer", 'i', false), new("b", "text", 'i', true)]));
        Assert.Equal("SELECT * FROM public.f(\n    NULL, -- a integer, required\n    NULL -- xs integer[], variadic\n);\n",
            CallRoutine("public.f", false, [new("a", "integer", 'i', false), new("xs", "integer[]", 'v', false)]));
    }

    [Fact]
    public void Routine_without_arguments_is_a_bare_call()
    {
        Assert.Equal("CALL public.p();\n", CallRoutine("public.p", true, []));
        Assert.Equal("SELECT * FROM public.f();\n", CallRoutine("public.f", false, [new("v", "integer", 't', false)]));
    }
}
