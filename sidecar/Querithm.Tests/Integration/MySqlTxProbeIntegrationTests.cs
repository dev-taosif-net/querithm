using MySqlConnector;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// M9 / 8a review against the Docker fixture (QUERITHM_IT=1), MySQL 8.4: with the events_transactions_current consumer
/// turned off, performance_schema records no transaction for a connection, so the state comes from the savepoint probe —
/// and an open transaction must read as open, never as none (the connection would go back to the pool with it). The
/// consumer is a global setting: other MySQL tests running meanwhile take the probe path too, which is just as correct.
/// </summary>
[Collection("mysql-consumers")]
public sealed class MySqlTxProbeIntegrationTests
{
    static async Task SetConsumer(bool on)
    {
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={MySqlIntegrationTests.Port("mysql")};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE performance_schema.setup_consumers SET ENABLED = '{(on ? "YES" : "NO")}' WHERE NAME = 'events_transactions_current'";
        await cmd.ExecuteNonQueryAsync();
    }

    [IntegrationFact]
    public async Task Without_the_transaction_consumer_the_savepoint_probe_tells_open_from_none()
    {
        await SetConsumer(false);
        try
        {
            var (h, _, sid) = await MySqlIntegrationTests.OpenAsync("mysql");
            await using var _ = h;
            async Task<TxState> State() => (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState;

            Assert.Null((await MySqlIntegrationTests.Run(h, sid, "SELECT 1 AS one")).Done.Error);
            Assert.Equal(TxState.None, await State());

            // Typed in the editor: read on the connection after the run, so the tab pins it.
            Assert.Null((await MySqlIntegrationTests.Run(h, sid, "START TRANSACTION")).Done.Error);
            Assert.Equal(TxState.Open, await State());
            Assert.True(h.Services.Sessions.Get(sid).Pinned != null);
            Assert.Null((await MySqlIntegrationTests.Run(h, sid, "COMMIT")).Done.Error);
            Assert.Equal(TxState.None, await State());

            // Through the toolbar, with the state read after each.
            Assert.Equal(TxState.Open, (await h.Rpc.TxBegin(new SessionRef { SessionId = sid })).TxState);
            Assert.Null((await MySqlIntegrationTests.Run(h, sid, "SELECT 2 AS two")).Done.Error);
            Assert.Equal(TxState.Open, await State());
            Assert.Equal(TxState.None, (await h.Rpc.TxRollback(new SessionRef { SessionId = sid })).TxState);
        }
        finally { await SetConsumer(true); }
    }
}
