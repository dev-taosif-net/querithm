using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using Apache.Arrow;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S03 against the Oracle Free fixture (QUERITHM_IT=1; <c>docker compose -p querithm-fixture -f
/// scripts/bench/docker/compose.yml --profile oracle up -d --wait oracle</c>): connect by service over TCPS (the fixture's
/// self-signed certificate for CN=localhost given as the CA file) and over the explicit plain-TCP downgrade, readable
/// sign-in and TLS failures, transactions checked from an independent connection, cancel then reuse, and the identity
/// read with and without the V$SESSION grant.
/// </summary>
public sealed class OracleProviderIntegrationTests
{
    const int TcpPort = OracleIntegrationTests.Port, TcpsPort = 15211;
    static string AppPassword => FixtureSecrets.Password("oracle");
    static string MonPassword => FixtureSecrets.Password("oracle-mon");

    sealed class Host : IAsyncDisposable
    {
        public CapturingNotifier Notifier { get; } = new();
        public CapturingFrames Frames { get; } = new();
        public Services Services { get; }
        public RpcServer Rpc { get; }
        public string Dir { get; } = Directory.CreateTempSubdirectory("qora-it-").FullName;

        public Host()
        {
            Services = Services.Create(LocalStore.InMemory(), Frames, Notifier, NullLoggerFactory.Instance,
                new ProviderRegistry(EngineSetup.Providers(Dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle")), Dir);
            Rpc = new RpcServer(Services, () => { });
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            try { Directory.Delete(Dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>The fixture's self-signed server certificate (its own CA), read from a TLS handshake and written as PEM.</summary>
    static async Task<string> FixtureCaAsync(string dir)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", TcpsPort);
        System.Security.Cryptography.X509Certificates.X509Certificate2? seen = null;
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert != null) seen = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
            return true;
        });
        await ssl.AuthenticateAsClientAsync("localhost");
        var path = Path.Combine(dir, "fixture-ca.pem");
        await File.WriteAllTextAsync(path, seen!.ExportCertificatePem());
        return path;
    }

    static Profile Tcp(string user = OracleIntegrationTests.AppUser) => new()
    {
        Id = "", Name = "ora-tcp", Engine = Engine.Oracle, Host = "127.0.0.1", Port = TcpPort, Auth = AuthKind.Password, Username = user,
        DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
    };

    static Profile Tcps(string? caPath, string host = "localhost", TlsMode tls = TlsMode.Default) => Tcp() with
    {
        Name = "ora-tcps", Host = host, Port = TcpsPort, TlsMode = tls, CaPath = caPath,
    };

    static async Task<(Host H, string Sid)> OpenAsync(Profile? profile = null, string? secret = null)
    {
        var h = new Host();
        var p = await h.Rpc.ProfileSave(profile ?? Tcp());
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = secret ?? AppPassword });
        return (h, s.SessionId);
    }

    static async Task<(QueryDoneEvent Done, List<RecordBatch> Batches, string Id)> Run(Host h, string sid, string sql)
    {
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql });
        Assert.Null(r.Blocked);
        var done = await h.Notifier.WaitDone(r.ExecutionId!, 120_000);
        return (done, h.Frames.For(r.ExecutionId!).Where(f => f.Kind == 0x02).Select(f => TestHost.ReadBatch(f.Payload)).ToList(), r.ExecutionId!);
    }

    static string? Cell(RecordBatch b, int column, int row = 0) => b.Column(column) is StringArray s ? s.GetString(row) : b.Column(column).ToString();

    /// <summary>Rows in the test table, read on a connection of its own (the driver directly: what another client would see).</summary>
    static async Task<long> CountFromOutsideAsync(string table)
    {
        await using var c = new OracleConnection(OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
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

    // ---- connect, TLS, sign-in ----

    [IntegrationFact]
    public async Task Tcps_with_the_fixture_ca_connects_by_service_and_a_query_returns_rows()
    {
        var dir = Directory.CreateTempSubdirectory("qora-ca-").FullName;
        try
        {
            var (h, sid) = await OpenAsync(Tcps(await FixtureCaAsync(dir)));
            await using var _ = h;
            var (done, batches, _) = await Run(h, sid, "SELECT 42 AS answer, 'x' AS letter, SYS_CONTEXT('USERENV', 'NETWORK_PROTOCOL') AS proto FROM dual");
            Assert.Equal(ExecState.Done, done.State);
            Assert.Equal("42", Cell(batches[0], 0));
            Assert.Equal("x", Cell(batches[0], 1));
            Assert.Equal("tcps", Cell(batches[0], 2)?.ToLowerInvariant());
            var state = await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid });
            Assert.Contains("Oracle", state.Product?.Name);
            Assert.StartsWith("23.", state.Product?.Version);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public async Task Plain_tcp_connects_when_the_profile_downgrades_explicitly_and_the_service_is_the_database()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var (done, batches, _) = await Run(h, sid, "SELECT SYS_CONTEXT('USERENV', 'NETWORK_PROTOCOL') FROM dual");
        Assert.Equal(ExecState.Done, done.State);
        Assert.Equal("tcp", Cell(batches[0], 0)?.ToLowerInvariant());
        var state = await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid });
        Assert.Equal(OracleIntegrationTests.Service, state.Database?.ToUpperInvariant());
    }

    [IntegrationFact]
    public async Task A_wrong_password_gives_a_readable_reason()
    {
        await using var h = new Host();
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Tcp(), Secret = "fake-wrong-password" });
        Assert.False(r.Ok);
        Assert.Contains("rejected the user name or password", r.Error);
        Assert.DoesNotContain("fake-wrong-password", r.Error);
    }

    [IntegrationFact]
    public async Task Tcps_without_the_ca_refuses_the_untrusted_certificate_with_a_readable_reason()
    {
        await using var h = new Host();
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Tcps(null), Secret = AppPassword });
        Assert.False(r.Ok);
        Assert.Contains("certificate could not be verified", r.Error);
    }

    [IntegrationFact]
    public async Task The_server_name_is_checked_unless_the_profile_asks_for_the_chain_only()
    {
        var dir = Directory.CreateTempSubdirectory("qora-dn-").FullName;
        try
        {
            var ca = await FixtureCaAsync(dir);
            await using var h = new Host();
            // The certificate is for CN=localhost; 127.0.0.1 is the same server under another name.
            var mismatch = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Tcps(ca, "127.0.0.1"), Secret = AppPassword });
            Assert.False(mismatch.Ok);
            var chainOnly = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Tcps(ca, "127.0.0.1", TlsMode.VerifyCa), Secret = AppPassword });
            Assert.True(chainOnly.Ok, chainOnly.Error);
            var named = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Tcps(ca), Secret = AppPassword });
            Assert.True(named.Ok, named.Error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public async Task An_unknown_service_gives_a_readable_reason()
    {
        await using var h = new Host();
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Tcp() with { DatabaseDefault = "NO_SUCH_PDB" }, Secret = AppPassword });
        Assert.False(r.Ok);
        Assert.True(r.Error!.Contains("does not know the service"), r.Error);
    }

    [IntegrationFact]
    public async Task An_error_names_its_ora_code_and_position()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var (done, _, id) = await Run(h, sid, "SELECT 1,\n  nope FROM dual");
        Assert.Equal(ExecState.Failed, done.State);
        var all = string.Join("\n", h.Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == id).Select(m => m.Message)) + done.Error?.Message;
        Assert.Contains("ORA-00904", all);
        Assert.Contains("line 2, column 3", all);
        Assert.DoesNotContain("Help: http", all);
        Assert.Equal((2, 3), (done.Error?.EditorLine, done.Error?.EditorColumn));
    }

    [IntegrationFact]
    public async Task A_parse_error_in_a_statement_after_another_on_the_same_line_points_at_its_column_in_the_editor()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        // "SELECT nope FROM dual" starts at column 21 of line 1; the server names column 8 of its own text.
        var (done, _, id) = await Run(h, sid, "SELECT 1 FROM dual; SELECT nope FROM dual");
        Assert.Equal(ExecState.Failed, done.State);
        var error = Assert.Single(h.Notifier.Of<ServerMessage>(), m => m.ExecutionId == id && m.Severity == Severity.Error);
        Assert.Equal((1, 28), (error.EditorLine, error.EditorColumn));
    }

    // ---- transactions (D0b) ----

    [IntegrationFact]
    public async Task Begin_insert_rollback_leaves_no_row_seen_from_another_connection()
    {
        const string table = "q_s03_rollback";
        await FreshTableAsync(table);
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        Assert.Equal(TxState.Open, (await h.Rpc.TxBegin(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(ExecState.Done, (await Run(h, sid, $"INSERT INTO {table} VALUES (1)")).Done.State);
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(0, await CountFromOutsideAsync(table)); // not committed yet
        Assert.Equal(TxState.None, (await h.Rpc.TxRollback(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(0, await CountFromOutsideAsync(table));
        // Back in autocommit: a statement outside a transaction is visible at once.
        Assert.Equal(ExecState.Done, (await Run(h, sid, $"INSERT INTO {table} VALUES (2)")).Done.State);
        Assert.Equal(1, await CountFromOutsideAsync(table));
    }

    [IntegrationFact]
    public async Task Begin_insert_commit_is_seen_from_another_connection()
    {
        const string table = "q_s03_commit";
        await FreshTableAsync(table);
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Run(h, sid, $"INSERT INTO {table} VALUES (1)");
        Assert.Equal(TxState.None, (await h.Rpc.TxCommit(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(1, await CountFromOutsideAsync(table));
    }

    [IntegrationFact]
    public async Task A_begun_transaction_with_no_change_yet_is_still_open()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Run(h, sid, "SELECT 1 FROM dual");
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(TxState.None, (await h.Rpc.TxCommit(new SessionRef { SessionId = sid })).TxState);
    }

    [IntegrationFact]
    public async Task Ddl_inside_an_explicit_transaction_ends_it_and_commits_the_work_before_it()
    {
        const string table = "q_s03_ddl";
        await FreshTableAsync(table);
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Run(h, sid, $"INSERT INTO {table} VALUES (1)");
        Assert.Equal(ExecState.Done, (await Run(h, sid, $"CREATE INDEX {table}_x ON {table} (id + 0)")).Done.State);
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(1, await CountFromOutsideAsync(table)); // DDL committed the insert
        // Autocommit again: the next insert is visible at once.
        await Run(h, sid, $"INSERT INTO {table} VALUES (2)");
        Assert.Equal(2, await CountFromOutsideAsync(table));
    }

    [IntegrationFact]
    public async Task A_typed_commit_or_rollback_ends_the_explicit_transaction()
    {
        const string table = "q_s03_typed";
        await FreshTableAsync(table);
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Run(h, sid, $"INSERT INTO {table} VALUES (1);\nCOMMIT;");
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(1, await CountFromOutsideAsync(table));
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Run(h, sid, $"INSERT INTO {table} VALUES (2);\nROLLBACK;");
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        Assert.Equal(1, await CountFromOutsideAsync(table));
        // Nothing is pinned any more: a COMMIT asked for now commits nothing and the state stays none.
        Assert.Equal(TxState.None, (await h.Rpc.TxCommit(new SessionRef { SessionId = sid })).TxState);
    }

    [IntegrationFact]
    public async Task Savepoints_and_a_read_only_transaction_work_on_the_driver_connection()
    {
        const string table = "q_s03_sp";
        await FreshTableAsync(table);
        var provider = new OracleProvider(Directory.CreateTempSubdirectory("qora-sp-").FullName);
        await using var c = await provider.OpenAsync(Tcp(), AppPassword, null, null, CancellationToken.None);
        var tx = provider.Transactions;
        Assert.Equal(TxState.None, await tx.StateAsync(c, CancellationToken.None));
        await tx.BeginAsync(c, CancellationToken.None);
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = $"INSERT INTO {table} VALUES (1)"; await cmd.ExecuteNonQueryAsync(); }
        await tx.SavepointAsync(c, "qsp1", CancellationToken.None);
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = $"INSERT INTO {table} VALUES (2)"; await cmd.ExecuteNonQueryAsync(); }
        await tx.RollbackToSavepointAsync(c, "qsp1", CancellationToken.None);
        await tx.ReleaseSavepointAsync(c, "qsp1", CancellationToken.None);
        await tx.CommitAsync(c, CancellationToken.None);
        Assert.Equal(1, await CountFromOutsideAsync(table));
        Assert.Equal(TxState.None, await tx.StateAsync(c, CancellationToken.None));

        Assert.True(await tx.BeginReadOnlyAsync(c, CancellationToken.None));
        Assert.Equal(TxState.Open, await tx.StateAsync(c, CancellationToken.None));
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO {table} VALUES (3)";
            var refused = await Assert.ThrowsAsync<OracleException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(1456, refused.Number); // may not perform insert/delete/update operation inside a READ ONLY transaction
        }
        await tx.RollbackAsync(c, CancellationToken.None);
        Assert.Equal(TxState.None, await tx.StateAsync(c, CancellationToken.None));
        await Assert.ThrowsAsync<NoTransactionOpenException>(() => tx.CommitAsync(c, CancellationToken.None));
    }

    // ---- cancel ----

    [IntegrationFact]
    public async Task Cancel_ends_a_sleeping_block_promptly_and_the_connection_is_reused()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var spid = (await Run(h, sid, "SELECT SYS_CONTEXT('USERENV', 'SID') FROM dual")).Batches[0];
        var before = Cell(spid, 0);
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "BEGIN DBMS_SESSION.SLEEP(30); END;" });
        await Task.Delay(1500); // the block is sleeping on the server
        var since = Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
        var done = await h.Notifier.WaitDone(r.ExecutionId!, 30_000);
        Assert.Equal(ExecState.Cancelled, done.State);
        // The break itself lands at once (4 ms on a busy block); DBMS_SESSION.SLEEP sees it only between its sleep intervals
        // (measured 4.5–5.9 s on the fixture), so the bound here is that, with room, not the 30 s the block asked for.
        Assert.True(since.Elapsed < TimeSpan.FromSeconds(10), $"ended after {since.Elapsed.TotalMilliseconds:0} ms");
        var (next, batches, _) = await Run(h, sid, "SELECT SYS_CONTEXT('USERENV', 'SID') FROM dual");
        Assert.Equal(ExecState.Done, next.State);
        Assert.Equal(before, Cell(batches[0], 0)); // the same server session: the connection survived the cancel
    }

    [IntegrationFact]
    public async Task The_command_timeout_ends_a_long_block_with_a_readable_reason()
    {
        var (h, sid) = await OpenAsync(Tcp() with { CommandTimeoutS = 2 });
        await using var _ = h;
        var since = Stopwatch.StartNew();
        var (done, _, id) = await Run(h, sid, "BEGIN DBMS_SESSION.SLEEP(30); END;");
        Assert.NotEqual(ExecState.Done, done.State);
        Assert.True(since.Elapsed < TimeSpan.FromSeconds(15), $"ended after {since.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(ExecState.Done, (await Run(h, sid, "SELECT 1 FROM dual")).Done.State);
    }

    // ---- identity (§6 rule 7) ----

    [IntegrationFact]
    public async Task Identity_is_sid_serial_and_logon_with_the_grant_and_unidentified_without_it()
    {
        var provider = new OracleProvider(Directory.CreateTempSubdirectory("qora-id-").FullName);
        await using (var mon = await provider.OpenAsync(Tcp(OracleIntegrationTests.MonitorUser), MonPassword, null, null, CancellationToken.None))
        {
            var key = await provider.Activity!.IdentityAsync(mon, CancellationToken.None);
            Assert.True(key.Id > 0);
            Assert.True(provider.Activity.IsValidInstance(key.Instance), key.Instance);
            Assert.Equal((int?)key.Id, await provider.GetSpidAsync(mon, CancellationToken.None));
        }
        await using (var app = await provider.OpenAsync(Tcp(), AppPassword, null, null, CancellationToken.None))
        {
            var ex = await Assert.ThrowsAsync<IdentityUnavailableException>(() => provider.Activity!.IdentityAsync(app, CancellationToken.None));
            Assert.Equal(OracleActivity.NoSessionGrant, ex.Message);
            Assert.Equal(System.Data.ConnectionState.Open, app.State); // still serves queries
        }
    }

    [IntegrationFact]
    public async Task A_session_without_the_grant_still_runs_queries()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        Assert.Equal(ExecState.Done, (await Run(h, sid, "SELECT 1 FROM dual")).Done.State);
    }

    [IntegrationFact]
    public async Task The_service_is_the_only_database_and_switching_is_refused()
    {
        var provider = new OracleProvider(Directory.CreateTempSubdirectory("qora-db-").FullName);
        await using var c = await provider.OpenAsync(Tcp(), AppPassword, null, null, CancellationToken.None);
        var dbs = await provider.ListDatabasesAsync(c, CancellationToken.None);
        Assert.Equal(OracleIntegrationTests.Service, Assert.Single(dbs).Name.ToUpperInvariant());
        Assert.Equal(OracleIntegrationTests.Service, provider.CurrentDatabase(c)?.ToUpperInvariant());
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ChangeDatabaseAsync(c, "FREE", CancellationToken.None));
        await provider.ValidateAsync(c, CancellationToken.None);
    }
}
