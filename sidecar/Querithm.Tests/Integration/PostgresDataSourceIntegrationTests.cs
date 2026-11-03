using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// 8a review against the Docker fixture (QUERITHM_IT=1): forgetting a profile's Npgsql data sources (a changed setting,
/// a disconnect elsewhere) while its connections are open leaves them working — the source is disposed only once the
/// last one closes — and the next open builds a fresh source.
/// </summary>
public sealed class PostgresDataSourceIntegrationTests
{
    [IntegrationFact]
    public async Task Forgetting_the_data_sources_while_connections_are_open_leaves_them_working()
    {
        await using var h = new TestHost();
        var (profile, sid) = await h.OpenPgAsync();
        Assert.Equal(ExecState.Done, (await h.RunAsync(sid, "SELECT 1 AS one")).Done.State); // an idle pooled connection now
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid }); // and pinned

        var provider = h.Services.Sessions.Get(sid).Provider;
        var source = Querithm.Providers.Postgres.PostgresProvider.DataSourceFor(profile.Id, provider.BuildConnectionString(profile, TestHost.PgPassword, "postgres"));
        provider.ForgetConnections(profile.Id);

        var (inTx, _, id) = await h.RunAsync(sid, "SELECT 2 AS two");
        Assert.True(inTx.State == ExecState.Done, string.Join("\n", h.Messages(id)) + inTx.Error?.Message);
        Assert.Equal(TxState.None, (await h.Rpc.TxRollback(new SessionRef { SessionId = sid })).TxState);
        // Back in the pool, still from the forgotten source: it keeps working until it closes.
        var (pooled, _, id2) = await h.RunAsync(sid, "SELECT 3 AS three");
        Assert.True(pooled.State == ExecState.Done, string.Join("\n", h.Messages(id2)) + pooled.Error?.Message);
        await h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        // Once its pooled connection closes too, the forgotten source is disposed (its last lease went with it).
        h.Services.Sessions.ForgetPools(profile.Id);
        var end = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { await using var probe = await source.OpenConnectionAsync(); }
            catch (ObjectDisposedException) { break; }
            Assert.True(DateTime.UtcNow < end, "the forgotten data source was never disposed after its connections closed");
            await Task.Delay(50);
        }

        // A new session opens through a fresh source.
        var again = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = profile.Id, Secret = TestHost.PgPassword });
        var (fresh, _, id3) = await h.RunAsync(again.SessionId, "SELECT 4 AS four");
        Assert.True(fresh.State == ExecState.Done, string.Join("\n", h.Messages(id3)) + fresh.Error?.Message);
    }
}
