using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Querithm.Contract;
using Querithm.Core.Ssh;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Renci.SshNet;

namespace Querithm.Tests.Security;

/// <summary>
/// Regression tests for the PT5 / PT1 / PT9 provider fixes, beyond the pentest PoCs: PostgreSQL's cleartext-auth refusal
/// without a checked certificate (PT5-1), the host name still checked through an SSH forward for PostgreSQL and MySQL
/// (PT5-4, against loopback TLS servers with a throwaway CA), the hardened SSH algorithm lists (PT5-5) and the known_hosts
/// RSA key type (rsa-sha2-* vs ssh-rsa), certificate / failover notes for every server engine, MySQL version text and
/// timeout clamps.
/// </summary>
public sealed class ProviderTlsHardeningTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qtls-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    static Profile P(Engine engine, TlsMode tls = TlsMode.Default, string host = "db.example.test", string? ca = null) => new()
    {
        Id = "tls-" + engine, Name = "tls", Engine = engine, Host = host, Auth = engine == Engine.Mssql ? AuthKind.Sql : AuthKind.Password,
        Username = "u", Environment = EnvironmentKind.Dev, TlsMode = tls, CaPath = ca, TimeoutS = 5,
    };

    // ------------------------------------------------------------------ PT5-1 PostgreSQL: no cleartext password without a checked certificate

    [Theory]
    [InlineData(TlsMode.Default, true)]
    [InlineData(TlsMode.Require, true)]
    [InlineData(TlsMode.Prefer, true)]
    [InlineData(TlsMode.Optional, true)]
    [InlineData(TlsMode.VerifyCa, false)]
    [InlineData(TlsMode.VerifyFull, false)]
    [InlineData(TlsMode.Disable, false)]
    public void Postgres_refuses_cleartext_auth_only_where_the_certificate_is_not_checked(TlsMode mode, bool restricted)
    {
        var b = new NpgsqlConnectionStringBuilder(new PostgresProvider().BuildConnectionString(P(Engine.Pg, mode), "pw", null));
        if (!restricted)
        {
            Assert.Null(b.RequireAuth); // LDAP / RADIUS / Entra token cleartext over a verified TLS keeps working
            return;
        }
        var allowed = b.RequireAuth!.Split(',', StringSplitOptions.TrimEntries);
        Assert.DoesNotContain("Password", allowed, StringComparer.OrdinalIgnoreCase);
        foreach (var keep in new[] { "ScramSHA256", "MD5", "GSS", "SSPI", "None" }) Assert.Contains(keep, allowed, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ChannelBinding.Prefer, b.ChannelBinding); // Require would refuse servers without SCRAM-PLUS
    }

    /// <summary>
    /// Opens the provider's own connection string against the fake. GSS encryption is turned off for the fake only: with
    /// Npgsql's default (Prefer) the handshake with this loopback server stalls on Windows whatever Require Auth says
    /// (measured: 30 s with and without it), which real servers do not do.
    /// </summary>
    static async Task<Exception> OpenAgainstFake(Profile p)
    {
        var cs = new PostgresProvider().BuildConnectionString(p, "s3cret-zz1", null) + ";GSS Encryption Mode=Disable";
        await using var c = new NpgsqlConnection(cs);
        return await Assert.ThrowsAnyAsync<Exception>(() => c.OpenAsync());
    }

    [Fact]
    public async Task Postgres_server_asking_for_a_cleartext_password_under_require_gets_nothing_and_a_clear_reason()
    {
        var (ca, _) = Certificates.Ca(_dir);
        using var __ = ca;
        using var cert = Certificates.Issued(ca, "anything.example"); // Require checks nothing about it
        await using var server = FakePg.Start(cert, auth: 3); // AuthenticationCleartextPassword
        var ex = await OpenAgainstFake(P(Engine.Pg, TlsMode.Require, host: "127.0.0.1") with { Port = server.Port });
        Assert.True(PostgresProvider.IsRefusedCleartext(ex), string.Join(" | ", server.Trace) + " || " + ex);
        Assert.True(await server.SawStartup);
        Assert.False(server.SawPassword); // no PasswordMessage left the client
        Assert.DoesNotContain("s3cret-zz1", server.Received);
        // What OpenAsync throws in its place, and what the user reads.
        var shown = new PostgresProvider().TranslateException(new NpgsqlException(PostgresProvider.CleartextRefused, ex))[0].Message;
        Assert.StartsWith(PostgresProvider.CleartextRefused, shown);
        Assert.Contains("Verify full", shown);
    }

    [Fact]
    public void Only_the_cleartext_refusal_is_reworded()
    {
        Assert.True(PostgresProvider.IsRefusedCleartext(new NpgsqlException("\"Password\" authentication method is not allowed. Allowed methods: MD5, GSS, SSPI, ScramSHA256, None")));
        Assert.False(PostgresProvider.IsRefusedCleartext(new NpgsqlException("Exception while reading from stream")));
        Assert.False(PostgresProvider.IsRefusedCleartext(new InvalidOperationException("\"Password\" authentication method is not allowed.")));
    }

    [Fact]
    public async Task Postgres_md5_server_still_gets_its_answer_under_require()
    {
        var (ca, _) = Certificates.Ca(_dir);
        using var __ = ca;
        using var cert = Certificates.Issued(ca, "anything.example");
        await using var server = FakePg.Start(cert, auth: 5); // AuthenticationMD5Password
        await OpenAgainstFake(P(Engine.Pg, TlsMode.Require, host: "127.0.0.1") with { Port = server.Port });
        Assert.True(server.SawPassword); // the md5 hash was sent: compatibility kept
        Assert.DoesNotContain("s3cret-zz1", server.Received);
    }

    // ------------------------------------------------------------------ PT5-4 Verify full through an SSH forward checks the real name

    [Theory]
    [InlineData("db.example.test", true)]
    [InlineData("attacker.example", false)]
    public async Task Postgres_verify_full_through_a_forward_checks_the_real_host_name(string certName, bool accepted)
    {
        var (ca, caPath) = Certificates.Ca(_dir);
        using var _ = ca;
        using var leaf = Certificates.Issued(ca, certName);
        await using var server = FakePg.Start(leaf, auth: 0);
        var pg = new PostgresProvider();
        var (routed, note) = pg.RouteThrough(P(Engine.Pg, TlsMode.VerifyFull, ca: caPath), "127.0.0.1", server.Port);
        Assert.Null(note);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => pg.OpenAsync(routed, "pw", null, null, CancellationToken.None)); // the fake ends after the startup
            Assert.Equal(accepted, server.SawStartupOverTls);
        }
        finally { pg.ForgetConnections(routed.Id); }
    }

    [Fact]
    public async Task Postgres_tunnel_name_is_not_honoured_for_a_direct_connection()
    {
        // The option only matters on a loopback host: without it, VerifyFull checks the dialled name (127.0.0.1) and fails.
        var (ca, caPath) = Certificates.Ca(_dir);
        using var _ = ca;
        using var leaf = Certificates.Issued(ca, "db.example.test");
        await using var server = FakePg.Start(leaf, auth: 0);
        var pg = new PostgresProvider();
        var direct = P(Engine.Pg, TlsMode.VerifyFull, host: "127.0.0.1", ca: caPath) with { Port = server.Port };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => pg.OpenAsync(direct, "pw", null, null, CancellationToken.None));
            Assert.False(server.SawStartupOverTls);
        }
        finally { pg.ForgetConnections(direct.Id); }
        Assert.Null(PostgresProvider.TunnelTlsHost(P(Engine.Pg, TlsMode.VerifyFull, host: "other.example") with { Options = new() { [PostgresProvider.TunnelTlsHostOption] = "db.example.test" } }));
    }

    [Theory]
    [InlineData("db.example.test", true, true)]
    [InlineData("attacker.example", true, false)]
    [InlineData("db.example.test", false, false)] // a private CA without the CA file: not in the system store
    public async Task Mysql_verify_full_through_a_forward_checks_the_real_host_name(string certName, bool withCaFile, bool accepted)
    {
        var (ca, caPath) = Certificates.Ca(_dir);
        using var _ = ca;
        using var leaf = Certificates.Issued(ca, certName);
        await using var server = FakeMySql.Start(leaf);
        var my = new MySqlProvider();
        var (routed, note) = my.RouteThrough(P(Engine.MySql, ca: withCaFile ? caPath : null), "127.0.0.1", server.Port);
        Assert.Null(note);
        Assert.Equal(MySqlSslMode.VerifyFull, new MySqlConnectionStringBuilder(my.BuildConnectionString(routed, "pw", null)).SslMode);
        await Assert.ThrowsAnyAsync<Exception>(() => my.OpenAsync(routed, "pw", null, null, CancellationToken.None)); // the fake ends after the login packet
        Assert.Equal(accepted, server.SawLoginOverTls);
    }

    [Fact]
    public void Mysql_tunnel_name_is_read_only_for_a_loopback_verify_full_profile()
    {
        var opt = new Dictionary<string, string> { [MySqlProvider.TunnelTlsHostOption] = "db.example.test" };
        Assert.Equal("db.example.test", MySqlProvider.TunnelTlsHost(P(Engine.MySql, host: "127.0.0.1") with { Options = opt }));
        Assert.Null(MySqlProvider.TunnelTlsHost(P(Engine.MySql, host: "db.other.example") with { Options = opt }));
        Assert.Null(MySqlProvider.TunnelTlsHost(P(Engine.MySql, TlsMode.Require, host: "127.0.0.1") with { Options = opt }));
        Assert.Throws<InvalidOperationException>(() => MySqlProvider.VerifyFullFor("db.example.test", Path.Combine(_dir, "missing.pem")));
    }

    [Fact]
    public void Sqlserver_through_a_forward_keeps_the_name_as_before()
    {
        var (routed, _) = new SqlServerProvider().RouteThrough(P(Engine.Mssql), "127.0.0.1", 40003);
        Assert.Equal("db.example.test", new SqlConnectionStringBuilder(new SqlServerProvider().BuildConnectionString(routed, "pw", null)).HostNameInCertificate);
    }

    // ------------------------------------------------------------------ PT1-1 / PT5-2 / PT9-3 notes for every server engine

    [Fact]
    public void Certificate_retargeting_keywords_are_named_in_notes_for_every_engine()
    {
        var ms = new SqlServerProvider().ParseConnectionString("Server=db.example.test;HostNameInCertificate=attacker.example", P(Engine.Mssql));
        Assert.Contains(ms.Notes, n => n.Contains("attacker.example") && n.Contains("db.example.test"));
        var pg = new PostgresProvider().ParseConnectionString(@"Host=db.example.test;Root Certificate=C:\x\ca.pem", P(Engine.Pg));
        Assert.Contains(pg.Notes, n => n.Contains(@"C:\x\ca.pem") && n.Contains("system's trusted CAs"));
        var my = new MySqlProvider().ParseConnectionString(@"Server=db.example.test;SslCa=C:\x\ca.pem", P(Engine.MySql));
        Assert.Contains(my.Notes, n => n.Contains(@"C:\x\ca.pem") && n.Contains("system's trusted CAs"));
        // Nothing named, nothing said.
        Assert.Empty(new SqlServerProvider().ParseConnectionString("Server=db.example.test", P(Engine.Mssql)).Notes);
        Assert.Empty(new PostgresProvider().ParseConnectionString("Host=db.example.test", P(Engine.Pg)).Notes);
        Assert.Empty(new MySqlProvider().ParseConnectionString("Server=db.example.test", P(Engine.MySql)).Notes);
    }

    [Fact]
    public void Host_lists_are_named_in_full_and_sql_server_has_none()
    {
        var pg = new PostgresProvider().ParseConnectionString("Host=decoy.invalid,10.0.4.12:5433", P(Engine.Pg));
        Assert.Contains(pg.Notes, n => n.Contains("2 servers") && n.Contains("decoy.invalid") && n.Contains("10.0.4.12:5433"));
        var my = new MySqlProvider().ParseConnectionString("Server=decoy.invalid, 10.0.4.12", P(Engine.MySql));
        Assert.Contains(my.Notes, n => n.Contains("2 servers") && n.Contains("decoy.invalid") && n.Contains("10.0.4.12"));
        // SqlClient reads one Data Source (a list is a single, unresolvable name), so there is nothing to list.
        Assert.DoesNotContain(new SqlServerProvider().ParseConnectionString("Server=db.example.test,1433", P(Engine.Mssql)).Notes, n => n.Contains("servers"));
    }

    // ------------------------------------------------------------------ PT9-1 / PT1-3 MySQL

    [Theory]
    [InlineData("8.0.36", false, "8.0.36")]
    [InlineData("8.0.36 (2 ms) through the SSH tunnel\nok", false, "8.0.36")]
    [InlineData("5.5.5-10.11.6-MariaDB-1:10.11.6+maria~ubu2204", true, "10.11.6-MariaDB-1")]
    [InlineData("5.5.5-10.11.6-MariaDB", false, "5.5.5-10.11.6-MariaDB")] // the prefix is MariaDB's only
    [InlineData(" spoof", false, "unknown")]
    [InlineData("", false, "unknown")]
    public void Mysql_version_text_is_reduced_to_its_version_token(string raw, bool maria, string shown) =>
        Assert.Equal(shown, MySqlProvider.CleanVersion(raw, maria));

    [Fact]
    public void Mysql_version_token_is_at_most_64_characters() =>
        Assert.Equal(64, MySqlProvider.CleanVersion(new string('9', 200), false).Length);

    [Theory]
    [InlineData(-1, -1, 15u, 0u)]
    [InlineData(int.MinValue, int.MinValue, 15u, 0u)]
    [InlineData(0, 0, 0u, 0u)]
    [InlineData(30, 600, 30u, 600u)]
    public void Mysql_timeouts_never_wrap(int timeout, int command, uint connect, uint commandBuilt)
    {
        var b = new MySqlConnectionStringBuilder(new MySqlProvider().BuildConnectionString(P(Engine.MySql) with { TimeoutS = timeout, CommandTimeoutS = command }, "pw", null));
        Assert.Equal((connect, commandBuilt), (b.ConnectionTimeout, b.DefaultCommandTimeout));
    }

    // ------------------------------------------------------------------ PT5-5 SSH algorithms and known_hosts key types

    [Fact]
    public void Hardened_ssh_client_still_offers_what_current_servers_use()
    {
        var i = SshTunnelManager.Harden(new ConnectionInfo("127.0.0.1", 22, "u", new PasswordAuthenticationMethod("u", "pw")));
        Assert.Contains("curve25519-sha256", i.KeyExchangeAlgorithms.Keys);
        Assert.Contains("diffie-hellman-group14-sha256", i.KeyExchangeAlgorithms.Keys);
        Assert.Contains("ssh-ed25519", i.HostKeyAlgorithms.Keys);
        Assert.Contains("rsa-sha2-256", i.HostKeyAlgorithms.Keys);
        Assert.Contains("rsa-sha2-512", i.HostKeyAlgorithms.Keys);
        Assert.Contains("aes256-ctr", i.Encryptions.Keys);
        Assert.Contains("hmac-sha2-256", i.HmacAlgorithms.Keys);
        Assert.Equal(["none"], i.CompressionAlgorithms.Keys.ToArray());
    }

    static SshHostKey Key(string algo, string fp) => new() { Host = "bastion.example.test", Port = 22, Algorithm = algo, Fingerprint = fp };

    [Theory]
    [InlineData("ssh-rsa", "rsa-sha2-512")]   // a line trusted before the SHA-1 signature was dropped
    [InlineData("rsa-sha2-256", "rsa-sha2-512")]
    [InlineData("rsa-sha2-512", "ssh-rsa")]
    public void One_rsa_key_is_trusted_under_any_of_its_signature_names(string trustedAs, string offeredAs)
    {
        var k = new KnownHosts(Path.Combine(_dir, "known_hosts"));
        k.Trust(Key(trustedAs, "RSAFP"));
        Assert.Equal(HostKeyVerdict.Trusted, k.Check("bastion.example.test", 22, offeredAs, "RSAFP").Verdict);
        // Another RSA key is still a change, reported against the trusted one.
        Assert.Equal((HostKeyVerdict.Changed, "RSAFP"), k.Check("bastion.example.test", 22, offeredAs, "OTHER"));
        // Another key type with the same fingerprint text is not the same key.
        Assert.Equal(HostKeyVerdict.Changed, k.Check("bastion.example.test", 22, "ssh-ed25519", "RSAFP").Verdict);
    }

    [Fact]
    public void Known_hosts_lines_written_before_the_change_still_read()
    {
        var path = Path.Combine(_dir, "known_hosts");
        File.WriteAllLines(path, ["bastion.example.test 22 ssh-rsa OLDRSA", "other.example.test 2222 ssh-ed25519 ED"]);
        var k = new KnownHosts(path);
        Assert.Equal(HostKeyVerdict.Trusted, k.Check("bastion.example.test", 22, "rsa-sha2-256", "OLDRSA").Verdict);
        Assert.Equal(HostKeyVerdict.Trusted, k.Check("other.example.test", 2222, "ssh-ed25519", "ED").Verdict);
        Assert.Equal(HostKeyVerdict.Unknown, k.Check("third.example.test", 22, "ssh-ed25519", "ED").Verdict);
    }

    // ------------------------------------------------------------------ throwaway certificates and loopback servers

    static class Certificates
    {
        public static (X509Certificate2 Ca, string PemPath) Ca(string dir)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=Querithm Test CA", key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            var path = Path.Combine(dir, $"ca-{Guid.NewGuid():N}.pem");
            File.WriteAllText(path, ca.ExportCertificatePem());
            return (ca, path);
        }

        public static X509Certificate2 Issued(X509Certificate2 ca, string dns)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={dns}", key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(dns);
            req.CertificateExtensions.Add(san.Build());
            var serial = RandomNumberGenerator.GetBytes(8);
            using var pub = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), serial);
            using var withKey = pub.CopyWithPrivateKey(key);
            // SChannel needs a key it can reach: round-trip through PKCS#12.
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }
    }

    /// <summary>
    /// A PostgreSQL server for one connection: answers the SSLRequest with 'S', does TLS with <c>cert</c>, reads the
    /// startup message, sends Authentication(<c>auth</c>) (0: closes instead) and notes whether a PasswordMessage came back, then closes.
    /// </summary>
    sealed class FakePg : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
        readonly TaskCompletionSource<bool> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task _run = Task.CompletedTask;
        readonly MemoryStream _received = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public volatile bool SawStartupOverTls;
        public volatile bool SawPassword;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Trace = new();
        public Task<bool> SawStartup => _startup.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public string Received { get { lock (_received) return Encoding.UTF8.GetString(_received.ToArray()); } }

        public static FakePg Start(X509Certificate2 cert, int auth)
        {
            var f = new FakePg();
            f._listener.Start();
            f._run = Task.Run(() => f.Serve(cert, auth));
            return f;
        }

        async Task Serve(X509Certificate2 cert, int auth)
        {
            var ct = _cts.Token;
            try
            {
                // Npgsql may reconnect (a refused GSS encryption attempt): serve each connection until one reaches TLS.
                while (!ct.IsCancellationRequested && !SawStartupOverTls)
                {
                    using var client = await _listener.AcceptTcpClientAsync(ct);
                    Trace.Enqueue("accepted");
                    try { await ServeOne(client.GetStream(), cert, auth, ct); }
                    catch (Exception e) when (!ct.IsCancellationRequested && !SawStartupOverTls) { Trace.Enqueue(e.GetType().Name + ": " + e.Message); /* the client hung up, or refused the certificate */ }
                }
            }
            catch (Exception) { /* stopped */ }
            finally { _startup.TrySetResult(false); }
        }

        async Task ServeOne(NetworkStream ns, X509Certificate2 cert, int auth, CancellationToken ct)
        {
            {
                var head = new byte[8];
                while (true)
                {
                    await ns.ReadExactlyAsync(head, ct);
                    var code = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4));
                    Trace.Enqueue("request " + code);
                    if (code == 80877104) { await ns.WriteAsync("N"u8.ToArray(), ct); continue; } // GSSENCRequest: no
                    if (code != 80877103) return; // not an SSLRequest
                    break;
                }
                await ns.WriteAsync("S"u8.ToArray(), ct);
                await using var tls = new SslStream(ns, false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, ct);
                var len = new byte[4];
                await tls.ReadExactlyAsync(len, ct);
                var body = new byte[BinaryPrimitives.ReadInt32BigEndian(len) - 4];
                await tls.ReadExactlyAsync(body, ct);
                SawStartupOverTls = true;
                _startup.TrySetResult(true);
                if (auth == 0) return; // enough: the certificate was accepted
                var msg = new List<byte> { (byte)'R' };
                var payload = auth == 5 ? new byte[] { 0, 0, 0, 5, 1, 2, 3, 4 } : BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(auth));
                msg.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(4 + payload.Length)));
                msg.AddRange(payload);
                await tls.WriteAsync(msg.ToArray(), ct);
                await tls.FlushAsync(ct);
                var type = new byte[1];
                if (await tls.ReadAsync(type, ct) == 1 && type[0] == (byte)'p')
                {
                    SawPassword = true;
                    await tls.ReadExactlyAsync(len, ct);
                    var pw = new byte[BinaryPrimitives.ReadInt32BigEndian(len) - 4];
                    await tls.ReadExactlyAsync(pw, ct);
                    lock (_received) _received.Write(pw);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _run; } catch { /* stopped */ }
            _cts.Dispose();
        }
    }

    /// <summary>A MySQL server for one connection: a v10 handshake offering TLS, then TLS with <c>cert</c>, then it notes the login packet and closes.</summary>
    sealed class FakeMySql : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
        Task _run = Task.CompletedTask;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public volatile bool SawLoginOverTls;

        public static FakeMySql Start(X509Certificate2 cert)
        {
            var f = new FakeMySql();
            f._listener.Start();
            f._run = Task.Run(() => f.Serve(cert));
            return f;
        }

        async Task Serve(X509Certificate2 cert)
        {
            var ct = _cts.Token;
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                var ns = client.GetStream();
                await WritePacket(ns, 0, Handshake(), ct);
                await ReadPacket(ns, ct); // SSLRequest
                await using var tls = new SslStream(ns, false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, ct);
                await ReadPacket(tls, ct); // HandshakeResponse41, over TLS
                SawLoginOverTls = true;
            }
            catch (Exception) { /* the client hung up, or refused the certificate */ }
        }

        static byte[] Handshake()
        {
            var b = new List<byte> { 0x0a };
            b.AddRange("8.0.36\0"u8.ToArray());
            b.AddRange(BitConverter.GetBytes(7));
            b.AddRange("abcdefgh"u8.ToArray());
            b.Add(0);
            const uint caps = 0x1 | 0x8 | 0x200 | 0x800 | 0x2000 | 0x8000 | 0x80000; // ... CLIENT_SSL ...
            b.AddRange(BitConverter.GetBytes((ushort)(caps & 0xFFFF)));
            b.Add(0x21);
            b.AddRange(BitConverter.GetBytes((ushort)0x0002));
            b.AddRange(BitConverter.GetBytes((ushort)(caps >> 16)));
            b.Add(21);
            b.AddRange(new byte[10]);
            b.AddRange("ijklmnopqrst\0"u8.ToArray());
            b.AddRange("mysql_native_password\0"u8.ToArray());
            return [.. b];
        }

        static async Task WritePacket(Stream s, byte seq, byte[] payload, CancellationToken ct)
        {
            await s.WriteAsync(new byte[] { (byte)payload.Length, (byte)(payload.Length >> 8), (byte)(payload.Length >> 16), seq }, ct);
            await s.WriteAsync(payload, ct);
            await s.FlushAsync(ct);
        }

        static async Task ReadPacket(Stream s, CancellationToken ct)
        {
            var header = new byte[4];
            await s.ReadExactlyAsync(header, ct);
            await s.ReadExactlyAsync(new byte[header[0] | (header[1] << 8) | (header[2] << 16)], ct);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _run; } catch { /* stopped */ }
            _cts.Dispose();
        }
    }
}
