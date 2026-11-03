using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Sessions;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests.Integration;

/// <summary>The per-profile connection pool against real servers (max connections, queueing, staleness, retirement). Enable with QUERITHM_IT=1.</summary>
public class PoolIntegrationTests
{
    public abstract class Base(string engine)
    {
        IDbProvider Provider => engine == "pg" ? new PostgresProvider() : new SqlServerProvider();
        string Secret => engine == "pg" ? TestHost.PgPassword : TestHost.MssqlPassword;
        string Database => engine == "pg" ? "postgres" : "master";

        Profile Profile => engine == "pg"
            ? new Profile { Id = "pool-pg", Name = "pool", Engine = Engine.Pg, Host = "localhost", Port = 54329, Auth = AuthKind.Password, Username = "postgres", TlsMode = TlsMode.Prefer, DatabaseDefault = "postgres" }
            : new Profile { Id = "pool-ms", Name = "pool", Engine = Engine.Mssql, Host = TestHost.MssqlHost, Port = 14333, Auth = AuthKind.Sql, Username = "sa", TlsMode = TlsMode.Mandatory, TrustCert = true, DatabaseDefault = "master" };

        Pool NewPool(int max, Func<System.Data.Common.DbConnection, bool>? alive = null)
        {
            var provider = Provider;
            var profile = Profile;
            return new Pool("k", profile, provider, provider.DatabaseScopedConnections ? Database : null,
                (db, ct) => provider.OpenAsync(profile, Secret, db, null, ct), max, NullLogger.Instance, alive);
        }

        [IntegrationFact]
        public async Task Connections_are_reused_and_the_pool_never_exceeds_its_maximum()
        {
            var pool = NewPool(2);
            var a = await pool.BorrowAsync(Database, null, CancellationToken.None);
            var b = await pool.BorrowAsync(Database, null, CancellationToken.None);
            Assert.Equal(2, pool.Total);
            Assert.False(string.IsNullOrEmpty(pool.ServerVersion));
            var queued = 0;
            var third = pool.BorrowAsync(Database, () => Interlocked.Increment(ref queued), CancellationToken.None);
            await Task.Delay(300);
            Assert.False(third.IsCompleted);
            Assert.Equal(1, queued);
            pool.Return(a);
            var c = await third.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(a, c);   // handed straight to the waiter, not through the idle list
            Assert.Equal(2, pool.Total);
            pool.Return(b);
            pool.Return(c);
            Assert.Equal(2, pool.IdleCount);
            await pool.CloseAllAsync();
            Assert.Equal(0, pool.Total);
        }

        [IntegrationFact]
        public async Task A_waiter_can_be_cancelled_and_does_not_steal_the_next_returned_connection()
        {
            var pool = NewPool(1);
            var held = await pool.BorrowAsync(Database, null, CancellationToken.None);
            using var cts = new CancellationTokenSource();
            var waiting = pool.BorrowAsync(Database, null, cts.Token);
            await Task.Delay(200);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            pool.Return(held);
            Assert.Equal(1, pool.IdleCount);
            var again = await pool.BorrowAsync(Database, null, CancellationToken.None);
            Assert.Same(held, again);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.BorrowAsync(Database, null, new CancellationToken(true)));
            pool.Return(again);
            await pool.CloseAllAsync();
        }

        [IntegrationFact]
        public async Task Discarding_a_connection_frees_its_slot_for_a_waiter_that_then_opens_a_new_one()
        {
            var pool = NewPool(1);
            var first = await pool.BorrowAsync(Database, null, CancellationToken.None);
            var waiting = pool.BorrowAsync(Database, null, CancellationToken.None);
            await Task.Delay(200);
            await pool.DiscardAsync(first);
            var second = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotSame(first, second);
            Assert.True(second.IsOpen);
            Assert.Equal(1, pool.Total);
            pool.Return(second);
            await pool.CloseAllAsync();
        }

        [IntegrationFact]
        public async Task Connections_idle_for_over_thirty_seconds_are_validated_and_dead_ones_replaced()
        {
            var pool = NewPool(2);
            var conn = await pool.BorrowAsync(Database, null, CancellationToken.None);
            pool.Return(conn);
            conn.LastUsedMs = Querithm.Core.Time.NowMs() - 60_000;
            var healthy = await pool.BorrowAsync(Database, null, CancellationToken.None);
            Assert.Same(conn, healthy);   // validation passed, same connection
            pool.Return(healthy);

            // Kill the session server-side: the next validation fails and the pool opens a fresh connection.
            await using (var killer = await Provider.OpenAsync(Profile, Secret, Database, null, CancellationToken.None))
            {
                await using var cmd = killer.CreateCommand();
                cmd.CommandText = engine == "pg"
                    ? $"SELECT pg_terminate_backend({await Pid(healthy)})"
                    : $"KILL {await Pid(healthy)}";
                await cmd.ExecuteNonQueryAsync();
            }
            healthy.LastUsedMs = Querithm.Core.Time.NowMs() - 60_000;
            var replacement = await pool.BorrowAsync(Database, null, CancellationToken.None);
            // SQL Server's driver reconnects an idle connection by itself (connection resiliency), so the same one may come back.
            if (engine == "pg") Assert.NotSame(healthy, replacement);
            Assert.True(replacement.IsOpen);
            Assert.Equal(1, await Scalar(replacement));
            pool.Return(replacement);
            await pool.CloseAllAsync();
        }

        static async Task<int> Scalar(PhysicalConnection c)
        {
            await using var cmd = c.Db.CreateCommand();
            cmd.CommandText = "SELECT 1";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        async Task<int> Pid(PhysicalConnection c)
        {
            await using var cmd = c.Db.CreateCommand();
            cmd.CommandText = engine == "pg" ? "SELECT pg_backend_pid()" : "SELECT @@SPID";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        [IntegrationFact]
        public async Task A_connection_whose_transport_is_gone_is_dropped_instead_of_handed_out()
        {
            var alive = true;
            var pool = NewPool(2, _ => alive);
            var conn = await pool.BorrowAsync(Database, null, CancellationToken.None);
            pool.Return(conn);
            alive = false;
            var created = await Record.ExceptionAsync(async () => { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await pool.BorrowAsync(Database, null, cts.Token); });
            Assert.NotNull(created);   // every connection looks dead, so the borrow keeps retrying until cancelled
            alive = true;
            await pool.CloseAllAsync();
        }

        [IntegrationFact]
        public async Task Sweeping_closes_only_connections_idle_longer_than_asked()
        {
            var pool = NewPool(3);
            var old = await pool.BorrowAsync(Database, null, CancellationToken.None);
            var fresh = await pool.BorrowAsync(Database, null, CancellationToken.None);
            pool.Return(old);
            pool.Return(fresh);
            old.LastUsedMs = Querithm.Core.Time.NowMs() - 10 * 60_000;
            await pool.SweepAsync(5 * 60_000);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, pool.Total);
            Assert.False(old.IsOpen);
            await pool.CloseAllAsync();
        }

        [IntegrationFact]
        public async Task A_retired_pool_closes_connections_that_come_back()
        {
            var pool = NewPool(2);
            var busy = await pool.BorrowAsync(Database, null, CancellationToken.None);
            await pool.CloseAllAsync();
            Assert.True(pool.Retired);
            pool.Return(busy);
            await Task.Delay(300);
            Assert.False(busy.IsOpen);
            Assert.Equal(0, pool.Total);
        }

        [IntegrationFact]
        public async Task A_failed_open_gives_the_slot_back()
        {
            var provider = Provider;
            var pool = new Pool("k", Profile, provider, null, (db, ct) => throw new InvalidOperationException("no route to host"), 1, NullLogger.Instance);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.BorrowAsync(null, null, CancellationToken.None));
            Assert.Equal("no route to host", ex.Message);
            Assert.Equal(0, pool.Total);
            var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.BorrowAsync(null, null, CancellationToken.None));   // not stuck waiting for the slot
            Assert.Equal("no route to host", ex2.Message);
        }
    }

    [Collection("mssql")]
    public class SqlServer() : Base("mssql");

    [Collection("pg")]
    public class Postgres() : Base("pg");
}
