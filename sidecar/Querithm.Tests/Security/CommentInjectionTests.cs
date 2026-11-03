using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Compare;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.TSql;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests.Security;

/// <summary>
/// L6: names a server, a file or the user chose go into the comments of generated scripts (migration, sync, refactor,
/// scripting). A line break or <c>*/</c> in one must never turn the rest of the name into a statement.
/// </summary>
public class CommentInjectionTests
{
    /// <summary>Each payload's tail, if it escaped its comment, would be the statement DROP TABLE … pwned.</summary>
    static readonly string[] PayloadList =
    [
        "x\nDROP TABLE dbo.pwned;--", "x\rDROP TABLE dbo.pwned;--", "x\r\nDROP TABLE dbo.pwned;--", "x\u0085DROP TABLE dbo.pwned;--",
        "x\u2028DROP TABLE dbo.pwned;--", "x\u2029DROP TABLE dbo.pwned;--", "x*/ DROP TABLE dbo.pwned; /*", "x/* DROP TABLE dbo.pwned; */",
    ];

    public static TheoryData<string> Payloads => new(PayloadList);

    /// <summary>True when <c>pwned</c> is a code token of the engine's own lexer (outside comments, strings and quoted names).</summary>
    static bool RunsAsCode(Engine engine, string script) => engine switch
    {
        Engine.Mssql => TSqlParsing.NewParser().GetTokenStream(new StringReader(script), out _)
            .Any(t => t.TokenType == TSqlTokenType.Identifier && string.Equals(t.Text, "pwned", StringComparison.OrdinalIgnoreCase)),
        Engine.Pg => PgTokenizer.Tokenize(script).Any(t => t.Kind == PgTokenKind.Word && t.Text.Equals("pwned", StringComparison.OrdinalIgnoreCase)),
        Engine.MySql => MySqlTokenizer.Tokenize(script).Any(t => t.Kind == MySqlTokenKind.Word && t.Text.Equals("pwned", StringComparison.OrdinalIgnoreCase)),
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    [Fact]
    public void A_carriage_return_ends_a_postgres_or_duckdb_line_comment_as_the_server_reads_it()
    {
        // PostgreSQL's and DuckDB's lexers end "--" at CR too: the statement after it runs, so the classifier must see it.
        const string sql = "SELECT 1; -- note\rDROP TABLE public.t;";
        Assert.Contains(PgTokenizer.Tokenize(sql), t => t.Kind == PgTokenKind.Word && t.Text == "DROP");
        Assert.NotEmpty(new PgLanguageService().Classify(sql).Destructive);
        Assert.NotEmpty(new Querithm.Intelligence.DuckDb.DuckDbLanguageService().Classify(sql).Destructive);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Safe_text_keeps_one_line_and_no_comment_delimiter(string payload)
    {
        var safe = CommentText.Safe(payload);
        Assert.DoesNotContain('\n', safe);
        Assert.DoesNotContain('\r', safe);
        Assert.DoesNotContain('\u0085', safe);
        Assert.DoesNotContain('\u2028', safe);
        Assert.DoesNotContain('\u2029', safe);
        Assert.DoesNotContain("*/", safe);
        Assert.DoesNotContain("/*", safe);
        Assert.Contains("DROP TABLE dbo.pwned", safe); // still readable
    }

    [Fact]
    public void Safe_text_leaves_ordinary_names_alone()
    {
        Assert.Equal("dbo.Orders → dbo.Orders2 in Prod · Sales", CommentText.Safe("dbo.Orders → dbo.Orders2 in Prod · Sales"));
        Assert.Equal("a * b / c\td", CommentText.Safe("a * b / c\td"));
        Assert.Equal("* / * /", CommentText.Safe("*/*/"));
        Assert.Equal("", CommentText.Safe(null));
    }

    public static TheoryData<Engine, string> EnginePayloads
    {
        get
        {
            var data = new TheoryData<Engine, string>();
            foreach (var e in new[] { Engine.Mssql, Engine.Pg, Engine.MySql })
                foreach (var p in PayloadList) data.Add(e, p);
            return data;
        }
    }

    static ISchemaCompareDialect Dialect(Engine e) => e switch
    {
        Engine.Mssql => new SqlServerSchemaCompare(),
        Engine.Pg => new PostgresSchemaCompare(),
        _ => new MySqlSchemaCompare(),
    };

    static string Schema(Engine e) => e switch { Engine.Mssql => "dbo", Engine.Pg => "public", _ => "db" };

    static SchemaModel Model(Engine e, params ModelTable[] tables) => new()
    {
        Engine = e, MajorVersion = e == Engine.MySql ? 8 : 16, Database = "db", Schemas = [Schema(e)], Tables = tables, Modules = [], Sequences = [], Types = [],
    };

    static ModelTable Table(Engine e, string name, params string[] columns) => new()
    {
        Schema = Schema(e), Name = name, PrimaryKey = new ModelKey { Name = "pk_" + name.Length, Columns = ["id"] },
        Columns = [new ModelColumn { Name = "id", Type = e == Engine.Pg ? "integer" : "int", Nullable = false },
            .. columns.Select(c => new ModelColumn { Name = c, Type = e == Engine.Pg ? "integer" : "int", Nullable = true })],
    };

    [Theory]
    [MemberData(nameof(EnginePayloads))]
    public void A_migration_script_keeps_hostile_names_inside_their_comments(Engine engine, string payload)
    {
        // A new table (its step comment names it) and a dropped column left out as data loss (the "Not included" list
        // names it), from a source and a target that each carry a hostile name.
        var source = Model(engine, Table(engine, payload), Table(engine, "kept"));
        var target = Model(engine, Table(engine, "kept", payload));
        var c = SchemaComparer.Compare(source, target, Dialect(engine));
        var script = new MigrationScriptBuilder(c).Build(c.Items.Select(i => i.Key).ToArray(), [], includeDataLoss: false, "-- test").Sql;
        Assert.Contains("pwned", script);
        Assert.False(RunsAsCode(engine, script), script);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void A_sql_server_call_script_keeps_hostile_parameter_types_inside_their_comments(string payload)
    {
        (string, string, bool)[] ps = [("@a", payload, false), ("@b", "int", true)];
        foreach (var script in new[] { SqlServerScripter.SelectFunction("dbo.f", false, ps), SqlServerScripter.SelectFunction("dbo.f", true, ps), SqlServerScripter.ExecProc("dbo.p", ps, null) })
        {
            Assert.Contains("pwned", script);
            Assert.False(RunsAsCode(Engine.Mssql, script), script);
        }
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void A_postgres_call_script_keeps_hostile_argument_types_inside_their_comments(string payload)
    {
        foreach (var named in new[] { true, false })
        {
            var script = PostgresScripter.CallRoutine("public.f", true, [new(named ? "a" : null, payload, 'i', false), new("b", "integer", 'i', true)]);
            Assert.Contains("pwned", script);
            Assert.False(RunsAsCode(Engine.Pg, script), script);
        }
    }
}
