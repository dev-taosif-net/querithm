using Querithm.Contract;
using Querithm.Core;
using Querithm.Intelligence.PgSql;
using Querithm.Tests.Fakes;

namespace Querithm.Tests.Security;

/// <summary>
/// L2: deleting a profile ends what it had open in the sidecar — its sessions, secrets, pools — or, while a tab still has
/// an open or unknown transaction or a running query, refuses (profileBusy) and changes nothing, so the UI can ask first.
/// </summary>
public class ProfileDeleteTests
{
    static async Task<(TestHostWithFake Host, Profile Profile)> Rig(Engine engine)
    {
        var host = new TestHostWithFake();
        if (engine == Engine.Pg) host.Provider.As(Engine.Pg, new PgLanguageService(), transactions: new FakePgTransactions());
        var p = await host.Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "doomed", Engine = engine, Host = "db.internal", Auth = engine == Engine.Pg ? AuthKind.Password : AuthKind.Sql, Username = "u",
        });
        return (host, p);
    }

    static async Task<string> Open(TestHostWithFake host, Profile p)
    {
        var s = await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "pw" });
        await host.Rpc.SessionStateGet(new SessionRef { SessionId = s.SessionId });
        return s.SessionId;
    }

    [Theory]
    [MemberData(nameof(TestEngines.All), MemberType = typeof(TestEngines))]
    public async Task Deleting_an_idle_profile_closes_its_sessions_and_forgets_its_secret(Engine engine)
    {
        var (host, p) = await Rig(engine);
        await using var _ = host;
        var a = await Open(host, p);
        var b = await Open(host, p);
        await host.Rpc.ProfileDelete(new ProfileRef { ProfileId = p.Id });
        Assert.Throws<QuerithmException>(() => host.Services.Sessions.Get(a));
        Assert.Throws<QuerithmException>(() => host.Services.Sessions.Get(b));
        Assert.DoesNotContain(host.Services.Sessions.All, x => x.Profile.Id == p.Id);
        Assert.False(host.Services.Sessions.HasCredentials(p.Id));
        Assert.Null(host.Services.Profiles.Get(p.Id));
    }

    [Theory]
    [MemberData(nameof(TestEngines.All), MemberType = typeof(TestEngines))]
    public async Task Deleting_is_refused_while_a_transaction_is_open_and_changes_nothing(Engine engine)
    {
        var (host, p) = await Rig(engine);
        await using var _ = host;
        var idle = await Open(host, p);
        var sid = await Open(host, p);
        await host.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileDelete(new ProfileRef { ProfileId = p.Id }));
        Assert.Equal("profileBusy", ex.Code);
        // All or nothing: the idle tab keeps its session too, the transaction stays open, the profile and its secret stay.
        Assert.Equal(TxState.Open, (await host.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.NotNull(host.Services.Sessions.Get(idle));
        Assert.NotNull(host.Services.Profiles.Get(p.Id));
        Assert.True(host.Services.Sessions.HasCredentials(p.Id));
    }

    [Fact]
    public async Task Deleting_is_refused_while_a_query_runs()
    {
        var (host, p) = await Rig(Engine.Mssql);
        await using var _ = host;
        var sid = await Open(host, p);
        var gate = new TaskCompletionSource();
        host.Provider.Handler = async (cmd, ct) => { if (cmd.CommandText.StartsWith("WAITFOR", StringComparison.Ordinal)) await gate.Task.WaitAsync(ct); return FakeResult.None; };
        var exec = await host.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "WAITFOR DELAY '00:01'" });
        while (host.Services.Sessions.Get(sid).ActiveExecutionId == null) await Task.Delay(5);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileDelete(new ProfileRef { ProfileId = p.Id }));
        Assert.Equal("profileBusy", ex.Code);
        Assert.NotNull(host.Services.Profiles.Get(p.Id));
        gate.SetResult();
        await host.Notifier.WaitDone(exec.ExecutionId!);
        // Once the run ended, the delete goes through.
        await host.Rpc.ProfileDelete(new ProfileRef { ProfileId = p.Id });
        Assert.Null(host.Services.Profiles.Get(p.Id));
    }

    // Regression (CI flake): the done event can reach the caller just before the run releases the session's lock; a delete
    // in that window was refused as busy. The release is simulated: the lock is held, then let go 100 ms later.
    [Theory]
    [MemberData(nameof(TestEngines.All), MemberType = typeof(TestEngines))]
    public async Task Deleting_right_after_a_run_ends_waits_for_the_run_to_let_go_of_the_session(Engine engine)
    {
        var (host, p) = await Rig(engine);
        await using var _ = host;
        var sid = await Open(host, p);
        var session = host.Services.Sessions.Get(sid);
        await session.ExecLock.WaitAsync();
        var release = Task.Run(async () => { await Task.Delay(100); session.ExecLock.Release(); });
        await host.Rpc.ProfileDelete(new ProfileRef { ProfileId = p.Id });
        await release;
        Assert.Null(host.Services.Profiles.Get(p.Id));
        Assert.Throws<QuerithmException>(() => host.Services.Sessions.Get(sid));
    }
}
