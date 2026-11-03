using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.DuckDb;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// The real RPC surface with the DuckDB provider exposed, over temp-dir database files under the test's own app-data root.
/// File-engine integration tests need no Docker and run in plain <c>pnpm test</c> (no QUERITHM_IT gate, Phase D0g).
/// </summary>
public sealed class DuckDbHost : IAsyncDisposable
{
    public CapturingNotifier Notifier { get; } = new();
    public CapturingFrames Frames { get; } = new();
    public Services Services { get; }
    public RpcServer Rpc { get; }
    public string Dir { get; }
    public string AppData => Path.Combine(Dir, "appdata");

    public DuckDbHost()
    {
        Dir = Directory.CreateTempSubdirectory("qduckdb-").FullName;
        var registry = new ProviderRegistry([new SqlServerProvider(), new PostgresProvider(), new DuckDbProvider(AppData)]);
        Services = Services.Create(LocalStore.InMemory(), Frames, Notifier, NullLoggerFactory.Instance, registry, AppData);
        Rpc = new RpcServer(Services, () => { });
    }

    /// <summary>
    /// A database file made outside Querithm (as a user would have one), filled by <paramref name="setup"/>. The making
    /// connection is closed before the file is handed over: DuckDB.NET would otherwise share its unlocked instance.
    /// </summary>
    public string NewDatabase(string name = "sales.duckdb", string setup = "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR);")
    {
        var path = Path.Combine(Dir, name);
        using (var c = new DuckDBConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = setup;
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    public static FileGrant Grant(string path, FileAccessMode mode = FileAccessMode.ReadOnly, string id = "g1") =>
        new() { GrantId = id, Path = path, Mode = mode, Identity = FileIdentity.Of(path)! };

    public async Task<(Profile Profile, string SessionId)> OpenFileAsync(string path, FileAccessMode mode = FileAccessMode.ReadOnly, EnvironmentKind env = EnvironmentKind.Dev,
        string grantId = "g1")
    {
        var p = await Rpc.ProfileSave(new Profile
        {
            Id = "", Name = Path.GetFileName(path), Engine = Engine.DuckDb, Auth = AuthKind.None, Location = LocationKind.File,
            File = new FileLocation { GrantId = grantId, DisplayName = Path.GetFileName(path) }, Environment = env,
        });
        var s = await Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, FileGrant = Grant(path, mode, grantId) });
        return (p, s.SessionId);
    }

    public async Task<(Profile Profile, string SessionId)> OpenMemoryAsync()
    {
        var p = await Rpc.ProfileSave(new Profile { Id = "", Name = "scratch", Engine = Engine.DuckDb, Auth = AuthKind.None, Location = LocationKind.Memory });
        var s = await Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id });
        return (p, s.SessionId);
    }

    public async Task<(QueryDoneEvent Done, List<Apache.Arrow.RecordBatch> Batches, string ExecutionId)> RunAsync(string sessionId, string sql, ExecuteOptions? options = null,
        QueryParameter[]? parameters = null)
    {
        if (parameters != null) options = (options ?? new ExecuteOptions()) with { Parameters = parameters };
        var r = await Rpc.QueryExecute(new QueryExecuteParams { SessionId = sessionId, Sql = sql, Options = options });
        if (r.Blocked != null) throw new InvalidOperationException($"blocked: {r.Blocked.Reason}");
        var done = await Notifier.WaitDone(r.ExecutionId!);
        var batches = Frames.For(r.ExecutionId!).Where(f => f.Kind == 0x02).Select(f => TestHost.ReadBatch(f.Payload)).ToList();
        return (done, batches, r.ExecutionId!);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
    }
}
