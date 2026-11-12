using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S07 against the Oracle Free fixture (QUERITHM_IT=1): what a tab sees when its connection goes away and that it runs
/// again afterwards with nothing pinned — the session killed from an admin connection mid-result and inside an open
/// transaction, the engine exiting with a transaction open, and a server whose answer to Cancel never arrives (a relay
/// that stops forwarding). The query timeout and an acknowledged cancel are OracleProviderIntegrationTests'.
/// </summary>
public sealed class OracleRecoveryTests
{
    static string AppPassword => FixtureSecrets.Password("oracle");

    static Profile Tcp(string host = "127.0.0.1", int port = OracleIntegrationTests.Port) => new()
    {
        Id = "", Name = "ora-recovery", Engine = Engine.Oracle, Host = host, Port = port, Auth = AuthKind.Password,
        Username = OracleIntegrationTests.AppUser, DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
    };

    static TestHost NewHost() => new(TestOracle.Supported(Directory.CreateTempSubdirectory("qora-rec-").FullName));

    static async Task<string> OpenAsync(TestHost h, Profile? p = null)
    {
        var saved = await h.Rpc.ProfileSave(p ?? Tcp());
        return (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = AppPassword })).SessionId;
    }

    static async Task<OracleConnection> AdminAsync()
    {
        var b = new OracleConnectionStringBuilder(OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser))
        {
            UserID = "system", Password = FixtureSecrets.Password("oracle-admin"),
        };
        var c = new OracleConnection(b.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    /// <summary>Kills the app user's sessions matching <paramref name="where"/> (a v$session predicate); returns how many.</summary>
    static async Task<int> KillAsync(string where)
    {
        await using var admin = await AdminAsync();
        var targets = new List<(string Sid, string Serial)>();
        await using (var q = admin.CreateCommand())
        {
            q.CommandText = $"SELECT s.sid, s.serial# FROM v$session s WHERE s.username = 'QUERITHM_APP' AND ({where})";
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync()) targets.Add((Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!, Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture)!));
        }
        foreach (var (sid, serial) in targets)
        {
            await using var k = admin.CreateCommand();
            k.CommandText = $"ALTER SYSTEM KILL SESSION '{sid},{serial}' IMMEDIATE";
            // ORA-00031 "session marked for kill": the session was busy (here: mid network write of a result) and the server
            // kills it as soon as it can — still a kill. Seen once in D4 S13's gate run (1 of 4 runs of this class).
            try { await k.ExecuteNonQueryAsync(); }
            catch (OracleException e) when (e.Number == 31) { }
        }
        return targets.Count;
    }

    static async Task<long> CountFromOutsideAsync(string table)
    {
        await using var c = new OracleConnection(OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    static async Task FreshTableAsync(string table)
    {
        await using var c = new OracleConnection(OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {table} PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = $"CREATE TABLE {table} (id NUMBER(10) PRIMARY KEY)";
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task AssertRunsAgainAsync(TestHost h, string sid)
    {
        var (next, batches, id) = await h.RunAsync(sid, "SELECT 1 AS one FROM dual");
        Assert.True(next.State == ExecState.Done, string.Join("\n", h.Messages(id)) + next.Error?.Message);
        Assert.Single(batches);
        Assert.Null(h.Services.Sessions.Get(sid).Pinned);
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
    }

    [IntegrationFact]
    public async Task A_session_killed_mid_result_ends_the_run_with_a_clear_message_and_the_tab_runs_again()
    {
        await using var h = NewHost();
        var sid = await OpenAsync(h);
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams
        {
            SessionId = sid, Sql = "SELECT /* q_s07_kill_mid */ level AS n, RPAD('x', 40, 'x') AS pad FROM dual CONNECT BY level <= 20000000",
            Options = new ExecuteOptions { MaxRows = 0 }, // no row limit: the result is still streaming when the kill lands
        });
        var id = r.ExecutionId!;
        var sw = Stopwatch.StartNew();
        while (!h.Frames.For(id).Any(f => f.Kind == FrameKind.Batch) && sw.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(50);
        Assert.True(h.Frames.For(id).Any(f => f.Kind == FrameKind.Batch), "no batch arrived");
        Assert.Equal(1, await KillAsync("(s.sql_id IN (SELECT sql_id FROM v$sql WHERE sql_text LIKE '%q_s07_kill_mid%' AND sql_text NOT LIKE '%v$sql%') OR s.prev_sql_id IN (SELECT sql_id FROM v$sql WHERE sql_text LIKE '%q_s07_kill_mid%' AND sql_text NOT LIKE '%v$sql%'))"));

        var done = await h.Notifier.WaitDone(id, 60_000);
        Assert.True(done.State == ExecState.Failed, string.Join("\n", h.Messages(id)) + done.Error?.Message);
        var said = string.Join("\n", h.Messages(id)) + done.Error?.Message;
        Assert.True(said.Contains("connection to the server was lost", StringComparison.Ordinal) || said.Contains("server ended this session", StringComparison.Ordinal), said);
        Assert.Contains("Run again to reconnect", said);
        Assert.Contains(h.Frames.For(id), f => f.Kind == FrameKind.EndOfExecution); // the grid is told the result ended
        await AssertRunsAgainAsync(h, sid);
    }

    [IntegrationFact]
    public async Task A_session_killed_inside_an_open_transaction_tells_the_tab_the_transaction_is_gone()
    {
        const string table = "q_s07_kill_tx";
        await FreshTableAsync(table);
        await using var h = NewHost();
        var sid = await OpenAsync(h);
        Assert.Equal(TxState.Open, (await h.Rpc.TxBegin(new SessionRef { SessionId = sid })).TxState);
        var (insert, batches, insertId) = await h.RunAsync(sid, $"INSERT INTO {table} VALUES (1);\nSELECT SYS_CONTEXT('USERENV', 'SID') AS sid FROM dual;");
        Assert.True(insert.State == ExecState.Done, string.Join("\n", h.Messages(insertId)) + insert.Error?.Message);
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.NotNull(h.Services.Sessions.Get(sid).Pinned);
        var serverSid = ((Apache.Arrow.StringArray)batches[^1].Column(0)).GetString(0);
        Assert.Equal(1, await KillAsync($"s.sid = {int.Parse(serverSid, CultureInfo.InvariantCulture)}"));

        var (next, _, nextId) = await h.RunAsync(sid, "SELECT 1 AS one FROM dual");
        Assert.Equal(ExecState.Failed, next.State);
        var said = string.Join("\n", h.Messages(nextId)) + next.Error?.Message;
        Assert.Contains("open transaction was rolled back", said);
        Assert.Equal(0, await CountFromOutsideAsync(table)); // the server rolled the insert back
        await AssertRunsAgainAsync(h, sid);
    }

    [IntegrationFact]
    public async Task An_engine_that_exits_with_a_transaction_open_leaves_nothing_committed_and_a_new_engine_connects()
    {
        const string table = "q_s07_restart";
        await FreshTableAsync(table);
        var first = NewHost();
        var sid = await OpenAsync(first);
        await first.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var (insert, _, id) = await first.RunAsync(sid, $"INSERT INTO {table} VALUES (1)");
        Assert.True(insert.State == ExecState.Done, string.Join("\n", first.Messages(id)) + insert.Error?.Message);
        Assert.Equal(TxState.Open, (await first.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        // The engine goes away: its connections close (as a crash closes their sockets); the server rolls back.
        await first.DisposeAsync();
        Assert.Equal(0, await CountFromOutsideAsync(table));

        // The restarted engine has the profile replayed by the window and opens a fresh session on it.
        await using var second = NewHost();
        var again = await OpenAsync(second);
        var (count, batches, countId) = await second.RunAsync(again, $"SELECT COUNT(*) AS n FROM {table}");
        Assert.True(count.State == ExecState.Done, string.Join("\n", second.Messages(countId)) + count.Error?.Message);
        Assert.Equal("0", batches[0].Column(0) is Apache.Arrow.StringArray s ? s.GetString(0) : batches[0].Column(0).ToString());
    }

    /// <summary>A loopback TCP relay to the fixture that can stop forwarding (a server or network that goes silent) and then drop every connection.</summary>
    sealed class Relay : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly List<TcpClient> _open = [];
        readonly Task _loop;
        volatile bool _frozen;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Relay(int target)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    var server = new TcpClient();
                    try { await server.ConnectAsync(IPAddress.Loopback, target, _stop.Token); }
                    catch { client.Dispose(); server.Dispose(); continue; }
                    lock (_open) { _open.Add(client); _open.Add(server); }
                    _ = Pump(client.GetStream(), server.GetStream());
                    _ = Pump(server.GetStream(), client.GetStream());
                }
            });
        }

        async Task Pump(NetworkStream from, NetworkStream to)
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var n = await from.ReadAsync(buf, _stop.Token);
                    if (n == 0) break;
                    while (_frozen) await Task.Delay(20, _stop.Token); // silent: nothing passes either way
                    await to.WriteAsync(buf.AsMemory(0, n), _stop.Token);
                }
            }
            catch { /* dropped */ }
        }

        public void Freeze() => _frozen = true;

        /// <summary>Every connection through the relay ends at once (resets), as when the network gives up.</summary>
        public void DropAll()
        {
            _frozen = false; // connections made after this pass again
            lock (_open)
            {
                foreach (var c in _open)
                {
                    try { c.Client.LingerState = new LingerOption(true, 0); } catch { /* closed */ }
                    c.Dispose();
                }
                _open.Clear();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            DropAll();
            try { await _loop; } catch { /* stopped */ }
        }
    }

    [IntegrationFact]
    public async Task A_cancel_the_server_never_answers_is_reported_and_the_run_ends_when_the_connection_goes()
    {
        await using var relay = new Relay(OracleIntegrationTests.Port);
        await using var h = NewHost();
        h.Services.Executions.CancelGrace = TimeSpan.FromSeconds(1);
        h.Services.Executions.StuckLimit = TimeSpan.FromSeconds(2);
        var sid = await OpenAsync(h, Tcp(port: relay.Port));
        Assert.Equal(ExecState.Done, (await h.RunAsync(sid, "SELECT 1 FROM dual")).Done.State);

        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "BEGIN DBMS_SESSION.SLEEP(25); END;" });
        var id = r.ExecutionId!;
        await Task.Delay(1500); // sleeping on the server
        relay.Freeze();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = id });

        // Oracle is never closed mid-command (its Close waits for the call): the user is told the run is stuck and how out.
        var sw = Stopwatch.StartNew();
        while (!h.Messages(id).Any(m => m.Contains("Restart the engine", StringComparison.Ordinal)) && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
        Assert.Contains(h.Messages(id), m => m.Contains("Restart the engine", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Messages(id), m => m.Contains("closed this tab's connection", StringComparison.Ordinal));

        // The connection goes (a restart closes it; here the network drops it): the run ends and the tab runs again.
        relay.DropAll();
        var done = await h.Notifier.WaitDone(id, 30_000);
        Assert.NotEqual(ExecState.Done, done.State);
        await AssertRunsAgainAsync(h, sid);
    }
}
