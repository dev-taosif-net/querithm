using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Querithm.Contract;
using Querithm.Core.Logging;
using Querithm.Core.Ssh;
using Querithm.Core.Store;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>SSH tunnels: host-key trust, the forward's peer check, and each engine's routing rules.</summary>
public sealed class SshTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qssh-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    static SshHostKey Key(string fp, string algo = "ssh-ed25519") => new() { Host = "bastion", Port = 22, Algorithm = algo, Fingerprint = fp };

    [Fact]
    public void Host_keys_are_unknown_until_trusted_and_a_change_is_flagged()
    {
        var k = new KnownHosts(Path.Combine(_dir, "known_hosts"));
        Assert.Equal(HostKeyVerdict.Unknown, k.Check("bastion", 22, "ssh-ed25519", "AAA").Verdict);
        k.Trust(Key("AAA"));
        Assert.Equal(HostKeyVerdict.Trusted, k.Check("BASTION", 22, "ssh-ed25519", "AAA").Verdict);
        Assert.Equal((HostKeyVerdict.Changed, "AAA"), k.Check("bastion", 22, "ssh-ed25519", "BBB"));
        // Another key type than the trusted one is a change too (fail closed), and another port is another host.
        Assert.Equal(HostKeyVerdict.Changed, k.Check("bastion", 22, "rsa-sha2-512", "CCC").Verdict);
        Assert.Equal(HostKeyVerdict.Unknown, k.Check("bastion", 2222, "ssh-ed25519", "AAA").Verdict);
        // Trusting the new key replaces the old one.
        k.Trust(Key("BBB"));
        Assert.Equal(HostKeyVerdict.Trusted, k.Check("bastion", 22, "ssh-ed25519", "BBB").Verdict);
        Assert.Equal(HostKeyVerdict.Changed, k.Check("bastion", 22, "ssh-ed25519", "AAA").Verdict);
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, "known_hosts")));
        Assert.Throws<Core.QuerithmException>(() => k.Trust(Key("A B")));
    }

    [Fact]
    public void Proc_net_tcp_addresses()
    {
        Assert.Equal("0100007F:1F90", PeerOwnership.ProcAddress(new IPEndPoint(IPAddress.Loopback, 8080)));
        Assert.Equal("0F02000A:0016", PeerOwnership.ProcAddress(new IPEndPoint(IPAddress.Parse("10.0.2.15"), 22)));
        Assert.Equal("0000000000000000FFFF00000100007F:1F90", PeerOwnership.ProcAddress6(new IPEndPoint(IPAddress.Loopback, 8080)));
    }

    [Fact]
    public async Task Only_connections_from_this_process_are_ours()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = (IPEndPoint)listener.LocalEndpoint;

        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            // Drivers may dial with an IPv4 socket or a dual-stack IPv6 one; both must be recognised.
            using var own = new TcpClient(family);
            if (family == AddressFamily.InterNetworkV6) own.Client.DualMode = true;
            await own.ConnectAsync(server);
            using var accepted = await listener.AcceptTcpClientAsync();
            Assert.True(PeerOwnership.IsOwnConnection((IPEndPoint)accepted.Client.RemoteEndPoint!, server), $"{family}");
        }

        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/bash")) return;
        // Another process on the same machine (bash's /dev/tcp) is not ours.
        using var child = Process.Start(new ProcessStartInfo("/bin/bash", ["-c", $"exec 3<>/dev/tcp/127.0.0.1/{server.Port}; sleep 5"]) { UseShellExecute = false })!;
        try
        {
            using var foreign = await listener.AcceptTcpClientAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
            Assert.False(PeerOwnership.IsOwnConnection((IPEndPoint)foreign.Client.RemoteEndPoint!, server));
        }
        finally { child.Kill(); }
    }

    static Profile Mssql(string host, int? port = null) => new() { Id = "p", Name = "n", Engine = Engine.Mssql, Host = host, Port = port };
    static Profile Pg(string host, TlsMode tls = TlsMode.Default) => new() { Id = "p", Name = "n", Engine = Engine.Pg, Host = host, TlsMode = tls };

    [Theory]
    [InlineData("db.internal", null, "db.internal", 1433)]
    [InlineData("tcp:db.internal,14330", null, "db.internal", 14330)]
    [InlineData("db.internal", 1500, "db.internal", 1500)]
    public void Sql_server_tunnel_targets(string host, int? port, string expectedHost, int expectedPort)
    {
        var (h, p, refusal) = new SqlServerProvider().TunnelTarget(Mssql(host, port));
        Assert.Equal((expectedHost, expectedPort, (string?)null), (h, p, refusal));
    }

    [Theory]
    [InlineData("db\\SQLEXPRESS", "named instance")]
    [InlineData("np:\\\\.\\pipe\\sql", "Named pipes")]
    public void Sql_server_targets_ssh_cannot_carry_are_refused_with_a_reason(string host, string reason) =>
        Assert.Contains(reason, new SqlServerProvider().TunnelTarget(Mssql(host)).Refusal);

    [Fact]
    public void Sql_server_keeps_checking_the_certificate_against_the_real_host()
    {
        var (routed, note) = new SqlServerProvider().RouteThrough(Mssql("db.internal", 1433) with { Options = new() { ["multiSubnetFailover"] = "true" } }, "127.0.0.1", 40001);
        Assert.Null(note);
        Assert.Equal(("127.0.0.1", 40001), (routed.Host, routed.Port));
        Assert.Equal("db.internal", routed.Options!["hostNameInCertificate"]);
        Assert.False(routed.Options.ContainsKey("multiSubnetFailover"));
        var cs = new SqlServerProvider().BuildConnectionString(routed, "x", "master");
        Assert.Contains("127.0.0.1,40001", cs);
        Assert.Contains("Host Name In Certificate=db.internal", cs);
    }

    [Theory]
    [InlineData("pg1,pg2", "failover list")]
    [InlineData("/var/run/postgresql", "Unix socket")]
    public void Postgres_targets_ssh_cannot_carry_are_refused_with_a_reason(string host, string reason) =>
        Assert.Contains(reason, new PostgresProvider().TunnelTarget(Pg(host)).Refusal);

    [Fact]
    public void Postgres_verify_full_keeps_checking_the_real_host_name_through_the_tunnel()
    {
        // PT5-4: the TLS target host stays db.internal (ProviderTlsHardeningTests has the handshake).
        var pg = new PostgresProvider();
        Assert.Equal(("db.internal", 5432, (string?)null), pg.TunnelTarget(Pg("db.internal")));
        var (routed, note) = pg.RouteThrough(Pg("db.internal", TlsMode.VerifyFull), "127.0.0.1", 40002);
        Assert.Equal((TlsMode.VerifyFull, "127.0.0.1", 40002), (routed.TlsMode, routed.Host, routed.Port));
        Assert.Null(note);
        Assert.Equal("db.internal", PostgresProvider.TunnelTlsHost(routed));
        Assert.Null(pg.RouteThrough(Pg("db.internal", TlsMode.Require), "127.0.0.1", 1).Note);
    }

    [Fact]
    public void Ssh_settings_round_trip_without_any_secret()
    {
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var saved = profiles.Save(Pg("db") with { Id = "", Ssh = new SshSettings { Host = "bastion", Port = 2222, Username = "me", Auth = SshAuthKind.PrivateKey, KeyPath = "/home/me/.ssh/id_ed25519" } });
        var loaded = profiles.Get(saved.Id)!;
        Assert.Equal(saved.Ssh, loaded.Ssh);
        foreach (var bad in new[] { "password", "passphrase", "secret", "key" })
            Assert.DoesNotContain(typeof(SshSettings).GetProperties(), pr => pr.Name.Equals(bad, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ssh_secrets_are_scrubbed_from_logs()
    {
        await using var h = new TestHost();
        h.Services.Sessions.RememberSshSecret("p", "ssh-only-s3cret-9f");
        Assert.DoesNotContain("ssh-only-s3cret-9f", LogScrubber.Scrub("auth failed for ssh-only-s3cret-9f"));
    }

    [Fact]
    public async Task Tunnel_errors_before_connecting_name_the_problem()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(Pg("db") with { Id = "", Ssh = new SshSettings { Host = "127.0.0.1", Port = 1, Username = "me", Auth = SshAuthKind.Agent } });
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { ProfileId = p.Id });
        Assert.False(r.Ok);
        Assert.Contains("SSH agent", r.Error);
        var named = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Mssql("db\\SQL") with { Ssh = new SshSettings { Host = "b", Username = "u" } } });
        Assert.Contains("named instance", named.Error);
    }

    [Fact]
    public async Task Missing_key_path_oversized_key_agent_or_missing_secret_fail_with_specific_codes()
    {
        var known = new KnownHosts(Path.Combine(_dir, "kh"));
        await using var mgr = new SshTunnelManager(known, Log.Null);

        var noKeyPath = Pg("db") with { Ssh = new SshSettings { Host = "bastion", Username = "me", Auth = SshAuthKind.PrivateKey, KeyPath = "" } };
        Assert.Equal("invalid", (await Assert.ThrowsAsync<Core.QuerithmException>(() => mgr.EnsureAsync(noKeyPath, null, "db", 5432, CancellationToken.None))).Code);

        var missingFile = Pg("db") with { Ssh = new SshSettings { Host = "bastion", Username = "me", Auth = SshAuthKind.PrivateKey, KeyPath = Path.Combine(_dir, "nonexistent_id_ed25519") } };
        Assert.Equal("sshKey", (await Assert.ThrowsAsync<Core.QuerithmException>(() => mgr.EnsureAsync(missingFile, null, "db", 5432, CancellationToken.None))).Code);

        var oversizedKey = Path.Combine(_dir, "huge_key");
        File.WriteAllBytes(oversizedKey, new byte[300 * 1024]);
        var huge = Pg("db") with { Ssh = new SshSettings { Host = "bastion", Username = "me", Auth = SshAuthKind.PrivateKey, KeyPath = oversizedKey } };
        Assert.Equal("sshKey", (await Assert.ThrowsAsync<Core.QuerithmException>(() => mgr.EnsureAsync(huge, null, "db", 5432, CancellationToken.None))).Code);

        var noPassword = Pg("db") with { Ssh = new SshSettings { Host = "bastion", Username = "me", Auth = SshAuthKind.Password } };
        Assert.Equal("sshNeedsSecret", (await Assert.ThrowsAsync<Core.QuerithmException>(() => mgr.EnsureAsync(noPassword, "", "db", 5432, CancellationToken.None))).Code);

        var agent = Pg("db") with { Ssh = new SshSettings { Host = "bastion", Username = "me", Auth = SshAuthKind.Agent } };
        Assert.Equal("sshAgent", (await Assert.ThrowsAsync<Core.QuerithmException>(() => mgr.EnsureAsync(agent, null, "db", 5432, CancellationToken.None))).Code);
    }

    [Fact]
    public void Known_hosts_ignores_malformed_lines_and_rejects_control_characters()
    {
        var file = Path.Combine(_dir, "known_hosts_comments");
        File.WriteAllText(file, "# comment line\n\nbastion 2222 ssh-ed25519 FP2222\nbastion 22 ssh-ed25519 FP22\n");
        var k = new KnownHosts(file);
        Assert.Equal(HostKeyVerdict.Trusted, k.Check("bastion", 2222, "ssh-ed25519", "FP2222").Verdict);
        Assert.Equal(HostKeyVerdict.Trusted, k.Check("bastion", 22, "ssh-ed25519", "FP22").Verdict);
        Assert.Throws<Core.QuerithmException>(() => k.Trust(new SshHostKey { Host = "bad\nhost", Port = 22, Algorithm = "ssh-ed25519", Fingerprint = "AAA" }));
        Assert.Throws<Core.QuerithmException>(() => k.Trust(new SshHostKey { Host = "bastion", Port = 22, Algorithm = "bad algo", Fingerprint = "AAA" }));
    }
}
