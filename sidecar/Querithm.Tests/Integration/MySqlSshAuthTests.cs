using MySqlConnector;
using Querithm.Contract;
using static Querithm.Tests.Integration.MySqlIntegrationTests;
using MyHost = Querithm.Tests.Integration.MySqlIntegrationTests.Host;

namespace Querithm.Tests.Integration;

/// <summary>
/// Phase D2 (QUERITHM_IT=1): MySQL / MariaDB through the fixture's SSH bastion, tunnel loss, and the sign-in paths —
/// caching_sha2_password full authentication (needs TLS once the server's cache is empty) and MariaDB plugins the driver
/// does not ship.
/// </summary>
[Collection("ssh")]
public sealed class MySqlSshAuthTests
{
    static string KeyFile(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "scripts", "bench", "docker", "ssh", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(name);
    }

    static SshSettings Bastion() => new() { Host = "127.0.0.1", Port = 22220, Username = "querithm", Auth = SshAuthKind.PrivateKey, KeyPath = KeyFile("test_ed25519") };

    /// <summary>The server as the bastion sees it (compose service names, the container port).</summary>
    static Profile ViaSsh(string product) => ProfileFor(product) with
    {
        Name = product + " via SSH", Host = product switch { "mysql" => "mysql84", "mysql97" => "mysql97", _ => "mariadb118" }, Port = 3306, Ssh = Bastion(),
    };

    static async Task<string> SelectOneAsync(MyHost h, string sid)
    {
        var (done, batches, _) = await Run(h, sid, "SELECT 1 AS one");
        Assert.Null(done.Error);
        return Text(batches[0], "one")!;
    }

    static async Task ExecRootAsync(string product, string sql)
    {
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Connects_through_the_bastion_and_a_dropped_tunnel_is_reopened(string product)
    {
        await EnsureDatabaseAsync(product);
        await using var h = new MyHost();
        var p = await h.Rpc.ProfileSave(ViaSsh(product));
        var first = await h.Rpc.ProfileTest(new ProfileTestParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword });
        if (first.UntrustedHostKey is { } key) await h.Rpc.SshTrustHostKey(key);
        var test = await h.Rpc.ProfileTest(new ProfileTestParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword });
        Assert.True(test.Ok, test.Error);
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword })).SessionId;
        Assert.Equal("1", await SelectOneAsync(h, sid));

        var (host, port, refusal) = h.Services.Sessions.Get(sid).Provider.TunnelTarget(p);
        Assert.Null(refusal);
        var tunnel = await h.Services.Sessions.Tunnels!.EnsureAsync(p, null, host, port, CancellationToken.None);
        tunnel.Dispose(); // the bastion went away
        // The pooled connection that used the old tunnel is dropped, not handed to the next query.
        Assert.Equal("1", await SelectOneAsync(h, sid));
        var reopened = await h.Services.Sessions.Tunnels.EnsureAsync(p, null, host, port, CancellationToken.None);
        Assert.NotSame(tunnel, reopened);
        Assert.True(reopened.Alive);
    }

    [IntegrationFact]
    public async Task A_unix_socket_cannot_go_through_a_tunnel()
    {
        await using var h = new MyHost();
        var p = ViaSsh("mysql") with { Host = "/var/run/mysqld/mysqld.sock" };
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = TestHost.MySqlPassword });
        Assert.False(r.Ok);
        Assert.Contains("Unix socket", r.Error);
    }

    /// <summary>The container's own address on the Docker network: reachable from a Linux host, and not loopback to the driver.</summary>
    static string ContainerAddress(string container)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", ["inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", container])
        {
            RedirectStandardOutput = true,
        })!;
        var ip = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return ip;
    }

    [IntegrationTheory(LinuxOnly = true)]
    [InlineData("mysql", "querithm-fixture-mysql84-1")]
    [InlineData("mysql97", "querithm-fixture-mysql97-1")]
    public async Task Caching_sha2_full_authentication_needs_tls_once_the_cache_is_empty(string product, string container)
    {
        // MySqlConnector sends the password in clear to a loopback address (the plan allows it): the full-authentication
        // rule shows only against a non-loopback address, which Docker Desktop does not route.
        var address = ContainerAddress(container);
        Assert.False(string.IsNullOrEmpty(address), $"no address for {container}");
        await EnsureDatabaseAsync(product);
        await ExecRootAsync(product, $"""
            DROP USER IF EXISTS 'q_sha2'@'%';
            CREATE USER 'q_sha2'@'%' IDENTIFIED WITH caching_sha2_password BY 'q_sha2_fixture';
            GRANT SELECT ON {Db(product)}.* TO 'q_sha2'@'%';
            FLUSH PRIVILEGES;
            """);
        await using var h = new MyHost();
        // The server sees the Docker gateway, not loopback: with an empty cache and no TLS, the password cannot be sent.
        async Task<ProfileTestResult> Test(TlsMode tls) =>
            await h.Rpc.ProfileTest(new ProfileTestParams { Profile = ProfileFor(product, tls) with { Host = address, Port = 3306, Username = "q_sha2" }, Secret = "q_sha2_fixture" });
        var plain = await Test(TlsMode.Disable);
        Assert.False(plain.Ok);
        Assert.Contains("caching_sha2_password", plain.Error);
        Assert.Contains("TLS", plain.Error);
        Assert.True((await Test(TlsMode.Require)).Ok); // full authentication over TLS fills the server's cache
        // Now a plain connection succeeds (a fast-auth cache hit): why such failures look intermittent.
        Assert.True((await Test(TlsMode.Disable)).Ok);
        await ExecRootAsync(product, "FLUSH PRIVILEGES");
        Assert.False((await Test(TlsMode.Disable)).Ok);
    }

    [IntegrationFact]
    public async Task A_mariadb_plugin_the_driver_does_not_ship_says_so()
    {
        await EnsureDatabaseAsync("mariadb");
        try { await ExecRootAsync("mariadb", "INSTALL SONAME 'auth_ed25519'"); }
        catch (MySqlException e) when (e.Message.Contains("already", StringComparison.OrdinalIgnoreCase)) { /* installed by an earlier run */ }
        await ExecRootAsync("mariadb", """
            DROP USER IF EXISTS 'q_ed'@'%';
            CREATE USER 'q_ed'@'%' IDENTIFIED VIA ed25519 USING PASSWORD('q_ed_fixture');
            """);
        await using var h = new MyHost();
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = ProfileFor("mariadb") with { Username = "q_ed" }, Secret = "q_ed_fixture" });
        Assert.False(r.Ok);
        Assert.Contains("ed25519", r.Error);
        Assert.Contains("not supported", r.Error);
    }
}
