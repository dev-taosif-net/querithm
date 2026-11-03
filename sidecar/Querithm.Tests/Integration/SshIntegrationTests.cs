using System.Diagnostics;
using System.Net;
using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>SSH tunnels through the fixture bastion (scripts/bench/docker, port 22220) to both engines.</summary>
[Collection("ssh")]
public class SshIntegrationTests
{
    public static TheoryData<string> Engines => TestEngines.Names;

    static string KeyFile(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "scripts", "bench", "docker", "ssh", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(name);
    }

    static SshSettings Bastion(SshAuthKind auth = SshAuthKind.PrivateKey, string key = "test_ed25519") =>
        new() { Host = "127.0.0.1", Port = 22220, Username = "querithm", Auth = auth, KeyPath = auth == SshAuthKind.PrivateKey ? KeyFile(key) : null };

    /// <summary>The server as the bastion sees it (compose service names).</summary>
    static Profile Target(string engine, SshSettings ssh) => engine == "pg"
        ? new Profile { Id = "", Name = "PG via SSH", Engine = Engine.Pg, Host = "postgres", Port = 5432, Auth = AuthKind.Password, Username = "postgres", TlsMode = TlsMode.Prefer, DatabaseDefault = "postgres", Ssh = ssh }
        : new Profile { Id = "", Name = "MSSQL via SSH", Engine = Engine.Mssql, Host = "mssql", Port = 1433, Auth = AuthKind.Sql, Username = "sa", TlsMode = TlsMode.Mandatory, TrustCert = true, DatabaseDefault = "master", Ssh = ssh };

    static string Secret(string engine) => engine == "pg" ? TestHost.PgPassword : TestHost.MssqlPassword;

    /// <summary>Trusts whatever key the bastion shows (the tests' stand-in for the user comparing the fingerprint).</summary>
    static async Task TrustAsync(TestHost h, Profile p, string engine, string? sshSecret = null)
    {
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = Secret(engine), SshSecret = sshSecret });
        if (r.UntrustedHostKey is { } key) await h.Rpc.SshTrustHostKey(key);
    }

    static async Task<int> SelectOneAsync(TestHost h, string sid)
    {
        var (done, b, id) = await h.RunAsync(sid, "SELECT 1 AS one");
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)) + done.Error?.Message);
        return ((Int32Array)b[0].Column(0)).GetValue(0)!.Value;
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task An_unknown_host_key_is_refused_until_the_user_trusts_it(string engine)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(Target(engine, Bastion()));
        var test = await h.Rpc.ProfileTest(new ProfileTestParams { ProfileId = p.Id, Secret = Secret(engine) });
        Assert.False(test.Ok);
        Assert.NotNull(test.UntrustedHostKey);
        Assert.Contains("not trusted yet", test.Error);
        Assert.Contains(test.UntrustedHostKey!.Fingerprint, test.Error);

        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = Secret(engine) })).SessionId;
        var ex = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.SessionStateGet(new SessionRef { SessionId = sid }));
        Assert.Equal("sshHostKeyUnknown", ex.Code);

        await h.Rpc.SshTrustHostKey(test.UntrustedHostKey);
        var again = await h.Rpc.ProfileTest(new ProfileTestParams { ProfileId = p.Id, Secret = Secret(engine) });
        Assert.True(again.Ok, again.Error);
        Assert.Equal(1, await SelectOneAsync(h, sid));
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Password_key_and_passphrase_sign_ins(string engine)
    {
        await using var h = new TestHost();
        await TrustAsync(h, Target(engine, Bastion()), engine);
        async Task<ProfileTestResult> Test(SshSettings ssh, string? sshSecret) =>
            await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Target(engine, ssh), Secret = Secret(engine), SshSecret = sshSecret });

        Assert.True((await Test(Bastion(SshAuthKind.Password), FixtureSecrets.Password("ssh"))).Ok);
        Assert.Contains("SSH sign-in", (await Test(Bastion(SshAuthKind.Password), "wrong")).Error);
        Assert.True((await Test(Bastion(key: "test_rsa_pass"), FixtureSecrets.SshKeyPassphrase)).Ok);
        Assert.Contains("passphrase", (await Test(Bastion(key: "test_rsa_pass"), null)).Error);
        Assert.Contains("could not be used", (await Test(Bastion(key: "test_rsa_pass"), "not the passphrase")).Error);
        Assert.Contains("SSH agent", (await Test(Bastion(SshAuthKind.Agent), null)).Error);
    }

    [IntegrationFact]
    public async Task A_changed_host_key_is_refused()
    {
        await using var h = new TestHost();
        var p = Target("pg", Bastion());
        var first = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = TestHost.PgPassword });
        var real = first.UntrustedHostKey!;
        var earlier = real with { Fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
        // M2: ssh.trustHostKey takes only a key the bastion presented, so the webview cannot plant this one ...
        var refused = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.SshTrustHostKey(earlier));
        Assert.Equal("sshHostKeyNotPresented", refused.Code);
        // ... and the bastion cannot be made to change its key here: the registry gets the key it had "before", as a
        // trust given on an earlier connection left it (the write ssh.trustHostKey makes after that check).
        h.Services.Sessions.Tunnels!.KnownHosts.Trust(earlier);
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = TestHost.PgPassword });
        Assert.False(r.Ok);
        Assert.Contains("different key", r.Error);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", r.UntrustedHostKey!.PreviousFingerprint);
        Assert.Equal(real.Fingerprint, r.UntrustedHostKey.Fingerprint);
        // The changed key was presented, so the user may trust it now; then it connects.
        await h.Rpc.SshTrustHostKey(r.UntrustedHostKey);
        Assert.True((await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = TestHost.PgPassword })).Ok);
    }

    [IntegrationTheory(LinuxOnly = true), MemberData(nameof(Engines))]
    public async Task The_forward_is_loopback_only_and_drops_other_programs(string engine)
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(Target(engine, Bastion()));
        await TrustAsync(h, p, engine);
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = Secret(engine) })).SessionId;
        Assert.Equal(1, await SelectOneAsync(h, sid));
        var provider = h.Services.Sessions.Get(sid).Provider;
        var (host, port, _) = provider.TunnelTarget(p);
        var tunnel = await h.Services.Sessions.Tunnels!.EnsureAsync(p, null, host, port, CancellationToken.None);
        Assert.True(IPAddress.IsLoopback(tunnel.Local.Address));
        Assert.Equal(IPAddress.Loopback, tunnel.Local.Address);

        // Another program on this machine connects to the forward: it is closed before anything reaches the server.
        var before = tunnel.Refused;
        using var child = Process.Start(new ProcessStartInfo("/bin/bash", ["-c", $"exec 3<>/dev/tcp/127.0.0.1/{tunnel.Local.Port} && printf 'hello' >&3; sleep 2"])
        { UseShellExecute = false })!;
        var sw = Stopwatch.StartNew();
        while (tunnel.Refused == before && sw.ElapsedMilliseconds < 5000) await Task.Delay(50);
        child.Kill();
        Assert.Equal(before + 1, tunnel.Refused);
        // The sidecar's own driver connections still get through: a second pooled connection opens via the same forward.
        var pool = h.Services.Sessions.PoolFor(h.Services.Sessions.Get(sid));
        var a = await pool.BorrowAsync(null, null, CancellationToken.None);
        var b = await pool.BorrowAsync(null, null, CancellationToken.None);
        Assert.True(a.IsOpen && b.IsOpen);
        pool.Return(a);
        pool.Return(b);
        Assert.Equal(before + 1, tunnel.Refused);
    }

    [IntegrationFact]
    public async Task A_dropped_tunnel_is_reopened_on_the_next_connection()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(Target("pg", Bastion()));
        await TrustAsync(h, p, "pg");
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = TestHost.PgPassword })).SessionId;
        Assert.Equal(1, await SelectOneAsync(h, sid));
        var (host, port, _) = h.Services.Sessions.Get(sid).Provider.TunnelTarget(p);
        var tunnel = await h.Services.Sessions.Tunnels!.EnsureAsync(p, null, host, port, CancellationToken.None);
        tunnel.Dispose(); // the bastion went away
        // The pooled connection that used the old tunnel is dropped, not handed to the next query.
        Assert.Equal(1, await SelectOneAsync(h, sid));
        var reopened = await h.Services.Sessions.Tunnels.EnsureAsync(p, null, host, port, CancellationToken.None);
        Assert.NotSame(tunnel, reopened);
        Assert.True(reopened.Alive);
    }
}
