using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// 8a review against the Docker fixture (QUERITHM_IT=1): a read Querithm runs on the tab's pinned connection and stops
/// (its deadline) leaves the user's PostgreSQL transaction usable — without the savepoint the cancel's 57014 aborted the
/// block and every later statement failed with 25P02 until a rollback.
/// </summary>
public sealed class PinnedReadIntegrationTests
{
    [IntegrationFact]
    public async Task Postgres_a_read_stopped_on_the_pinned_connection_leaves_the_users_transaction_usable()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var (made, _, _) = await h.RunAsync(sid, "CREATE TEMP TABLE pinned_read_t (x int); INSERT INTO pinned_read_t VALUES (1);");
        Assert.Null(made.Error?.Message);

        // A catalog read on the pinned connection that its deadline stops mid-statement.
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            await Assert.ThrowsAnyAsync<Exception>(() => h.Services.Objects.OnConnectionAsync(sid, null, async c =>
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT pg_sleep(30)";
                return await cmd.ExecuteScalarAsync(deadline.Token);
            }, deadline.Token));

        // A successful read on the same connection (its own savepoint, released).
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await h.Services.Objects.OnConnectionAsync(sid, null, async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1";
            return await cmd.ExecuteScalarAsync();
        }, CancellationToken.None);

        // The user's transaction is still usable: the next statement runs, and it still sees the first row.
        var (next, batches, id) = await h.RunAsync(sid, "INSERT INTO pinned_read_t VALUES (2); SELECT count(*)::int AS n FROM pinned_read_t;");
        Assert.True(next.Error == null, string.Join("\n", h.Messages(id)));
        Assert.Equal(2, ((Int32Array)batches.Single().Column(0)).GetValue(0));
        Assert.Equal(TxState.None, (await h.Rpc.TxRollback(new SessionRef { SessionId = sid })).TxState);
    }
}
