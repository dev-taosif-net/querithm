using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Tests.Fakes;
using Querithm.Tests.Security;

namespace Querithm.Tests;

/// <summary>
/// 8a review: a deadline-bounded read on the user's pinned connection (meta.definition / dependencies / script / rowCounts,
/// compare models, data.rowTokens) never silently aborts the user's transaction. On PostgreSQL a cancelled or failed
/// statement aborts the block (25P02), so the read runs inside SAVEPOINT … RELEASE and is rolled back to; on every engine
/// a stopped read re-reads the state, and a transaction the server ended (SQL Server XACT_ABORT ON) is said.
/// </summary>
public class PinnedReadTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    const string Read = "SELECT definition FROM catalog";

    sealed class Catalog : IMetadataProvider
    {
        public async Task<string?> GetDefinitionAsync(System.Data.Common.DbConnection c, ObjectRef obj, CancellationToken ct)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = Read;
            return (string?)await cmd.ExecuteScalarAsync(ct);
        }

        public Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(System.Data.Common.DbConnection c, CancellationToken ct) => throw new NotSupportedException();
        public Task<MetadataBundle> GetDetailsAsync(System.Data.Common.DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(System.Data.Common.DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dependency[]> GetDependenciesAsync(System.Data.Common.DbConnection c, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public string ToEditableScript(string definition, Capabilities caps) => definition;
        public Task<RowCount[]> GetRowCountsAsync(System.Data.Common.DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>The real RPC surface with a 300 ms deadline over a fake server whose catalog read does <paramref name="read"/>.</summary>
    static async Task<(TestHostWithFake Host, string Sid)> Host(bool pg, Func<FakeDbCommand, CancellationToken, Task<FakeResult>> read)
    {
        var h = new TestHostWithFake(TimeSpan.FromMilliseconds(300));
        if (pg) h.Provider.Transactions = new FakePgTransactions();
        h.Provider.CloseAbortsCommand = false;
        h.Provider.MetadataImpl = new Catalog();
        h.Provider.Handler = (cmd, ct) => cmd.CommandText == Read ? read(cmd, ct) : Task.FromResult(FakeResult.None);
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "srv", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" })).SessionId;
        return (h, sid);
    }

    static Task<DefinitionResult> Definition(TestHostWithFake h, string sid) =>
        h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "public", Name = "v", Kind = ObjectKind.View } }).WaitAsync(Wait);

    static List<string> Log(TestHostWithFake h, string sid) => [.. ((FakeDbConnection)h.Services.Sessions.Get(sid).Pinned!.Db).Log];

    static async Task<FakeResult> UntilCancelled(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return FakeResult.None;
    }

    [Fact]
    public async Task A_read_stopped_by_its_deadline_inside_an_open_pg_transaction_is_rolled_back_to_its_savepoint_and_the_transaction_stays()
    {
        var (h, sid) = await Host(pg: true, (_, ct) => UntilCancelled(ct));
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Definition(h, sid));
        Assert.Equal("timeout", ex.Code);

        var log = Log(h, sid);
        var at = log.IndexOf($"SAVEPOINT {SessionManager.ReadSavepoint}");
        Assert.True(at >= 0, string.Join(" | ", log));
        Assert.Equal([$"SAVEPOINT {SessionManager.ReadSavepoint}", Read, $"ROLLBACK TO SAVEPOINT {SessionManager.ReadSavepoint}", $"RELEASE SAVEPOINT {SessionManager.ReadSavepoint}"], log.Skip(at).Take(4));
        Assert.Contains("SELECT pg_current_xact_id_if_assigned()", log.Skip(at + 4)); // the state was read again ...
        var state = await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid });
        Assert.Equal(TxState.Open, state.TxState); // ... and the user's transaction is still there
        Assert.Contains(h.Notifier.Of<SessionState>(), s => s.SessionId == sid && s.TxState == TxState.Open);
    }

    [Fact]
    public async Task A_read_inside_an_open_pg_transaction_runs_inside_a_released_savepoint()
    {
        var (h, sid) = await Host(pg: true, (_, _) => Task.FromResult(FakeResult.Value("CREATE VIEW v AS SELECT 1")));
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });

        Assert.Equal("CREATE VIEW v AS SELECT 1", (await Definition(h, sid)).Sql);
        var log = Log(h, sid);
        var at = log.IndexOf($"SAVEPOINT {SessionManager.ReadSavepoint}");
        Assert.Equal([$"SAVEPOINT {SessionManager.ReadSavepoint}", Read, $"RELEASE SAVEPOINT {SessionManager.ReadSavepoint}"], log.Skip(at).Take(3));
        Assert.DoesNotContain(log, l => l.StartsWith("ROLLBACK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_read_that_fails_inside_an_open_pg_transaction_is_rolled_back_to_its_savepoint_and_reports_its_error()
    {
        var (h, sid) = await Host(pg: true, (_, _) => throw new InvalidOperationException("permission denied for view v"));
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Definition(h, sid));
        Assert.Contains("permission denied", ex.Message);
        Assert.Contains($"ROLLBACK TO SAVEPOINT {SessionManager.ReadSavepoint}", Log(h, sid));
        Assert.Equal(TxState.Open, h.Services.Sessions.Get(sid).TxState);
    }

    [Fact]
    public async Task Outside_a_transaction_a_read_sets_no_savepoint()
    {
        var (h, sid) = await Host(pg: true, (_, _) => Task.FromResult(FakeResult.Value("x")));
        await using var _ = h;
        await Definition(h, sid);
        Assert.DoesNotContain(h.Provider.Opened.SelectMany(c => c.Log), l => l.Contains("SAVEPOINT"));
    }

    [Fact]
    public async Task On_sql_server_a_cancelled_read_whose_transaction_the_server_rolled_back_says_so()
    {
        // XACT_ABORT ON: the attention that cancels the read rolls the user's transaction back.
        var (h, sid) = await Host(pg: false, async (cmd, ct) =>
        {
            try { return await UntilCancelled(ct); }
            catch (OperationCanceledException) { cmd.Fake.TxDepth = 0; throw; }
        });
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var conn = (FakeDbConnection)h.Services.Sessions.Get(sid).Pinned!.Db;

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Definition(h, sid));
        Assert.Equal("txLost", ex.Code);
        Assert.Contains("has ended on the server", ex.Message);
        Assert.DoesNotContain(conn.Log, l => l.Contains("SAVEPOINT")); // a failed statement does not abort a SQL Server transaction: no guard
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Contains(h.Notifier.Of<SessionState>(), s => s.SessionId == sid && s.TxState == TxState.None);
    }

    [Fact]
    public async Task A_read_whose_connection_breaks_while_it_is_stopped_says_the_transaction_is_gone()
    {
        var (h, sid) = await Host(pg: true, async (cmd, ct) =>
        {
            try { return await UntilCancelled(ct); }
            catch (OperationCanceledException) { cmd.Fake.Break(); throw; } // Npgsql breaks a connection whose cancel is not answered
        });
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Definition(h, sid));
        Assert.Equal("txLost", ex.Code);
        Assert.Contains("connection broke", ex.Message);
        var s = h.Services.Sessions.Get(sid);
        Assert.Equal((TxState.None, (PhysicalConnection?)null), (s.TxState, s.Pinned));
    }
}
