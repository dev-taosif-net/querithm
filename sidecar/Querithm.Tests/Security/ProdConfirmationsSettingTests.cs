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
/// §13 rule 7, the Prod confirmations setting (§16, 2026-11-18): Settings › Query execution › "Confirm before running on
/// production" is on by default and the sidecar enforces it. Off, a Prod target runs like any other — no first-run,
/// destructive or Querithm-generated-write confirmation — while refusals (file and configuration policy) still hold. The
/// value lives in store.db under <see cref="QueryGuard.SettingKey"/>; an unreadable or non-boolean value counts as on.
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
    public async Task On_by_default_prod_asks_for_the_first_run_and_for_destructive_statements()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        Assert.True(h.Services.Executions.Guard.ProdConfirmations);
        var first = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t" });
        Assert.Equal(GuardReason.ProdFirstRun, first.Blocked?.Reason);
        var second = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t", Options = new ExecuteOptions { ConfirmedProd = true } });
        Assert.Equal(GuardReason.Destructive, second.Blocked?.Reason);
        Assert.Equal(3, await Count(h, sid));
    }

    [Fact]
    public async Task Off_prod_runs_without_any_confirmation_even_a_destructive_statement()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        await Set(h, "false");
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t WHERE id = 1" });
        Assert.Null(r.Blocked);
        await h.Notifier.WaitDone(r.ExecutionId!);
        var destructive = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t" });
        Assert.Null(destructive.Blocked);
        await h.Notifier.WaitDone(destructive.ExecutionId!);
        Assert.Equal(0, await Count(h, sid));
    }

    [Fact]
    public async Task Off_querithm_generated_writes_on_prod_need_no_confirmation()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        var session = h.Services.Sessions.Get(sid);
        var finding = new DestructiveFinding { Rule = "delete", Statement = "DELETE", Line = 1, Column = 1, Description = "deletes rows" };
        Assert.Equal(GuardReason.ProdWrite, h.Services.Executions.ToolGuard(session, false, false, [], [finding])?.Reason);
        await Set(h, "false");
        Assert.Null(h.Services.Executions.ToolGuard(session, false, false, [], [finding]));
    }

    [Fact]
    public async Task Off_refusals_still_hold()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        await Set(h, "false");
        var e = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1; ATTACH 'x.db' AS x" }));
        Assert.Equal("refused", e.Code);
    }

    [Fact]
    public async Task Turning_it_back_on_asks_again()
    {
        var (h, sid) = await SqliteProd();
        await using var _ = h;
        await Set(h, "false");
        await Set(h, "true");
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "DELETE FROM t" });
        Assert.Equal(GuardReason.ProdFirstRun, r.Blocked?.Reason);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"false\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task A_value_that_is_not_true_or_false_is_refused_and_changes_nothing(string json)
    {
        var (h, _) = await SqliteProd();
        await using var _h = h;
        await Set(h, "false");
        var e = await Assert.ThrowsAsync<QuerithmException>(() => Set(h, json));
        Assert.Equal("invalid", e.Code);
        Assert.False(h.Services.Executions.Guard.ProdConfirmations);
    }

    /// <summary>
    /// settings.patch merges members of an object setting; the switch is a bare true / false that settings.set checks and
    /// applies to the guard, so a patch of its key is refused and neither the stored value nor the guard moves.
    /// </summary>
    [Fact]
    public async Task A_settings_patch_of_the_switch_is_refused_and_changes_nothing()
    {
        var (h, sid) = await SqliteProd();
        await using var _h = h;
        await Set(h, "false");
        var e = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SettingsPatch(new SettingsPatchParams
        {
            Key = QueryGuard.SettingKey, Set = new Dictionary<string, string> { ["on"] = "true" }, SetWhenNew = new Dictionary<string, string> { ["on"] = "true" },
        }));
        Assert.Equal("invalid", e.Code);
        Assert.Equal("false", Assert.Single(await h.Rpc.SettingsGet(new SettingsGetParams { Keys = [QueryGuard.SettingKey] })).ValueJson);
        Assert.False(h.Services.Executions.Guard.ProdConfirmations);
        Assert.Equal(3, await Count(h, sid));
    }

    static Services Start(LocalStore store) =>
        Services.Create(store, new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, new ProviderRegistry([new SqliteProvider()]));

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("\"off\"", true)]
    [InlineData("0", true)]
    public async Task The_sidecar_reads_the_stored_value_at_start_and_anything_but_false_is_on(string stored, bool expected)
    {
        var store = LocalStore.InMemory();
        new SettingsStore(store).Set(QueryGuard.SettingKey, stored);
        await using var services = Start(store);
        Assert.Equal(expected, services.Executions.Guard.ProdConfirmations);
    }

    [Fact]
    public async Task With_nothing_stored_it_is_on()
    {
        await using var services = Start(LocalStore.InMemory());
        Assert.True(services.Executions.Guard.ProdConfirmations);
    }
}
