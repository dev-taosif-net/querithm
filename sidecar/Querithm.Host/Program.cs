using System.Reflection;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using StreamJsonRpc;

// §4: the sidecar owns every database byte. Control plane = JSON-RPC on stdio; data plane = Arrow frames on a local socket.
var exit = new TaskCompletionSource();
ParentWatch.Start(() => Environment.Exit(3));

// stdout belongs to JSON-RPC; anything else printing to Console goes to stderr, and so do native writes to descriptor 1.
var stdout = Querithm.Host.StdoutGuard.Claim();
var stdin = Console.OpenStandardInput();
Console.SetOut(Console.Error);

if (args is ["--stdout-guard-test"])
{
    Querithm.Host.StdoutGuard.NativeWrite("native\n");
    stdout.Write("rpc\n"u8);
    stdout.Flush();
    return 0;
}

// Build check (W10): embedded resources load in the published, trimmed binary. Prints counts only; touches nothing.
if (args is ["--self-test"])
{
    var dictionary = Querithm.Core.Monitoring.WaitDictionary.Load();
    Console.Error.WriteLine($"waitDictionary sqlserver={dictionary.SqlServer.Count} postgres={dictionary.Postgres.Count}");
    // The monitor export serializes snapshots through the reflection-based options: prove the trimmed build keeps them.
    var tick = new Querithm.Contract.MonitorSnapshot
    {
        MonitorId = "self-test", Seq = 1, At = 1, Status = [new Querithm.Contract.PaneStatus { Pane = Querithm.Contract.MonitorPane.Sessions, State = Querithm.Contract.PaneState.Truncated, Total = 2 }],
        Sessions = [new Querithm.Contract.ActivityRow { Key = new Querithm.Contract.SessionKey { Id = 7, Instance = "1:1" }, Status = "running", BlockedBy = [8], IsUserSession = true, WaitMs = 5 }],
        Locks = [new Querithm.Contract.LockRow { Key = new Querithm.Contract.SessionKey { Id = 7, Instance = "1:1" }, ResourceType = "KEY", Mode = "X", Status = "granted", Count = 1 }],
        NewDeadlocks = [new Querithm.Contract.DeadlockEvent { Id = "d", Victims = [], Processes = [], Resources = [] }],
    };
    var json = System.Text.Json.JsonSerializer.Serialize(tick, Json.Options);
    var back = System.Text.Json.JsonSerializer.Deserialize<Querithm.Contract.MonitorSnapshot>(json, Json.Options);
    var jsonOk = back?.Sessions?[0].BlockedBy[0] == 8 && back.Status[0].State == Querithm.Contract.PaneState.Truncated && back.Locks?.Length == 1 && json.Contains("\"truncated\"", StringComparison.Ordinal);
    Console.Error.WriteLine($"monitorJson {(jsonOk ? "ok" : "FAILED")} bytes={json.Length}");
    // History keeps ticks as Deflate-compressed JSON: the trimmed build must round-trip one.
    var history = new Querithm.Core.Monitoring.HistoryBuffer(new Querithm.Core.Monitoring.TextStore());
    history.Add(tick, 1);
    var replayed = history.At(1);
    var historyOk = replayed?.Sessions?.SingleOrDefault()?.BlockedBy is [8] && replayed.Locks?.Length == 1;
    Console.Error.WriteLine($"monitorHistory {(historyOk ? "ok" : "FAILED")} bytes={history.Bytes}");
    // Phase D1: the trimmed build keeps SQLite's native hardening (authorizer delegate, db_config) and the file identity
    // read (System.Native stat shim) that guard file databases.
    var sqliteOk = false;
    try
    {
        var dir = Directory.CreateTempSubdirectory("querithm-selftest-").FullName;
        var file = Path.Combine(dir, "t.db");
        using (var seed = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            seed.Open();
            using var mk = seed.CreateCommand();
            mk.CommandText = "CREATE TABLE t (v); INSERT INTO t VALUES (42);";
            mk.ExecuteNonQuery();
        }
        var identity = Querithm.Core.Sessions.FileIdentity.Of(file)!;
        var grant = new FileGrant { GrantId = "self-test", Path = file, Mode = FileAccessMode.ReadOnly, Identity = identity };
        var profile = new Profile { Id = "self-test", Name = "self-test", Engine = Engine.Sqlite, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "self-test" } };
        await using (var c = await new Querithm.Providers.Sqlite.SqliteProvider().OpenAsync(profile, null, null, grant, CancellationToken.None))
        {
            using var read = c.CreateCommand();
            read.CommandText = "SELECT v FROM t";
            var value = Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            using var attach = c.CreateCommand();
            attach.CommandText = $"ATTACH '{Path.Combine(dir, "x.db")}' AS x";
            var refused = false;
            try { attach.ExecuteNonQuery(); } catch (Microsoft.Data.Sqlite.SqliteException) { refused = true; }
            sqliteOk = value == 42 && refused && !File.Exists(Path.Combine(dir, "x.db")) && identity.StartsWith(OperatingSystem.IsWindows() ? "w:" : "u:", StringComparison.Ordinal);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { /* temp */ }
    }
    catch (Exception ex) { Console.Error.WriteLine($"sqlite self-test error: {ex.GetType().Name}: {ex.Message}"); }
    Console.Error.WriteLine($"sqlite {(sqliteOk ? "ok" : "FAILED")}");
    // Phase D3: the data plane as trimmed — a decimal and a timestamp through the Arrow writer and back (Apache.Arrow 23).
    var arrowOk = false;
    try
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("d").DataType(new Apache.Arrow.Types.Decimal128Type(18, 4)))
            .Field(f => f.Name("t").DataType(new Apache.Arrow.Types.TimestampType(Apache.Arrow.Types.TimeUnit.Microsecond, "UTC"))).Build();
        var d = new Apache.Arrow.Decimal128Array.Builder(new Apache.Arrow.Types.Decimal128Type(18, 4)).Append(12345.6789m).Build();
        var t = new Apache.Arrow.TimestampArray.Builder(new Apache.Arrow.Types.TimestampType(Apache.Arrow.Types.TimeUnit.Microsecond, "UTC")).Append(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)).Build();
        var ipc = Querithm.Core.Execution.ResultSetEncoder.ToIpc(schema, new Apache.Arrow.RecordBatch(schema, [d, t], 1));
        using var decoded = new Apache.Arrow.Ipc.ArrowStreamReader(new MemoryStream(ipc)).ReadNextRecordBatch();
        arrowOk = ((Apache.Arrow.Decimal128Array)decoded.Column(0)).GetValue(0) == 12345.6789m
                  && ((Apache.Arrow.TimestampArray)decoded.Column(1)).GetTimestamp(0) == new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
    }
    catch (Exception ex) { Console.Error.WriteLine($"arrow self-test error: {ex.GetType().Name}: {ex.Message}"); }
    Console.Error.WriteLine($"arrow {(arrowOk ? "ok" : "FAILED")}");
    // Phase D2: the trimmed build keeps MySqlConnector's option table (local-infile must read back as off).
    var mysqlOk = false;
    try
    {
        var mp = new Profile { Id = "self-test", Name = "self-test", Engine = Engine.MySql, Host = "db.invalid", Auth = AuthKind.Password, Username = "u", Options = new() { ["AllowLoadLocalInfile"] = "true" } };
        var built = new MySqlConnector.MySqlConnectionStringBuilder(new Querithm.Providers.MySql.MySqlProvider().BuildConnectionString(mp, "x", "d"));
        mysqlOk = !built.AllowLoadLocalInfile && built.AllowUserVariables && built.SslMode == MySqlConnector.MySqlSslMode.VerifyFull;
        // The plan's raw form ([{text, plan}] through System.Text.Json) and the TREE / JSON v2 parsers, as trimmed.
        var raw = System.Text.Json.JsonSerializer.Serialize(new[] { new Dictionary<string, string> { ["text"] = "SELECT 1", ["plan"] = "-> Rows fetched before execution  (cost=0..0 rows=1) (actual time=0.001..0.001 rows=1 loops=1)" } });
        var tree = new Querithm.Providers.MySql.MySqlPlans().Parse(raw);
        mysqlOk &= tree.Statements.Length == 1 && tree.Statements[0].Root?.ActualRows == 1;
        // The schema-compare text helpers on tokens (DEFINER removal), as trimmed.
        mysqlOk &= Querithm.Providers.MySql.MySqlSchemaCompare.StripDefiner("CREATE DEFINER=`a`@`%` VIEW v AS SELECT 1") == "CREATE VIEW v AS SELECT 1";
    }
    catch (Exception ex) { Console.Error.WriteLine($"mysql self-test error: {ex.GetType().Name}: {ex.Message}"); }
    Console.Error.WriteLine($"mysql {(mysqlOk ? "ok" : "FAILED")}");
    // Phase D3: the trimmed build keeps DuckDB's native library and policy (locked settings, refused file access), its
    // provider-specific values (exact decimals) and the generic read of a list holding a NULL.
    var duckdbOk = false;
    try
    {
        var dir = Directory.CreateTempSubdirectory("querithm-selftest-").FullName;
        var dp = new Profile { Id = "self-test", Name = "self-test", Engine = Engine.DuckDb, Auth = AuthKind.None, Location = LocationKind.Memory };
        await using (var c = await new Querithm.Providers.DuckDb.DuckDbProvider(dir).OpenAsync(dp, null, null, null, CancellationToken.None))
        {
            using var read = c.CreateCommand();
            read.CommandText = "SELECT 12345678901234567890.123456789012345678::DECIMAL(38,18) AS d, [1, NULL]::INT[] AS l";
            using var r = read.ExecuteReader();
            r.Read();
            var text = Querithm.Providers.DuckDb.DuckDbValues.Text(r, 0) + "|" + Querithm.Providers.DuckDb.DuckDbValues.Text(r, 1);
            r.Close();
            using var copy = c.CreateCommand();
            copy.CommandText = $"COPY (SELECT 1) TO '{Path.Combine(dir, "x.csv").Replace("'", "''")}'";
            var refused = false;
            try { copy.ExecuteNonQuery(); } catch (DuckDB.NET.Data.DuckDBException) { refused = true; }
            duckdbOk = text == "12345678901234567890.123456789012345678|[1, NULL]" && refused && !File.Exists(Path.Combine(dir, "x.csv"));
        }
        try { Directory.Delete(dir, true); } catch { /* temp */ }
    }
    catch (Exception ex) { Console.Error.WriteLine($"duckdb self-test error: {ex.GetType().Name}: {ex.Message}"); }
    Console.Error.WriteLine($"duckdb {(duckdbOk ? "ok" : "FAILED")}");
    // Phase D4 (hidden): the trimmed build loads ODP.NET whole (rooted untrimmed: its license allows unmodified copies
    // only) — its connection-string table and a connection object, without dialing — and the skeleton still refuses SQL.
    var oracleOk = false;
    try
    {
        var ob = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder { DataSource = "//db.invalid:1521/FREEPDB1", UserID = "u", Pooling = false };
        await using var oc = new Oracle.ManagedDataAccess.Client.OracleConnection(ob.ConnectionString);
        var read = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(oc.ConnectionString);
        var refused = new Querithm.Providers.Oracle.OracleProvider().Language.Classify("SELECT 1 FROM DUAL").Refusal != null;
        oracleOk = read.DataSource == "//db.invalid:1521/FREEPDB1" && !read.Pooling && oc.State == System.Data.ConnectionState.Closed && refused
                   && Querithm.Providers.Oracle.OracleProvider.DriverVersion.StartsWith("23.", StringComparison.Ordinal);
        Console.Error.WriteLine($"oracle driver {Querithm.Providers.Oracle.OracleProvider.DriverVersion}");
    }
    catch (Exception ex) { Console.Error.WriteLine($"oracle self-test error: {ex.GetType().Name}: {ex.Message}"); }
    Console.Error.WriteLine($"oracle {(oracleOk ? "ok" : "FAILED")}");
    return dictionary.SqlServer.Count > 0 && dictionary.Postgres.Count > 0 && jsonOk && historyOk && sqliteOk && mysqlOk && arrowOk && duckdbOk && oracleOk ? 0 : 1;
}

var dataDir = Environment.GetEnvironmentVariable("QUERITHM_DATA_DIR") ?? Paths.DefaultDataDir();
var token = Environment.GetEnvironmentVariable("QUERITHM_TOKEN");
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("QUERITHM_TOKEN is required (the sidecar is launched by the Querithm shell).");
    return 2;
}
var socket = Environment.GetEnvironmentVariable("QUERITHM_SOCKET") ?? Paths.DefaultSocket();
var level = Environment.GetEnvironmentVariable("QUERITHM_LOG") switch
{
    "debug" => LogLevel.Debug, "trace" => LogLevel.Trace, "warn" => LogLevel.Warning, _ => LogLevel.Information,
};

// L4: <data dir>/querithm is the user's alone (Unix 0700), narrowed if an older build or the umask left it wider.
try { Querithm.Core.Store.PrivateFiles.Folder(dataDir); }
catch (Exception ex) { Console.Error.WriteLine($"[warn] could not make the data folder private: {ex.Message}"); }
// M21: QUERITHM_LOG=trace stays available in release (support asks for it); it adds only Querithm's own debug lines, which
// carry no SQL text, row values or secrets, and no third-party library gets this factory (LogLevelTests).
using var logs = LoggerFactory.Create(b => b.SetMinimumLevel(level).AddProvider(new ScrubbingLoggerProvider(Path.Combine(dataDir, "logs"), level,
    echoToStderr: Environment.GetEnvironmentVariable("QUERITHM_LOG_STDERR") != "0")));
var log = logs.CreateLogger("host");
// M21: a crash leaves its reason in the log (scrubbed) before the runtime ends the process.
CrashLog.Install(log);
log.LogInformation("Querithm sidecar {Version} (contract {Contract}) pid {Pid}, data {Dir}",
    Assembly.GetExecutingAssembly().GetName().Version, ContractInfo.Version, Environment.ProcessId, dataDir);

await using var data = new DataPlaneServer(socket, token, logs.CreateLogger("data"));
data.Start();

var notifier = new RpcNotifier();
// A run whose rows have no reader fails with the reason; the webview also gets the state for its banner (§5).
data.StateChanged += connected => notifier.Notify("dataPlane.state", new DataPlaneStateEvent { Connected = connected });
IDbProvider[] engines = EngineSetup.Providers(dataDir);
// M22: a damaged store.db is set aside and replaced by a fresh one (the user is told once, system.notices) instead of
// failing every start; a store this build must upgrade is copied first, and its history's secrets are masked with each
// engine's rules (8a review). One this build must not touch (written by a newer build, a schema it cannot parse) is left as
// it is: this session runs on an empty in-memory store and the user is told why.
LocalStore store;
string? setAside = null, storeRefused = null;
try { store = LocalStore.OpenOrRecover(Path.Combine(dataDir, "store.db"), logs.CreateLogger("store"), out setAside, LocalStore.HistoryRedactor(engines)); }
catch (StoreRefusedException ex)
{
    storeRefused = ex.Message;
    store = LocalStore.InMemory();
}
// Phase D merge model: engines behind the flag (EngineSetup.BehindFlag) stay hidden unless QUERITHM_DEV_ENGINES or the
// dev.engines setting (comma-separated engine names) exposes them for testing.
var behindFlag = EngineSetup.BehindFlag;
// Spill files a crashed sidecar left behind (Phase D3): their owner no longer runs.
Querithm.Providers.DuckDb.DuckDbInstances.SweepTemp(dataDir);
var devEngines = Environment.GetEnvironmentVariable("QUERITHM_DEV_ENGINES")
                 ?? new SettingsStore(store).Read("dev.engines", Json.Context.String);
var providers = new ProviderRegistry(engines, ProviderRegistry.HiddenEngines(behindFlag, devEngines));
await using var services = Services.Create(store, data, notifier, logs, providers, dataDir);
// L17: history past its retention goes in a background pass, 30 s after start (off the cold start, §14 metric 1).
_ = services.History.SchedulePrune(TimeSpan.FromSeconds(30));
if (storeRefused != null)
    services.Diagnostics.AddNotice(new SystemNotice
    {
        Code = "storeRefused",
        Message = $"{storeRefused} Until then nothing is kept: connections, history and settings made in this session are not saved.",
    });
if (setAside != null)
    services.Diagnostics.AddNotice(new SystemNotice
    {
        Code = "storeReset", File = Path.GetFileName(setAside),
        Message = "Querithm's local data file was damaged and was set aside; connection profiles and history were reset. Passwords in the system keyring are untouched.",
    });

var handler = new NewLineDelimitedMessageHandler(stdout, stdin, QuerithmJsonRpc.Formatter());
using var rpc = new QuerithmJsonRpc(handler);
// L10: bench.* only for the bench / smoke harnesses, which launch the engine with QUERITHM_BENCH=1 (§14).
rpc.AddMethods(new RpcServer(services, () => exit.TrySetResult()), bench: Environment.GetEnvironmentVariable("QUERITHM_BENCH") == "1");
notifier.Attach(rpc);
// PT8-5: a malformed message no longer closes the control plane (GuardedJsonFormatter); when it does close, the log says why.
rpc.Disconnected += (_, e) => log.LogInformation("Control plane closed: {Reason}", e.Reason);
rpc.StartListening();

await Task.WhenAny(rpc.Completion, exit.Task);
log.LogInformation("Sidecar shutting down");
return 0;

static class Paths
{
    public static string DefaultDataDir()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "querithm");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "querithm");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg, "querithm");
    }

    public static string DefaultSocket()
    {
        var pid = Environment.ProcessId;
        if (OperatingSystem.IsWindows()) return $"querithm-{pid}";
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(dir) || OperatingSystem.IsMacOS()) dir = Path.GetTempPath();
        return Path.Combine(dir, $"querithm-{pid}.sock");
    }
}
