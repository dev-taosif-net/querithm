using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Sqlite;
using Querithm.Tests.FileEngines;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 7, the 3-level Prod confirmations setting (§16, 2026-11-18 / 2026-11-27): Settings › Query execution ›
/// "Production confirmations" defaults to <see cref="ProdGuardMode.Risky"/> and is enforced by the sidecar.
/// <list type="bullet">
/// <item><see cref="ProdGuardMode.Risky"/> (default): harmless reads run without a prompt; destructive statements and
/// Querithm-generated writes ask once.</item>
/// <item><see cref="ProdGuardMode.Strict"/>: also asks before the first non-destructive query on each Prod database;
/// destructive first runs still ask once (directly <see cref="GuardReason.Destructive"/>, never two modals).</item>
/// <item><see cref="ProdGuardMode.Off"/>: runs on Prod without confirmations; policy refusals still hold.</item>
/// </list>
/// The value lives in store.db under <see cref="QueryGuard.SettingKey"/>; an unreadable or unrecognised stored value
/// fails closed to <see cref="ProdGuardMode.Strict"/>.
/// </summary>
public sealed class ProdConfirmationsSettingTests
{
    static async Task<(SqliteHost H, string Sid)> SqliteProd()
    {
        var h = new SqliteHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v); INSERT INTO t(v) VALUES (1),(2),(3);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite, EnvironmentKind.Prod);
        return (h, sid);
    }

    static Task Set(SqliteHost h, string json) => h.Rpc.SettingsSet(new SettingEntry { Key = QueryGuard.SettingKey, ValueJson = json });

    static async Task<long> Count(SqliteHost h, string sid)
    {
        var (_, batches, _) = await h.RunAsync(sid, "SELECT count(*) FROM t", new ExecuteOptions { ConfirmedProd = true });
        return batches[0].Column(0) switch
        {
            Apache.Arrow.Int64Array i => i.GetValue(0)!.Value,
            Apache.Arrow.StringArray t => long.Parse(t.GetString(0)!, System.Globalization.CultureInfo.InvariantCulture),
            var a => throw new InvalidOperationException(a.GetType().Name),
        };
    }

    [Fact]
    public async Task Risky_by_default_runs_safe_reads_without_a_prompt_and_guards_destructive_statements_in_one_step()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        Assert.Equal(ProdGuardMode.Risky, h.Services.Executions.Guard.ProdMode);

        // Safe SELECT runs immediately with no ProdFirstRun modal.
        var read = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT count(*) FROM t" });
        Assert.Null(read.Blocked);
        await h.Notifier.WaitDone(read.ExecutionId!);

        // Destructive statement asks directly with Destructive (no ProdFirstRun beforehand), and confirmedProd alone does not bypass it.
        var del = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t" });
        Assert.Equal(GuardReason.Destructive, del.Blocked?.Reason);
        var stillBlocked = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t", Options = new ExecuteOptions { ConfirmedProd = true } });
        Assert.Equal(GuardReason.Destructive, stillBlocked.Blocked?.Reason);
        Assert.Equal(3, await Count(h, sid));
    }

    [Fact]
    public async Task Strict_asks_before_the_first_read_and_asks_once_for_a_destructive_first_run()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        await Set(h, "\"strict\"");
        Assert.Equal(ProdGuardMode.Strict, h.Services.Executions.Guard.ProdMode);

        // First read asks ProdFirstRun; confirmedDestructive alone cannot satisfy it on a non-destructive query.
        var first = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Equal(GuardReason.ProdFirstRun, first.Blocked?.Reason);
        var spoofed = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1", Options = new ExecuteOptions { ConfirmedDestructive = true } });
        Assert.Equal(GuardReason.ProdFirstRun, spoofed.Blocked?.Reason);

        // A destructive first run goes straight to Destructive (one confirmation, not ProdFirstRun followed by Destructive).
        var del = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t" });
        Assert.Equal(GuardReason.Destructive, del.Blocked?.Reason);
        var confirmed = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t", Options = new ExecuteOptions { ConfirmedDestructive = true } });
        Assert.Null(confirmed.Blocked);
        await h.Notifier.WaitDone(confirmed.ExecutionId!);
        Assert.Equal(0, await Count(h, sid));

        // After the destructive run was confirmed, subsequent reads on the same Prod target need no ProdFirstRun modal.
        var nextRead = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Null(nextRead.Blocked);
        await h.Notifier.WaitDone(nextRead.ExecutionId!);
    }

    [Fact]
    public async Task Off_prod_runs_without_any_confirmation_even_a_destructive_statement()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        await Set(h, "\"off\"");
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t WHERE id = 1" });
        Assert.Null(r.Blocked);
        await h.Notifier.WaitDone(r.ExecutionId!);
        var destructive = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t" });
        Assert.Null(destructive.Blocked);
        await h.Notifier.WaitDone(destructive.ExecutionId!);
        Assert.Equal(0, await Count(h, sid));
    }

    [Fact]
    public async Task Tool_writes_ask_once_combining_destructive_findings_and_off_skips_them()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        var session = h.Services.Sessions.Get(sid);
        var write = new DestructiveFinding { Rule = "gridEdit", Statement = "Grid changes", Line = 0, Column = 0, Description = "Writes the staged grid changes" };
        var del = new DestructiveFinding { Rule = "delete", Statement = "DELETE", Line = 0, Column = 0, Description = "Deletes 1 row(s)" };

        // Non-destructive write asks ProdWrite.
        Assert.Equal(GuardReason.ProdWrite, h.Services.Executions.ToolGuard(session, false, false, [write], [])?.Reason);
        Assert.Null(h.Services.Executions.ToolGuard(session, true, false, [write], []));

        // Destructive write asks once with Destructive carrying both write summary and delete finding.
        var combined = h.Services.Executions.ToolGuard(session, false, false, [write], [del]);
        Assert.Equal(GuardReason.Destructive, combined?.Reason);
        Assert.Equal(["gridEdit", "delete"], combined!.Findings.Select(f => f.Rule));
        // confirmedProd alone never bypasses destructive tool writes.
        Assert.Equal(GuardReason.Destructive, h.Services.Executions.ToolGuard(session, true, false, [write], [del])?.Reason);
        Assert.Null(h.Services.Executions.ToolGuard(session, false, true, [write], [del]));

        await Set(h, "\"off\"");
        Assert.Null(h.Services.Executions.ToolGuard(session, false, false, [write], [del]));
    }

    [Fact]
    public async Task Off_refusals_still_hold()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        await Set(h, "\"off\"");
        var e = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1; ATTACH 'x.db' AS x" }));
        Assert.Equal("refused", e.Code);
    }

    [Fact]
    public async Task Switching_modes_and_legacy_booleans_take_effect_immediately()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        await Set(h, "false");
        Assert.Equal(ProdGuardMode.Off, h.Services.Executions.Guard.ProdMode);
        Assert.Equal("\"off\"", Assert.Single(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = [QueryGuard.SettingKey] })).ValueJson);

        await Set(h, "true");
        Assert.Equal(ProdGuardMode.Strict, h.Services.Executions.Guard.ProdMode);
        Assert.Equal("\"strict\"", Assert.Single(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = [QueryGuard.SettingKey] })).ValueJson);
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Equal(GuardReason.ProdFirstRun, r.Blocked?.Reason);

        await Set(h, "\"risky\"");
        Assert.Equal(ProdGuardMode.Risky, h.Services.Executions.Guard.ProdMode);
        var read = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Null(read.Blocked);
        await h.Notifier.WaitDone(read.ExecutionId!);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"false\"")]
    [InlineData("\"bogus\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task An_unrecognised_value_is_refused_and_changes_nothing(string json)
    {
        var (h, _) = await SqliteProd();
        await using var _h = h;
        await Set(h, "\"off\"");
        var e = await Assert.ThrowsAsync<QuerithmException>(() => Set(h, json));
        Assert.Equal("invalid", e.Code);
        Assert.Equal(ProdGuardMode.Off, h.Services.Executions.Guard.ProdMode);
    }

    /// <summary>
    /// settings.patch merges members of an object setting; the guard mode is a scalar that settings.set checks and
    /// applies to the guard, so a patch of its key is refused and neither the stored value nor the guard moves.
    /// </summary>
    [Fact]
    public async Task A_settings_patch_of_the_guard_mode_is_refused_and_changes_nothing()
    {
        var (h, sid) = await SqliteProd();
        await using var _h = h;
        await Set(h, "\"off\"");
        var e = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SettingsPatch(new SettingsPatchParams
        {
            Key = QueryGuard.SettingKey, Set = new Dictionary<string, string> { ["on"] = "true" }, SetWhenNew = new Dictionary<string, string> { ["on"] = "true" },
        }));
        Assert.Equal("invalid", e.Code);
        Assert.Equal("\"off\"", Assert.Single(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = [QueryGuard.SettingKey] })).ValueJson);
        Assert.Equal(ProdGuardMode.Off, h.Services.Executions.Guard.ProdMode);
        Assert.Equal(3, await Count(h, sid));
    }

    static Services Start(LocalStore store) =>
        Services.Create(store, new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, new ProviderRegistry([new SqliteProvider()]));

    [Theory]
    [InlineData("\"strict\"", ProdGuardMode.Strict)]
    [InlineData("\"risky\"", ProdGuardMode.Risky)]
    [InlineData("\"off\"", ProdGuardMode.Off)]
    [InlineData("true", ProdGuardMode.Strict)]
    [InlineData("false", ProdGuardMode.Off)]
    [InlineData("\"bogus\"", ProdGuardMode.Strict)]
    [InlineData("0", ProdGuardMode.Strict)]
    public async Task The_sidecar_reads_the_stored_value_at_start_and_unknown_values_fail_closed_to_strict(string stored, ProdGuardMode expected)
    {
        var store = LocalStore.InMemory();
        new SettingsStore(store).Set(QueryGuard.SettingKey, stored);
        await using var services = Start(store);
        Assert.Equal(expected, services.Executions.Guard.ProdMode);
    }

    [Fact]
    public async Task Switching_from_risky_to_strict_after_a_select_still_asks_for_first_run_confirmation()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        // Unprompted SELECT in default Risky mode must not silently mark this Prod target as confirmed.
        var read = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Null(read.Blocked);
        await h.Notifier.WaitDone(read.ExecutionId!);

        await Set(h, "\"strict\"");
        var afterSwitch = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Equal(GuardReason.ProdFirstRun, afterSwitch.Blocked?.Reason);
    }

    [Fact]
    public async Task Concurrent_guard_mode_writes_keep_store_and_in_memory_guard_in_sync_and_reject_missing_key()
    {
        var (h, _) = await SqliteProd();
        await using var _ = h;
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SettingsSet(null!))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SettingsSet(new SettingEntry { Key = "", ValueJson = "true" }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SettingsSet(new SettingEntry { Key = new string('k', 200), ValueJson = "true" }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SettingsSet(new SettingEntry { Key = "ui.other", ValueJson = null! }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SettingsSet(new SettingEntry { Key = "ui.other", ValueJson = "{bad" }))).Code);

        var values = new[] { "\"strict\"", "\"off\"", "\"risky\"", "\"strict\"", "\"off\"", "\"risky\"" };
        await Task.WhenAll(values.Select(v => Task.Run(() => Set(h, v))));
        var stored = Assert.Single(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = [QueryGuard.SettingKey] })).ValueJson;
        Assert.Equal(QueryGuard.ParseSetting(stored), h.Services.Executions.Guard.ProdMode);
    }

    [Fact]
    public async Task With_nothing_stored_it_defaults_to_risky()
    {
        await using var services = Start(LocalStore.InMemory());
        Assert.Equal(ProdGuardMode.Risky, services.Executions.Guard.ProdMode);
    }
}
