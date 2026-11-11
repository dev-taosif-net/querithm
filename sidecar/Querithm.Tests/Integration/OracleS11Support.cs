using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;

namespace Querithm.Tests.Integration;

/// <summary>D4 S11: a sidecar with Oracle enabled and a session on the fixture's app user, its catalog refreshed.</summary>
internal sealed class OracleS11Host : IAsyncDisposable
{
    public CapturingNotifier Notifier { get; } = new();
    public CapturingFrames Frames { get; } = new();
    public Services Services { get; }
    public RpcServer Rpc { get; }
    public string Dir { get; } = Directory.CreateTempSubdirectory("qora-s11h-").FullName;
    public string Sid { get; private set; } = "";
    public string ProfileId { get; private set; } = "";
    public static string Db => OracleIntegrationTests.Service;

    OracleS11Host()
    {
        Services = Services.Create(LocalStore.InMemory(), Frames, Notifier, NullLoggerFactory.Instance,
            new ProviderRegistry(EngineSetup.Providers(Dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle")), Dir);
        Rpc = new RpcServer(Services, () => { });
    }

    public static async Task<OracleS11Host> OpenAsync(bool refresh = true)
    {
        var h = new OracleS11Host();
        var p = await h.Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "ora-s11", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password,
            Username = OracleIntegrationTests.AppUser, DatabaseDefault = Db, TlsMode = TlsMode.Disable, TimeoutS = 30,
        });
        h.ProfileId = p.Id;
        h.Sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = FixtureSecrets.Password("oracle") })).SessionId;
        if (refresh) await h.RefreshAsync();
        return h;
    }

    public async Task RefreshAsync()
    {
        await Rpc.MetaRefresh(new RefreshParams { ProfileId = ProfileId, Database = Db, SessionId = Sid, Full = true });
        await Task.Delay(100);
        await Services.Metadata.WaitAsync(ProfileId, Db);
    }

    public async Task<QueryDoneEvent> RunAsync(string sql)
    {
        var r = await Rpc.QueryExecute(new QueryExecuteParams { SessionId = Sid, Sql = sql });
        Assert.Null(r.Blocked);
        return await Notifier.WaitDone(r.ExecutionId!, 300_000);
    }

    public List<ServerMessage> Messages(string executionId) => Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == executionId).ToList();

    /// <summary>Runs a script through the sidecar and fails with the server's messages and the script when it does not finish.</summary>
    public async Task RunOkAsync(string sql)
    {
        var r = await Rpc.QueryExecute(new QueryExecuteParams { SessionId = Sid, Sql = sql });
        Assert.Null(r.Blocked);
        var done = await Notifier.WaitDone(r.ExecutionId!, 300_000);
        if (done.State != ExecState.Done)
            Assert.Fail($"{done.State}: {string.Join("\n", Messages(r.ExecutionId!).Select(m => m.Message))}\n\n{sql}");
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
    }
}
