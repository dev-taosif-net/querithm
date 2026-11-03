using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

public sealed class CapturingNotifier : INotifier
{
    public ConcurrentQueue<(string Method, object Payload)> All { get; } = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<QueryDoneEvent>> _done = new();

    public void Notify(string method, object payload)
    {
        All.Enqueue((method, payload));
        if (payload is QueryDoneEvent d) Done(d.ExecutionId).TrySetResult(d);
    }

    TaskCompletionSource<QueryDoneEvent> Done(string id) => _done.GetOrAdd(id, _ => new TaskCompletionSource<QueryDoneEvent>(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task<QueryDoneEvent> WaitDone(string executionId, int timeoutMs = 30_000) => Done(executionId).Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    /// <summary>Payloads of one type; <c>Of&lt;ServerMessage&gt;()</c> reads the messages inside each query.messages batch (M13).</summary>
    public IEnumerable<T> Of<T>() =>
        All.Select(x => x.Payload).SelectMany(p => p is QueryMessagesEvent b && typeof(T) == typeof(ServerMessage) ? b.Messages.Cast<object>() : [p]).OfType<T>();
}

public sealed class CapturingFrames : IFrameSink
{
    public ConcurrentQueue<(byte Kind, string ExecutionId, ushort ResultIndex, byte[] Payload)> Frames { get; } = new();

    public ValueTask WriteFrameAsync(byte kind, Ulid executionId, ushort resultIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Frames.Enqueue((kind, executionId.ToString(), resultIndex, payload.ToArray()));
        return ValueTask.CompletedTask;
    }

    public List<(byte Kind, ushort ResultIndex, byte[] Payload)> For(string executionId) =>
        Frames.Where(f => f.ExecutionId == executionId).Select(f => (f.Kind, f.ResultIndex, f.Payload)).ToList();
}

public sealed class TestHost : IAsyncDisposable
{
    public CapturingNotifier Notifier { get; } = new();
    public CapturingFrames Frames { get; } = new();
    public Services Services { get; }
    public RpcServer Rpc { get; }
    /// <summary>The host's own data directory (known_hosts, reject logs), removed on dispose.</summary>
    public string DataDir { get; }

    /// <summary>SQL Server and PostgreSQL unless <paramref name="engines"/> names the providers to register.</summary>
    public TestHost(params IDbProvider[] engines)
    {
        var providers = new ProviderRegistry(engines.Length > 0 ? engines : new IDbProvider[] { new SqlServerProvider(), new PostgresProvider() });
        DataDir = Directory.CreateTempSubdirectory("qhost-").FullName;
        Services = Services.Create(LocalStore.InMemory(), Frames, Notifier, NullLoggerFactory.Instance, providers, DataDir);
        Rpc = new RpcServer(Services, () => { });
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
    }

    /// <summary>The fixture's passwords, read from scripts/bench/docker (L33: they live only there).</summary>
    public static string MssqlPassword => FixtureSecrets.Password("mssql");
    public static string PgPassword => FixtureSecrets.Password("pg");
    /// <summary>Every MySQL / MariaDB fixture service uses the same root password (mysql84's).</summary>
    public static string MySqlPassword => FixtureSecrets.Password("mysql");

    public static bool IntegrationEnabled => Environment.GetEnvironmentVariable("QUERITHM_IT") == "1";

    /// <summary>The SQL Server fixture's host (default localhost); set QUERITHM_IT_MSSQL_HOST=127.0.0.1 where localhost does not reach Docker's IPv4-only port.</summary>
    public static string MssqlHost => Environment.GetEnvironmentVariable("QUERITHM_IT_MSSQL_HOST") is { Length: > 0 } h ? h : "localhost";
    /// <summary>The PostgreSQL fixture host (default localhost); 127.0.0.1 avoids a slow localhost connect on some Windows/Docker setups (ARCHITECTURE §16).</summary>
    public static string PgHost => Environment.GetEnvironmentVariable("QUERITHM_IT_PG_HOST") is { Length: > 0 } h ? h : "localhost";

    public async Task<(Profile Profile, string SessionId)> OpenMssqlAsync(string? database = "master", EnvironmentKind env = EnvironmentKind.Dev)
    {
        var p = await Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "Fixture MSSQL", Engine = Engine.Mssql, Host = MssqlHost, Port = 14333, Auth = AuthKind.Sql, Username = "sa",
            Environment = env, TlsMode = TlsMode.Mandatory, TrustCert = true, DatabaseDefault = database,
        });
        var s = await Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = MssqlPassword, Database = database });
        return (p, s.SessionId);
    }

    public async Task<(Profile Profile, string SessionId)> OpenPgAsync(string database = "postgres")
    {
        var p = await Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "Fixture PG", Engine = Engine.Pg, Host = PgHost, Port = 54329, Auth = AuthKind.Password, Username = "postgres",
            Environment = EnvironmentKind.Dev, TlsMode = TlsMode.Prefer, DatabaseDefault = database,
        });
        var s = await Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = PgPassword, Database = database });
        return (p, s.SessionId);
    }

    /// <summary>
    /// Creates a fixture database unless it exists. Test collections run in parallel and several share a database, so
    /// a CREATE that loses the race fails; that is fine as long as the database exists afterwards.
    /// </summary>
    public async Task EnsureDatabaseAsync(Engine engine, string database)
    {
        var (exists, create) = engine == Engine.Mssql
            ? ($"SELECT CAST(COUNT(*) AS int) FROM sys.databases WHERE name = '{database}'", $"CREATE DATABASE {database}")
            : ($"SELECT count(*)::int FROM pg_database WHERE datname = '{database}'", $"CREATE DATABASE {database}");
        var (_, server) = engine == Engine.Mssql ? await OpenMssqlAsync("master") : await OpenPgAsync("postgres");
        async Task<bool> Exists() => ((Apache.Arrow.Int32Array)(await RunAsync(server, exists)).Batches[0].Column(0)).GetValue(0) > 0;
        if (await Exists()) return;
        var (done, _, id) = await RunAsync(server, create);
        Assert.True(await Exists(), $"could not create {database}: " + string.Join("\n", Messages(id)) + done.Error?.Message);
    }

    public async Task<(QueryDoneEvent Done, List<Apache.Arrow.RecordBatch> Batches, string ExecutionId)> RunAsync(string sessionId, string sql, ExecuteOptions? options = null)
    {
        var r = await Rpc.QueryExecute(new QueryExecuteParams { SessionId = sessionId, Sql = sql, Options = options });
        Assert.NotNull(r.ExecutionId);
        var done = await Notifier.WaitDone(r.ExecutionId!);
        var batches = Frames.For(r.ExecutionId!).Where(f => f.Kind == FrameKind.Batch).Select(f => ReadBatch(f.Payload)).ToList();
        return (done, batches, r.ExecutionId!);
    }

    public static Apache.Arrow.RecordBatch ReadBatch(byte[] ipc)
    {
        using var reader = new Apache.Arrow.Ipc.ArrowStreamReader(new MemoryStream(ipc));
        return reader.ReadNextRecordBatch()!;
    }

    public string[] Messages(string executionId) => Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == executionId).Select(m => m.Message).ToArray();
}

/// <summary>plan.get runs as an execution (§11): the plan arrives as query.plan, the end as query.done.</summary>
public static class PlanRuns
{
    public sealed record Result(string ExecutionId, QueryDoneEvent Done, PlanTree? Plan, string? Raw, string[] Errors)
    {
        public PlanTree Tree => Plan ?? throw new Xunit.Sdk.XunitException($"no plan ({Done.State}): {string.Join(" | ", Errors)}");
    }

    public static async Task<Result> PlanAsync(this IQuerithmRpc rpc, CapturingNotifier notifier, PlanParams p)
    {
        var r = await rpc.PlanGet(p);
        if (r.Blocked is { } b) throw new Xunit.Sdk.XunitException($"plan.get was blocked ({b.Reason})");
        var id = r.ExecutionId!;
        var done = await notifier.WaitDone(id);
        var plan = notifier.Of<QueryPlanEvent>().LastOrDefault(e => e.ExecutionId == id);
        var errors = notifier.Of<ServerMessage>().Where(m => m.ExecutionId == id && m.Severity == Severity.Error).Select(m => m.Message).ToArray();
        return new Result(id, done, plan?.Plan, plan?.Raw, errors);
    }
}

static class Log
{
    public static ILogger Null => NullLogger.Instance;
}

/// <summary>
/// L33: the dev fixture's credentials live only in scripts/bench/docker — compose.yml (the engines' passwords, the SSH
/// bastion's as the build arg SSH_PASSWORD) and ssh/ (the key passphrase) — so the tests read them from there, or from
/// QUERITHM_IT_&lt;ENGINE&gt;_PASSWORD when set (MSSQL, PG, MYSQL, SSH). A reader for the compose file's own plain layout,
/// not a YAML parser; the JS scripts use scripts/fixture/fixture-secrets.mjs. Never print the values.
/// </summary>
public static class FixtureSecrets
{
    static readonly Lazy<string> Compose = new(() => File.ReadAllText(FixtureFile("compose.yml")));

    /// <summary>A file under scripts/bench/docker, found from the test assembly's folder upwards.</summary>
    public static string FixtureFile(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "scripts", "bench", "docker", relative);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"scripts/bench/docker/{relative} not found above {AppContext.BaseDirectory}");
    }

    public static string Password(string engine)
    {
        if (Environment.GetEnvironmentVariable($"QUERITHM_IT_{engine.ToUpperInvariant()}_PASSWORD") is { Length: > 0 } fromEnv) return fromEnv;
        return engine switch
        {
            "mssql" => ComposeValue(Compose.Value, "mssql", "MSSQL_SA_PASSWORD"),
            "pg" => ComposeValue(Compose.Value, "postgres", "POSTGRES_PASSWORD"),
            "mysql" => ComposeValue(Compose.Value, "mysql84", "MYSQL_ROOT_PASSWORD"),
            "ssh" => ComposeValue(Compose.Value, "ssh", "SSH_PASSWORD"),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "no fixture password"),
        };
    }

    /// <summary>The passphrase of scripts/bench/docker/ssh/test_rsa_pass.</summary>
    public static string SshKeyPassphrase => File.ReadAllText(FixtureFile(Path.Combine("ssh", "test_rsa_pass.passphrase"))).Trim();

    /// <summary>A <c>KEY: value</c> line inside one service's block of <paramref name="compose"/> (environment or build.args).</summary>
    public static string ComposeValue(string compose, string service, string key)
    {
        var lines = compose.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        static int Indent(string l) => l.Length - l.TrimStart().Length;
        static bool Meaningful(string l) => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#');
        var services = Array.FindIndex(lines, l => l.TrimEnd() == "services:");
        var start = services < 0 ? -1 : Array.FindIndex(lines, services + 1, l => Indent(l) == 2 && l.Trim() == service + ":");
        if (start < 0) throw new InvalidOperationException($"compose file: no service {service}");
        for (var i = start + 1; i < lines.Length && (!Meaningful(lines[i]) || Indent(lines[i]) > 2); i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var v = line[(key.Length + 1)..].Trim();
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0]) v = v[1..^1];
            else if (v.IndexOf(" #", StringComparison.Ordinal) is var c and >= 0) v = v[..c].TrimEnd();
            if (v.Length > 0) return v;
        }
        throw new InvalidOperationException($"compose file: no {key} for {service}");
    }
}
