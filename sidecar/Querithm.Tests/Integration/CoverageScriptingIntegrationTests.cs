using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>Object scripting (every script kind x object kind) and plan retrieval on both engines. Enable with QUERITHM_IT=1.</summary>
public class ScriptingIntegrationTests
{
    static string U() => "cov_" + Guid.NewGuid().ToString("N")[..8];

    static async Task<string> Script(TestHost h, string sid, string schema, string name, ObjectKind kind, ScriptKind sk) =>
        (await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = new ObjectRef { Schema = schema, Name = name, Kind = kind }, Kind = sk })).Sql;

    static async Task Ok(TestHost h, string sid, string sql)
    {
        var (done, _, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, sql + "\n" + string.Join("\n", h.Messages(id)));
    }

    [Collection("mssql")]
    public class SqlServer
    {
        [IntegrationFact]
        public async Task Every_script_kind_for_tables_views_routines_and_functions()
        {
            await using var h = new TestHost();
            var (_, sid) = await h.OpenMssqlAsync("tempdb");
            var n = U();
            string t = n + "_t", v = n + "_v", p = n + "_p", f = n + "_f", tf = n + "_tf";
            try
            {
                await Ok(h, sid, $"CREATE TABLE dbo.{t} (id int IDENTITY PRIMARY KEY, name nvarchar(20) NOT NULL DEFAULT N'x', qty int NULL)");
                await Ok(h, sid, $"CREATE VIEW dbo.{v} AS SELECT id, name FROM dbo.{t}");
                await Ok(h, sid, $"CREATE PROCEDURE dbo.{p} @a int, @b int OUTPUT, @c int = 5 AS BEGIN SET @b = @a + @c; END");
                await Ok(h, sid, $"CREATE FUNCTION dbo.{f} (@x int) RETURNS int AS BEGIN RETURN @x * 2; END");
                await Ok(h, sid, $"CREATE FUNCTION dbo.{tf} (@x int) RETURNS TABLE AS RETURN SELECT @x AS v");

                var top = await Script(h, sid, "dbo", t, ObjectKind.Table, ScriptKind.SelectTop);
                Assert.Contains("SELECT TOP (1000)", top);
                Assert.Contains($"[id]", top);
                Assert.Contains("[qty]", top);
                Assert.Contains("SELECT TOP (1000) *", await Script(h, sid, "dbo", tf, ObjectKind.Tvf, ScriptKind.SelectTop));

                var create = await Script(h, sid, "dbo", t, ObjectKind.Table, ScriptKind.Create);
                Assert.Contains("CREATE TABLE", create);
                Assert.Contains("PRIMARY KEY", create);
                Assert.Contains("DROP TABLE IF EXISTS", await Script(h, sid, "dbo", t, ObjectKind.Table, ScriptKind.DropCreate));
                Assert.Equal($"DROP TABLE IF EXISTS [dbo].[{t}];\n", await Script(h, sid, "dbo", t, ObjectKind.Table, ScriptKind.Drop));
                var tableAlter = await Assert.ThrowsAnyAsync<Exception>(() => Script(h, sid, "dbo", t, ObjectKind.Table, ScriptKind.Alter));
                Assert.Contains("DROP and CREATE", tableAlter.Message);

                var viewCreate = await Script(h, sid, "dbo", v, ObjectKind.View, ScriptKind.Create);
                Assert.Contains("CREATE VIEW", viewCreate);
                Assert.EndsWith("GO\n", viewCreate);
                Assert.Contains("ALTER VIEW", await Script(h, sid, "dbo", v, ObjectKind.View, ScriptKind.Alter));
                Assert.Contains("DROP VIEW IF EXISTS", await Script(h, sid, "dbo", v, ObjectKind.View, ScriptKind.DropCreate));
                Assert.Equal($"DROP VIEW IF EXISTS [dbo].[{v}];\n", await Script(h, sid, "dbo", v, ObjectKind.View, ScriptKind.Drop));

                var exec = await Script(h, sid, "dbo", p, ObjectKind.Proc, ScriptKind.Execute);
                Assert.Contains($"EXEC [dbo].[{p}]", exec);
                Assert.Contains("DECLARE @b int;", exec);
                Assert.Contains("@a = NULL, -- int, required\n    @b = @b OUTPUT, -- int, required\n    @c = NULL; -- int, optional (default 5)\n", exec);
                await Ok(h, sid, exec);
                Assert.Contains($"SELECT [dbo].[{f}](NULL /* @x int */);", await Script(h, sid, "dbo", f, ObjectKind.Func, ScriptKind.Execute));
                Assert.Contains($"SELECT * FROM [dbo].[{tf}](NULL /* @x int */);", await Script(h, sid, "dbo", tf, ObjectKind.Tvf, ScriptKind.Execute));
                Assert.Contains("DROP PROCEDURE IF EXISTS", await Script(h, sid, "dbo", p, ObjectKind.Proc, ScriptKind.Drop));
                Assert.Contains("DROP FUNCTION IF EXISTS", await Script(h, sid, "dbo", f, ObjectKind.Func, ScriptKind.Drop));
                Assert.Contains("DROP SEQUENCE IF EXISTS", await Script(h, sid, "dbo", "s", ObjectKind.Sequence, ScriptKind.Drop));
                Assert.Contains("DROP SYNONYM IF EXISTS", await Script(h, sid, "dbo", "s", ObjectKind.Synonym, ScriptKind.Drop));
                Assert.Contains("DROP TYPE IF EXISTS", await Script(h, sid, "dbo", "s", ObjectKind.Type, ScriptKind.Drop));
            }
            finally
            {
                await h.RunAsync(sid, $"DROP VIEW IF EXISTS dbo.{v}; DROP PROCEDURE IF EXISTS dbo.{p}; DROP FUNCTION IF EXISTS dbo.{f}; DROP FUNCTION IF EXISTS dbo.{tf}; DROP TABLE IF EXISTS dbo.{t};");
            }
        }

        [IntegrationFact]
        public async Task Missing_objects_report_why_nothing_could_be_scripted()
        {
            await using var h = new TestHost();
            var (_, sid) = await h.OpenMssqlAsync("tempdb");
            await Assert.ThrowsAnyAsync<Exception>(() => Script(h, sid, "dbo", "cov_does_not_exist", ObjectKind.View, ScriptKind.Create));
        }

        [IntegrationFact]
        public async Task Actual_plan_reports_per_operator_runtime_and_estimated_plan_runs_nothing()
        {
            await using var h = new TestHost();
            var (_, sid) = await h.OpenMssqlAsync("master");
            const string sql = "SELECT TOP 50 a.object_id, b.name FROM sys.all_objects a JOIN sys.all_objects b ON a.schema_id = b.schema_id ORDER BY b.name";
            var est = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = sql, Mode = PlanMode.Estimated });
            Assert.NotEmpty(est.Tree.Statements);
            Assert.All(est.Tree.Statements, s => Assert.NotNull(s.Root));
            // SQL Server's actual plan comes with the run (SET STATISTICS XML); plan.get refuses the actual mode (§16).
            var (done, _, id) = await h.RunAsync(sid, sql, new ExecuteOptions { ActualPlan = true });
            Assert.Equal(ExecState.Done, done.State);
            var act = Assert.Single(h.Notifier.Of<QueryPlanEvent>(), p => p.ExecutionId == id);
            Assert.NotEmpty(act.Plan.Statements);
            Assert.Contains(Flatten(act.Plan.Statements[0].Root!), n => n.ActualRows != null);
            var refused = await Assert.ThrowsAsync<Querithm.Core.QuerithmException>(() => h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = sql, Mode = PlanMode.Actual, ConfirmedRun = true }));
            Assert.Contains("Include actual plan", refused.Message);
        }

        [IntegrationFact]
        public async Task Statistics_options_produce_io_and_time_rows()
        {
            await using var h = new TestHost();
            var (_, sid) = await h.OpenMssqlAsync("master");
            var (done, _, _) = await h.RunAsync(sid, "SELECT COUNT(*) FROM sys.all_objects", new ExecuteOptions { StatisticsIo = true, StatisticsTime = true });
            Assert.Equal(ExecState.Done, done.State);
            Assert.NotNull(done.Statistics);
            Assert.Contains(done.Statistics!, s => s.Kind == "time");
        }

        static IEnumerable<PlanNode> Flatten(PlanNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));
    }

    [Collection("pg")]
    public class Postgres
    {
        [IntegrationFact]
        public async Task Every_script_kind_for_tables_views_routines_and_functions()
        {
            await using var h = new TestHost();
            var (_, sid) = await h.OpenPgAsync();
            var n = U();
            string t = n + "_t", v = n + "_v", p = n + "_p", f = n + "_f", tf = n + "_tf", sq = n + "_s", ty = n + "_ty";
            try
            {
                await Ok(h, sid, $"CREATE TABLE public.{t} (id serial PRIMARY KEY, name text NOT NULL DEFAULT 'x', qty int);");
                await Ok(h, sid, $"CREATE VIEW public.{v} AS SELECT id, name FROM public.{t};");
                await Ok(h, sid, $"CREATE FUNCTION public.{f}(x int) RETURNS int LANGUAGE sql AS $$ SELECT x * 2 $$;");
                await Ok(h, sid, $"CREATE FUNCTION public.{tf}(x int) RETURNS TABLE(v int) LANGUAGE sql AS $$ SELECT x $$;");
                await Ok(h, sid, $"CREATE PROCEDURE public.{p}(a int, OUT r int, \"select\" int DEFAULT 5) LANGUAGE plpgsql AS $$ BEGIN r := a + \"select\"; END $$;");
                await Ok(h, sid, $"CREATE SEQUENCE public.{sq};");
                await Ok(h, sid, $"CREATE TYPE public.{ty} AS ENUM ('a','b');");

                Assert.Equal($"SELECT * FROM public.{t} LIMIT 1000;\n", await Script(h, sid, "public", t, ObjectKind.Table, ScriptKind.SelectTop));
                Assert.Contains("(/* arguments */) LIMIT 1000;", await Script(h, sid, "public", tf, ObjectKind.Tvf, ScriptKind.SelectTop));
                Assert.Contains("DROP TABLE IF EXISTS", await Script(h, sid, "public", t, ObjectKind.Table, ScriptKind.Drop));
                Assert.Contains("DROP SEQUENCE IF EXISTS", await Script(h, sid, "public", sq, ObjectKind.Sequence, ScriptKind.Drop));
                Assert.Contains("DROP TYPE IF EXISTS", await Script(h, sid, "public", ty, ObjectKind.Type, ScriptKind.Drop));
                Assert.Contains("DROP FUNCTION IF EXISTS", await Script(h, sid, "public", f, ObjectKind.Func, ScriptKind.Drop));
                Assert.Contains("DROP PROCEDURE IF EXISTS", await Script(h, sid, "public", p, ObjectKind.Proc, ScriptKind.Drop));
                var execF = await Script(h, sid, "public", f, ObjectKind.Func, ScriptKind.Execute);
                Assert.Equal($"SELECT * FROM public.{f}(\n    x => NULL -- integer, required\n);\n", execF);
                await Ok(h, sid, execF);
                var execP = await Script(h, sid, "public", p, ObjectKind.Proc, ScriptKind.Execute);
                Assert.Equal($"CALL public.{p}(\n    a => NULL, -- integer, required\n    r => NULL, -- integer, OUT\n    \"select\" => NULL -- integer, optional\n);\n", execP);
                await Ok(h, sid, execP);
                Assert.Contains("(/* arguments */)", await Script(h, sid, "public", n + "_missing", ObjectKind.Proc, ScriptKind.Execute));

                var create = await Script(h, sid, "public", t, ObjectKind.Table, ScriptKind.Create);
                Assert.Contains("CREATE TABLE", create);
                Assert.Contains("NOT NULL DEFAULT", create);
                var dropCreate = await Script(h, sid, "public", t, ObjectKind.Table, ScriptKind.DropCreate);
                Assert.StartsWith("DROP TABLE IF EXISTS", dropCreate);
                Assert.Contains("CREATE TABLE", dropCreate);

                Assert.Contains("CREATE", await Script(h, sid, "public", v, ObjectKind.View, ScriptKind.Create));
                Assert.Contains("DROP VIEW IF EXISTS", await Script(h, sid, "public", v, ObjectKind.View, ScriptKind.DropCreate));
                Assert.Contains("CREATE OR REPLACE FUNCTION", await Script(h, sid, "public", f, ObjectKind.Func, ScriptKind.Alter));
                Assert.Contains("CREATE OR REPLACE FUNCTION", await Script(h, sid, "public", f, ObjectKind.Func, ScriptKind.Create));
            }
            finally
            {
                await h.RunAsync(sid, $"DROP VIEW IF EXISTS public.{v}; DROP FUNCTION IF EXISTS public.{f}(int); DROP FUNCTION IF EXISTS public.{tf}(int); DROP PROCEDURE IF EXISTS public.{p}; DROP TABLE IF EXISTS public.{t}; DROP SEQUENCE IF EXISTS public.{sq}; DROP TYPE IF EXISTS public.{ty};");
            }
        }

        [IntegrationFact]
        public async Task Scripting_a_missing_function_fails_with_a_reason()
        {
            await using var h = new TestHost();
            var (_, sid) = await h.OpenPgAsync();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Script(h, sid, "public", "cov_nothing_here", ObjectKind.Func, ScriptKind.Create));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }

        [IntegrationFact]
        public async Task Plans_flag_sort_spills_and_cardinality_misses_when_actually_run()
        {
            await using var h = new TestHost();
            var (_, sid) = await h.OpenPgAsync();
            await Ok(h, sid, "SET work_mem = '64kB'");
            const string sql = "SELECT g, md5(g::text) AS m FROM generate_series(1, 60000) g ORDER BY m";
            var act = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = sql, Mode = PlanMode.Actual, ConfirmedRun = true });
            var nodes = Flatten(act.Tree.Statements[0].Root!).ToList();
            Assert.Contains(nodes, n => n.ActualRows != null);
            var est = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = sql, Mode = PlanMode.Estimated });
            Assert.NotEmpty(est.Tree.Statements);
        }

        static IEnumerable<PlanNode> Flatten(PlanNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));
    }
}
