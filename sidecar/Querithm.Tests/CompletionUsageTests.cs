using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Store;
using Querithm.Tests.Security;

namespace Querithm.Tests;

/// <summary>Completion usage ranking (§9): accepted items per database in store.db, bounded, and gone with their profile.</summary>
public class CompletionUsageTests
{
    [Fact]
    public void Counts_uses_keeps_the_last_use_and_lists_most_recent_first()
    {
        var store = LocalStore.InMemory();
        var usage = new CompletionUsageStore(store);
        var t = 1000L;
        usage.Now = () => t;
        usage.Record("p", "db", ["orders", "customers"]);
        t = 2000;
        usage.Record("p", "db", ["orders"]);
        usage.Record("p", "other", ["x"]);
        var list = usage.List("p", "db");
        Assert.Equal(["orders", "customers"], list.Select(u => u.Item));
        Assert.Equal(2, list[0].Uses);
        Assert.Equal(2000, list[0].LastUsedAt);
        Assert.Equal(1, list[1].Uses);
        Assert.Equal(["x"], usage.List("p", "other").Select(u => u.Item));
    }

    [Fact]
    public void Keeps_at_most_the_bound_dropping_the_least_recently_used()
    {
        var usage = new CompletionUsageStore(LocalStore.InMemory());
        var t = 0L;
        usage.Now = () => ++t;
        for (var i = 0; i < CompletionUsageStore.MaxPerDatabase + 5; i++) usage.Record("p", "db", [$"t{i}"]);
        var list = usage.List("p", "db");
        Assert.Equal(CompletionUsageStore.MaxPerDatabase, list.Length);
        Assert.DoesNotContain(list, u => u.Item == "t0");
    }

    [Fact]
    public async Task Deleting_the_profile_deletes_its_usage_and_the_rpc_refuses_bad_input()
    {
        await using var host = new TestHostWithFake();
        var profile = host.Services.Profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        await host.Rpc.LangUsageRecord(new UsageRecordParams { ProfileId = profile.Id, Database = "db", Items = ["orders"] });
        Assert.Single(await host.Rpc.LangUsageList(new UsageListParams { ProfileId = profile.Id, Database = "db" }));

        await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.LangUsageRecord(new UsageRecordParams { ProfileId = "nope", Database = "db", Items = ["x"] }));
        await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.LangUsageRecord(new UsageRecordParams { ProfileId = profile.Id, Database = "db", Items = [new string('x', 600)] }));
        await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.LangUsageRecord(new UsageRecordParams { ProfileId = profile.Id, Database = "db", Items = [.. Enumerable.Range(0, 65).Select(i => $"t{i}")] }));
        await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.LangUsageList(new UsageListParams { ProfileId = profile.Id, Database = "" }));

        host.Services.Profiles.Delete(profile.Id);
        Assert.Empty(host.Services.Usage.List(profile.Id, "db"));
    }
}
