using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Ssh;
using Querithm.Providers.Oracle;

namespace Querithm.Tests;

/// <summary>
/// D4 S07 without the fixture: Oracle through an SSH forward (the real server's host and port carried to the open, Verify
/// full checked against that name by a handshake of Querithm's own, the subject then checked again by the driver), listener
/// redirects named for what they are — against a fake listener that answers with a TNS REDIRECT the real driver follows —,
/// lost connections in Querithm's words, and the query timeout named when the driver gives no reason.
/// </summary>
public sealed class OracleTunnelTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qora-tun-").FullName;
    readonly OracleProvider _oracle;

    public OracleTunnelTests() => _oracle = new OracleProvider(_dir, unavailable: null);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    static Profile P(TlsMode tls = TlsMode.Default, string host = "db.example.test", int? port = null) => new()
    {
        Id = "p", Name = "ora", Engine = Engine.Oracle, Host = host, Port = port, Auth = AuthKind.Password, Username = "app",
        DatabaseDefault = "ORCLPDB1", TlsMode = tls, TimeoutS = 5,
        Ssh = new SshSettings { Host = "bastion.example.test", Port = 22, Username = "u", Auth = SshAuthKind.Password },
    };

    // ---- route ----

    [Theory]
    [InlineData(TlsMode.Default, null, 2484)]
    [InlineData(TlsMode.VerifyCa, null, 2484)]
    [InlineData(TlsMode.Disable, null, 1521)]
    [InlineData(TlsMode.Disable, 1600, 1600)]
    public void The_tunnel_targets_the_listener_on_the_transports_port(TlsMode tls, int? port, int expected) =>
        Assert.Equal(("db.example.test", expected, (string?)null), _oracle.TunnelTarget(P(tls, port: port)));

    [Fact]
    public void A_profile_that_cannot_open_cannot_be_tunnelled_either()
    {
        Assert.NotNull(_oracle.TunnelTarget(P(host: "bad host)")).Refusal);
        Assert.NotNull(_oracle.TunnelTarget(P() with { TrustCert = true }).Refusal);
    }

    [Fact]
    public void The_routed_profile_dials_the_forward_and_carries_the_real_server()
    {
        var (routed, note) = _oracle.RouteThrough(P(), "127.0.0.1", 40001);
        Assert.Null(note);
        Assert.Equal(("127.0.0.1", 40001), (routed.Host, routed.Port));
        Assert.Equal(("db.example.test", 2484), OracleConnectionStrings.TunnelOf(routed));
        var descriptor = OracleConnectionStrings.Descriptor(routed);
        Assert.Contains("(HOST=127.0.0.1)(PORT=40001)", descriptor);
        Assert.Contains("(SERVER=DEDICATED)", descriptor); // no shared-server dispatcher redirect
        Assert.Contains("(SSL_SERVER_DN_MATCH=TRUE)", descriptor);
        // A direct connection is untouched.
        Assert.DoesNotContain("SERVER=DEDICATED", OracleConnectionStrings.Descriptor(P() with { Ssh = null }));
    }

    [Fact]
    public void Stored_tunnel_options_never_retarget_a_direct_connection_and_are_refused_on_save()
    {
        var planted = P() with { Ssh = null, Options = new() { [OracleConnectionStrings.TunnelHostOption] = "evil.example.test", [OracleConnectionStrings.TunnelPortOption] = "2484" } };
        Assert.Null(OracleConnectionStrings.TunnelOf(planted)); // not a loopback host: the options are not read
        Assert.Contains("Querithm's own", _oracle.ValidateProfile(planted));
        Assert.Contains("Querithm's own", _oracle.ValidateProfile(P() with { Options = new() { [OracleConnectionStrings.TunnelPortOption] = "1" } }));
        Assert.Null(_oracle.ValidateProfile(P()));
    }

    [Fact]
    public void The_checked_subject_reaches_the_descriptor_only_for_verify_full_through_a_tunnel()
    {
        var routed = _oracle.RouteThrough(P(), "127.0.0.1", 40001).Profile;
        Assert.Contains("(SECURITY=(SSL_SERVER_DN_MATCH=TRUE)(SSL_SERVER_CERT_DN=\"CN=db.example.test, O=Example\"))", OracleConnectionStrings.Descriptor(routed, "CN=db.example.test, O=Example"));
        // A subject that could close the clause, a downgraded mode or a direct connection never carries one.
        Assert.Throws<ArgumentException>(() => OracleConnectionStrings.Descriptor(routed, "CN=x\")(HOST=evil"));
        Assert.Throws<ArgumentException>(() => OracleConnectionStrings.Descriptor(_oracle.RouteThrough(P(TlsMode.VerifyCa), "127.0.0.1", 1).Profile, "CN=x"));
        Assert.Throws<ArgumentException>(() => OracleConnectionStrings.Descriptor(_oracle.RouteThrough(P(TlsMode.Disable), "127.0.0.1", 1).Profile, "CN=x"));
        Assert.Throws<ArgumentException>(() => OracleConnectionStrings.Descriptor(P(), "CN=x"));
    }

    [Theory]
    [InlineData(TlsMode.Default, true, 2)]
    [InlineData(TlsMode.VerifyCa, true, 1)]
    [InlineData(TlsMode.Disable, true, 1)]
    [InlineData(TlsMode.Default, false, 1)]
    public void Verify_full_through_a_tunnel_expects_two_connections_per_open(TlsMode tls, bool tunnelled, int expected)
    {
        var p = tunnelled ? _oracle.RouteThrough(P(tls), "127.0.0.1", 40001).Profile : P(tls);
        Assert.Equal(expected, _oracle.TunnelConnectsPerOpen(p));
        Assert.Equal(1, ((IDbProvider)new Fakes.FakeDbProvider()).TunnelConnectsPerOpen(p));
    }

    [Fact]
    public void A_connect_window_admits_as_many_connections_as_it_expects_and_no_more()
    {
        var tunnel = new SshTunnelManager.Tunnel { Key = "k", ProfileId = "p" };
        using (tunnel.Expect(2))
        {
            Assert.True(tunnel.TakeTicket());
            Assert.True(tunnel.TakeTicket());
            Assert.False(tunnel.TakeTicket());
        }
        using (tunnel.Expect()) Assert.True(tunnel.TakeTicket());
        Assert.False(tunnel.TakeTicket()); // the window closed with its unused tickets
        using (tunnel.Expect(2)) { }
        Assert.False(tunnel.TakeTicket());
    }

    // ---- Verify full through the forward ----

    /// <summary>A loopback TLS endpoint (what the SSH forward looks like to the client) presenting <paramref name="cert"/>, then closing.</summary>
    sealed class TlsEndpoint : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly Task _loop;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Handshakes;

        public TlsEndpoint(X509Certificate2 cert)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                            try
                            {
                                await using var ssl = new SslStream(c.GetStream());
                                await ssl.AuthenticateAsServerAsync(cert).WaitAsync(TimeSpan.FromSeconds(10));
                                Interlocked.Increment(ref Handshakes);
                                await Task.Delay(200);
                            }
                            catch { /* the client refused the certificate */ }
                    });
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* stopped */ }
        }
    }

    static X509Certificate2 SelfSigned(string subject, string dns)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    string CaFile(X509Certificate2 cert, string name = "ca.pem")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, cert.ExportCertificatePem());
        return path;
    }

    [Fact]
    public async Task The_certificate_is_checked_against_the_real_server_name_not_the_forward()
    {
        using var cert = SelfSigned("CN=db.example.test, OU=Data, O=Querithm Test, C=US", "db.example.test");
        await using var endpoint = new TlsEndpoint(cert);
        var ca = CaFile(cert);
        var subject = await OracleTunnelTls.ServerSubjectAsync("127.0.0.1", endpoint.Port, "db.example.test", ca, 5, CancellationToken.None);
        Assert.Equal(cert.Subject, subject);

        var wrongName = await Assert.ThrowsAsync<OracleTunnelTls.CertificateRefusedException>(() =>
            OracleTunnelTls.ServerSubjectAsync("127.0.0.1", endpoint.Port, "other.example.test", ca, 5, CancellationToken.None));
        Assert.Contains("not issued for other.example.test", wrongName.Message);

        using var otherCa = SelfSigned("CN=Someone Else", "else.example.test");
        var untrusted = await Assert.ThrowsAsync<OracleTunnelTls.CertificateRefusedException>(() =>
            OracleTunnelTls.ServerSubjectAsync("127.0.0.1", endpoint.Port, "db.example.test", CaFile(otherCa, "other.pem"), 5, CancellationToken.None));
        Assert.Contains("not signed by a CA this computer trusts", untrusted.Message);
    }

    [Fact]
    public async Task Through_a_tunnel_verify_full_refuses_a_certificate_for_another_name_before_the_driver_dials()
    {
        using var cert = SelfSigned("CN=other.example.test", "other.example.test");
        await using var endpoint = new TlsEndpoint(cert);
        var routed = _oracle.RouteThrough(P() with { CaPath = CaFile(cert) }, "127.0.0.1", endpoint.Port).Profile;
        var ex = await Assert.ThrowsAsync<OracleTunnelTls.CertificateRefusedException>(() => _oracle.OpenAsync(routed, "pw", null, null, CancellationToken.None));
        Assert.Contains("not issued for db.example.test", ex.Message);
        Assert.Contains("not issued for db.example.test", Assert.Single(_oracle.TranslateException(ex)).Message);
    }

    [Fact]
    public async Task Through_a_tunnel_the_driver_checks_the_same_subject_on_its_own_handshake()
    {
        // The endpoint is no Oracle listener: it closes after the handshake. What matters is that the driver's own TLS
        // check passed (no ORA-29003 "mismatched server certificate") although it dialled 127.0.0.1.
        using var cert = SelfSigned("CN=db.example.test, OU=Data, O=Querithm Test, C=US", "db.example.test");
        await using var endpoint = new TlsEndpoint(cert);
        var routed = _oracle.RouteThrough(P() with { CaPath = CaFile(cert) }, "127.0.0.1", endpoint.Port).Profile;
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _oracle.OpenAsync(routed, "pw", null, null, CancellationToken.None));
        Assert.IsNotType<OracleTunnelTls.CertificateRefusedException>(ex);
        Assert.DoesNotContain("29003", Chain(ex));
        Assert.Equal(2, endpoint.Handshakes); // Querithm's check, then the driver's
    }

    static string Chain(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e != null; e = e.InnerException) sb.Append(e.Message).Append(" <- ");
        return sb.ToString();
    }

    // ---- listener redirects ----

    /// <summary>A loopback "listener" that answers every TNS CONNECT with a REDIRECT to <paramref name="address"/>.</summary>
    sealed class RedirectingListener : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly Task _loop;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public RedirectingListener(string address)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    using (c)
                        try
                        {
                            var s = c.GetStream();
                            var buf = new byte[8192];
                            if (await s.ReadAsync(buf, _stop.Token) < 8 || buf[4] != 1) continue; // not a TNS CONNECT
                            var data = Encoding.ASCII.GetBytes(address);
                            var total = 8 + 2 + data.Length;
                            // TNS header: length, packet checksum, type 5 (REDIRECT), flags, header checksum; then the data length.
                            byte[] header = [(byte)(total >> 8), (byte)total, 0, 0, 5, 0, 0, 0, (byte)(data.Length >> 8), (byte)data.Length];
                            await s.WriteAsync(header.Concat(data).ToArray(), _stop.Token);
                            await Task.Delay(1000, _stop.Token);
                        }
                        catch { /* client went away */ }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* stopped */ }
        }
    }

    [Theory]
    [InlineData("(ADDRESS=(PROTOCOL=TCP)(HOST=scan-node-1.invalid)(PORT=1521))", "another address")]
    [InlineData("(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=1))", "127.0.0.1:1")]
    public async Task A_listener_redirect_the_tunnel_does_not_cover_is_refused_with_the_reason(string redirect, string named)
    {
        await using var listener = new RedirectingListener(redirect);
        var routed = _oracle.RouteThrough(P(TlsMode.Disable, host: "scan.example.test"), "127.0.0.1", listener.Port).Profile;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _oracle.OpenAsync(routed, "pw", null, null, CancellationToken.None));
        Assert.Contains("listener at scan.example.test:1521 sent the connection on to " + named, ex.Message);
        Assert.Contains("RAC SCAN", ex.Message);
        Assert.Equal(ex.Message, Assert.Single(_oracle.TranslateException(ex)).Message);
    }

    [Fact]
    public async Task Without_a_tunnel_a_redirect_is_the_drivers_own_error()
    {
        await using var listener = new RedirectingListener("(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=1))");
        var direct = P(TlsMode.Disable, host: "127.0.0.1", port: listener.Port) with { Ssh = null };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _oracle.OpenAsync(direct, "pw", null, null, CancellationToken.None));
        Assert.IsNotType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Redirect_detection_reads_only_connect_failures_away_from_the_forward()
    {
        static Exception Driver(string inner) => new Exception("ORA-50201: Oracle Communication: Failed to connect to server", new IOException(inner));
        Assert.Contains("10.1.2.3:1521", OracleErrors.RedirectRefusal(Driver("ORA-12541: Cannot connect. No listener at  host 10.1.2.3 port 1521. (CONNECTION_ID=x)"), 40001, "db", 2484));
        Assert.NotNull(OracleErrors.RedirectRefusal(Driver("ORA-12545: Connect failed because target host or object does not exist"), 40001, "db", 2484));
        Assert.NotNull(OracleErrors.RedirectRefusal(new Exception("x", new SocketException((int)SocketError.HostNotFound)), 40001, "db", 2484));
        // The forward itself not listening is no redirect; neither is a sign-in or a lost connection.
        Assert.Null(OracleErrors.RedirectRefusal(Driver("ORA-12541: Cannot connect. No listener at host 127.0.0.1 port 40001."), 40001, "db", 2484));
        Assert.Null(OracleErrors.RedirectRefusal(Driver("ORA-01017: invalid credential"), 40001, "db", 2484));
        Assert.Null(OracleErrors.RedirectRefusal(Driver("ORA-12537: TNS:connection closed"), 40001, "db", 2484));
        Assert.Null(OracleErrors.NoListenerAt("ORA-12541: No listener at host bad)host port 1."));
    }

    // ---- lost connections, timeouts ----

    [Theory]
    [InlineData(3113, "connection to the server was lost (ORA-03113)")]
    [InlineData(3114, "connection to the server was lost (ORA-03114)")]
    [InlineData(12537, "connection to the server was lost (ORA-12537)")]
    [InlineData(12547, "connection to the server was lost (ORA-12547)")]
    [InlineData(28, "server ended this session (ORA-00028")]
    [InlineData(1012, "no longer signed in")]
    [InlineData(2396, "idle for too long")]
    public void A_lost_connection_says_so_and_that_the_next_run_reconnects(int code, string expected)
    {
        var message = Assert.Single(_oracle.TranslateException(OracleErrorFactory.Create(code, $"ORA-{code:D5}: driver text")));
        Assert.Contains(expected, message.Message);
        Assert.Contains("Run again to reconnect", message.Message);
        Assert.True(OracleErrors.IsConnectionLost(code));
        Assert.Equal(code, message.Number);
    }

    [Fact]
    public void A_bare_cancellation_from_the_query_timeout_names_the_timeout()
    {
        var session = new Session("s", P() with { CommandTimeoutS = 7 }, _oracle, null);
        Assert.Contains("query timeout (7 s)", ExecutionService.TimeoutText(session, new TaskCanceledException()));
        // Without a timeout (or for a driver that says why) the driver's text stays.
        Assert.Equal("A task was canceled.", ExecutionService.TimeoutText(new Session("s", P(), _oracle, null), new TaskCanceledException()));
        Assert.Equal("Execution Timeout Expired", ExecutionService.TimeoutText(session, new TimeoutException("Execution Timeout Expired")));
    }
}
