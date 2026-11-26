using Querithm.Contract;
using Querithm.Core;

namespace Querithm.Tests.Integration;

/// <summary>
/// Completion audit 2026-11-25 S1 against the Docker fixture (QUERITHM_IT=1): with NO_BACKSLASH_ESCAPES in the session's
/// sql_mode, MySQL and MariaDB read <c>SELECT 'a\'; DROP TABLE t; -- '</c> as a SELECT and a DROP. The production guard now
/// stops it; once confirmed, the server really drops the table — the threat the guard covers.
/// </summary>
public sealed class MySqlSqlModeGuardTests
{
    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task A_drop_hidden_by_no_backslash_escapes_is_stopped_on_production(string product)
    {
        await MySqlIntegrationTests.EnsureDatabaseAsync(product);
        await using var h = new MySqlIntegrationTests.Host();
        var p = await h.Rpc.ProfileSave(MySqlIntegrationTests.ProfileFor(product) with { Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword });
        var sid = s.SessionId;
        var confirmed = new ExecuteOptions { ConfirmedProd = true };

        var setup = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DROP TABLE IF EXISTS zz_nbe; CREATE TABLE zz_nbe (id INT);", Options = confirmed with { ConfirmedDestructive = true } });
        await h.Notifier.WaitDone(setup.ExecutionId!);

        const string script = "SET SESSION sql_mode = CONCAT(@@sql_mode, ',NO_BACKSLASH_ESCAPES');\nSELECT 'a\\'; DROP TABLE zz_nbe; -- ';";
        var blocked = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = script, Options = confirmed });
        Assert.Equal(GuardReason.Destructive, blocked.Blocked?.Reason);
        Assert.Contains(blocked.Blocked!.Findings, f => f.Rule == "drop");
        Assert.True(await TableExists(h, sid, confirmed), "the guard let the DROP run");

        // Confirmed: the server reads the hidden DROP and runs it.
        var run = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = script, Options = confirmed with { ConfirmedDestructive = true } });
        await h.Notifier.WaitDone(run.ExecutionId!);
        Assert.False(await TableExists(h, sid, confirmed), "the server did not run the hidden DROP: the premise of the guard changed");
    }

    /// <summary>
    /// The four readings are uniform; the server switches mode at the SET. A first statement it reads in the default mode
    /// shifts every uniform reading's quotes, so none of them saw the DROP (probe 2026-11-25) — the SET is now the finding.
    /// </summary>
    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task A_drop_after_a_set_of_sql_mode_behind_a_shifting_prefix_is_stopped_on_production(string product)
    {
        await MySqlIntegrationTests.EnsureDatabaseAsync(product);
        await using var h = new MySqlIntegrationTests.Host();
        var p = await h.Rpc.ProfileSave(MySqlIntegrationTests.ProfileFor(product) with { Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword });
        var sid = s.SessionId;
        var confirmed = new ExecuteOptions { ConfirmedProd = true };

        var setup = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DROP TABLE IF EXISTS zz_nbe2; CREATE TABLE zz_nbe2 (id INT);", Options = confirmed with { ConfirmedDestructive = true } });
        await h.Notifier.WaitDone(setup.ExecutionId!);

        var script = """SELECT 'x\'y'; SET SESSION sql_mode = CONCAT(@@sql_mode, ',NO_BACKSLASH_ESCAPES');""" + "\n" + """SELECT 'a\'; DROP TABLE zz_nbe2; -- ';""";
        var blocked = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = script, Options = confirmed });
        Assert.Equal(GuardReason.Destructive, blocked.Blocked?.Reason);
        Assert.Contains(blocked.Blocked!.Findings, f => f.Rule == "readingChanged");
        Assert.True(await TableExists(h, sid, confirmed, "zz_nbe2"), "the guard let the DROP run");

        // Confirmed: the server switches mode at the SET and runs the DROP no uniform reading saw.
        var run = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = script, Options = confirmed with { ConfirmedDestructive = true } });
        await h.Notifier.WaitDone(run.ExecutionId!);
        Assert.False(await TableExists(h, sid, confirmed, "zz_nbe2"), "the server did not run the hidden DROP: the premise of the guard changed");
    }

    /// <summary>
    /// Code audit 2026-11-25 (H1 / C1): an estimated plan sends EXPLAIN FORMAT=JSON + the text, which the server splits by
    /// its own sql_mode — under NO_BACKSLASH_ESCAPES it ran the hidden DROP with no transaction around it, on a Dev
    /// session without a single prompt. The plan is refused now, and the table is still there.
    /// </summary>
    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task An_estimated_plan_does_not_run_a_drop_hidden_by_no_backslash_escapes(string product)
    {
        await MySqlIntegrationTests.EnsureDatabaseAsync(product);
        await using var h = new MySqlIntegrationTests.Host();
        var p = await h.Rpc.ProfileSave(MySqlIntegrationTests.ProfileFor(product));
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword })).SessionId;
        var options = new ExecuteOptions { ConfirmedDestructive = true };
        var setup = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DROP TABLE IF EXISTS zz_nbe_plan; CREATE TABLE zz_nbe_plan (id INT);", Options = options });
        await h.Notifier.WaitDone(setup.ExecutionId!);

        // A transaction pins the connection, so the plan would run where the mode was set.
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var set = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SET SESSION sql_mode = CONCAT(@@sql_mode, ',NO_BACKSLASH_ESCAPES')", Options = options });
        await h.Notifier.WaitDone(set.ExecutionId!);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanGet(new PlanParams
        {
            SessionId = sid, Sql = "SELECT 'a\\'; DROP TABLE zz_nbe_plan; -- '", Mode = PlanMode.Estimated,
        }));
        Assert.Equal("unsupported", ex.Code);
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
        Assert.True(await TableExists(h, sid, options, "zz_nbe_plan"), "the estimated plan ran the hidden DROP");
    }

    /// <summary>Code audit 2026-11-25 (C2): under a client DELIMITER the server runs what follows a semicolon.</summary>
    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task A_plan_does_not_run_a_drop_after_a_semicolon_under_a_client_delimiter(string product)
    {
        await MySqlIntegrationTests.EnsureDatabaseAsync(product);
        await using var h = new MySqlIntegrationTests.Host();
        var p = await h.Rpc.ProfileSave(MySqlIntegrationTests.ProfileFor(product) with { Environment = EnvironmentKind.Prod });
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword })).SessionId;
        var confirmed = new ExecuteOptions { ConfirmedProd = true };
        var options = confirmed with { ConfirmedDestructive = true };
        var setup = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DROP TABLE IF EXISTS zz_delim_plan; CREATE TABLE zz_delim_plan (id INT);", Options = options });
        await h.Notifier.WaitDone(setup.ExecutionId!);

        foreach (var mode in new[] { PlanMode.Estimated, PlanMode.Actual })
        {
            var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanGet(new PlanParams
            {
                SessionId = sid, Sql = "DELIMITER $$\nSELECT 1; DROP TABLE zz_delim_plan $$", Mode = mode, ConfirmedRun = true,
            }));
            Assert.Equal("unsupported", ex.Code);
        }
        // query.execute finds the DROP now and stops for the confirmation.
        var blocked = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELIMITER $$\nSELECT 1; DROP TABLE zz_delim_plan $$", Options = confirmed });
        Assert.Equal(GuardReason.Destructive, blocked.Blocked?.Reason);
        Assert.True(await TableExists(h, sid, options, "zz_delim_plan"), "a plan or run let the hidden DROP through");
    }

    static async Task<bool> TableExists(MySqlIntegrationTests.Host h, string sid, ExecuteOptions options, string table = "zz_nbe")
    {
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams
        {
            SessionId = sid, Options = options,
            Sql = $"SELECT COUNT(*) AS n FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'",
        });
        await h.Notifier.WaitDone(r.ExecutionId!);
        var batch = h.Frames.For(r.ExecutionId!).Where(f => f.Kind == 0x02).Select(f => TestHost.ReadBatch(f.Payload)).Single();
        return MySqlIntegrationTests.Text(batch, "n") != "0";
    }
}

/// <summary>
/// The same for PostgreSQL (QUERITHM_IT=1): with <c>standard_conforming_strings = off</c> the server (and Npgsql's own
/// statement split, which follows the setting) runs <c>SELECT 'a\'' ; DROP TABLE t; -- '</c> as a SELECT and a DROP.
/// </summary>
[Collection("pg")]
public sealed class PgStringModeGuardTests
{
    [IntegrationFact]
    public async Task A_drop_hidden_by_non_standard_strings_is_stopped_on_production()
    {
        await using var h = new TestHost();
        var (dev, _) = await h.OpenPgAsync();
        var prod = await h.Rpc.ProfileSave(dev with { Id = "", Name = "Fixture PG prod", Environment = EnvironmentKind.Prod });
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = prod.Id, Secret = TestHost.PgPassword, Database = "postgres" })).SessionId;
        var confirmed = new ExecuteOptions { ConfirmedProd = true };
        await h.RunAsync(sid, "DROP TABLE IF EXISTS zz_scs; CREATE TABLE zz_scs (id int);", confirmed with { ConfirmedDestructive = true });

        const string script = "SET standard_conforming_strings = off;\nSELECT 'a\\'' ; DROP TABLE zz_scs; -- ';";
        var blocked = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = script, Options = confirmed });
        Assert.Equal(GuardReason.Destructive, blocked.Blocked?.Reason);
        Assert.Contains(blocked.Blocked!.Findings, f => f.Rule == "drop");
        Assert.True(await Exists(), "the guard let the DROP run");

        await h.RunAsync(sid, script, confirmed with { ConfirmedDestructive = true });
        await h.RunAsync(sid, "SET standard_conforming_strings = on;", confirmed);
        Assert.False(await Exists(), "the server did not run the hidden DROP: the premise of the guard changed");

        async Task<bool> Exists()
        {
            var (_, b, _) = await h.RunAsync(sid, "SELECT count(*)::int AS n FROM pg_tables WHERE tablename = 'zz_scs'", confirmed);
            return ((Apache.Arrow.Int32Array)b[0].Column("n")).GetValue(0) > 0;
        }
    }

    /// <summary>
    /// Code audit 2026-11-25 (H1 / C3): Npgsql and the server split the estimated plan's text by the session's setting,
    /// so with standard_conforming_strings off EXPLAIN + the text ran the hidden UPDATE / DROP. Refused now.
    /// </summary>
    [IntegrationFact]
    public async Task An_estimated_plan_does_not_run_a_drop_hidden_by_non_standard_strings()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        var options = new ExecuteOptions { ConfirmedDestructive = true };
        await h.RunAsync(sid, "DROP TABLE IF EXISTS zz_scs_plan; CREATE TABLE zz_scs_plan (id int);", options);

        // A transaction pins the connection, so the plan would run where the setting was changed.
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await h.RunAsync(sid, "SET standard_conforming_strings = off;", options);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanGet(new PlanParams
        {
            SessionId = sid, Sql = "SELECT 'a\\'' ; DROP TABLE zz_scs_plan; -- '", Mode = PlanMode.Estimated,
        }));
        Assert.Equal("unsupported", ex.Code);
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
        var (_, b, _) = await h.RunAsync(sid, "SELECT count(*)::int AS n FROM pg_tables WHERE tablename = 'zz_scs_plan'", options);
        Assert.True(((Apache.Arrow.Int32Array)b[0].Column("n")).GetValue(0) > 0, "the estimated plan ran the hidden DROP");
        await h.RunAsync(sid, "DROP TABLE IF EXISTS zz_scs_plan;", options);
    }

    /// <summary>The prefix bypass (probe 2026-11-25): neither uniform reading saw the DROP after the SET; the SET is the finding.</summary>
    [IntegrationFact]
    public async Task A_drop_after_a_change_of_standard_conforming_strings_behind_a_shifting_prefix_is_stopped_on_production()
    {
        await using var h = new TestHost();
        var (dev, _) = await h.OpenPgAsync();
        var prod = await h.Rpc.ProfileSave(dev with { Id = "", Name = "Fixture PG prod", Environment = EnvironmentKind.Prod });
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = prod.Id, Secret = TestHost.PgPassword, Database = "postgres" })).SessionId;
        var confirmed = new ExecuteOptions { ConfirmedProd = true };
        await h.RunAsync(sid, "DROP TABLE IF EXISTS zz_scs2; CREATE TABLE zz_scs2 (id int);", confirmed with { ConfirmedDestructive = true });

        var script = """SELECT 'p\', ' /*'; SET standard_conforming_strings = off;""" + "\n" + """SELECT 'a\'' ; DROP TABLE zz_scs2; -- ';""";
        var blocked = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = script, Options = confirmed });
        Assert.Equal(GuardReason.Destructive, blocked.Blocked?.Reason);
        Assert.Contains(blocked.Blocked!.Findings, f => f.Rule == "readingChanged");
        Assert.True(await Exists(), "the guard let the DROP run");

        await h.RunAsync(sid, script, confirmed with { ConfirmedDestructive = true });
        await h.RunAsync(sid, "SET standard_conforming_strings = on;", confirmed);
        Assert.False(await Exists(), "the server did not run the hidden DROP: the premise of the guard changed");

        async Task<bool> Exists()
        {
            var (_, b, _) = await h.RunAsync(sid, "SELECT count(*)::int AS n FROM pg_tables WHERE tablename = 'zz_scs2'", confirmed);
            return ((Apache.Arrow.Int32Array)b[0].Column("n")).GetValue(0) > 0;
        }
    }
}
