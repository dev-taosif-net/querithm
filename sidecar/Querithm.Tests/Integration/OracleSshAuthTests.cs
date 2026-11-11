using System.Diagnostics;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S07 (QUERITHM_IT=1; the Oracle and SSH fixtures up): Oracle through the fixture's bastion. The bastion reaches the
/// listener as <c>oracle</c> (the compose service) on the container ports, 1521 TCP and 2484 TCPS. The fixture's
/// certificate is self-signed for CN=localhost, so through the tunnel Verify CA connects and Verify full is refused: the
/// name is checked against <c>oracle</c>, the server's own name, not the forward's 127.0.0.1 (the passing Verify full path
/// is OracleTunnelTests', against a certificate issued for the name). A dropped tunnel is reopened, and a tunnel lost
/// mid-query ends the run with the reason. Single instance only: RAC SCAN redirects cannot be made on Oracle Free; the
/// redirect refusal is checked against the real driver following a fake listener's REDIRECT (OracleTunnelTests).
/// </summary>
[Collection("ssh")]
public sealed class OracleSshAuthTests
{
    static string AppPassword => FixtureSecrets.Password("oracle");

    static SshSettings Bastion() => new()
    {
        Host = "127.0.0.1", Port = 22220, Username = "querithm", Auth = SshAuthKind.PrivateKey,
        KeyPath = FixtureSecrets.FixtureFile(Path.Combine("ssh", "test_ed25519")),
    };

    static Profile ViaSsh(TlsMode tls = TlsMode.Disable, string? caPath = null) => new()
    {
        Id = "", Name = "Oracle via SSH", Engine = Engine.Oracle, Host = "oracle", Port = tls == TlsMode.Disable ? 1521 : 2484,
        Auth = AuthKind.Password, Username = OracleIntegrationTests.AppUser, DatabaseDefault = OracleIntegrationTests.Service,
        TlsMode = tls, CaPath = caPath, TimeoutS = 30, Ssh = Bastion(),
    };

    static TestHost NewHost() => new(new OracleProvider(Directory.CreateTempSubdirectory("qora-ssh-").FullName));

    /// <summary>Trusts whatever key the bastion shows (the tests' stand-in for the user comparing the fingerprint).</summary>
    static async Task TrustAsync(TestHost h, Profile p)
    {
        var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = AppPassword });
        if (r.UntrustedHostKey is { } key) await h.Rpc.SshTrustHostKey(key);
    }

    static async Task<string> ProtocolAsync(TestHost h, string sid)
    {
        var (done, batches, id) = await h.RunAsync(sid, "SELECT LOWER(SYS_CONTEXT('USERENV', 'NETWORK_PROTOCOL')) AS proto FROM dual");
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)) + done.Error?.Message);
        return ((Apache.Arrow.StringArray)batches[0].Column(0)).GetString(0);
    }

    static async Task<string> OpenAsync(TestHost h, Profile p)
    {
        await TrustAsync(h, p);
        var saved = await h.Rpc.ProfileSave(p);
        var test = await h.Rpc.ProfileTest(new ProfileTestParams { ProfileId = saved.Id, Secret = AppPassword });
        Assert.True(test.Ok, test.Error);
        return (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = AppPassword })).SessionId;
    }

    [IntegrationFact]
    public async Task Connects_over_tcp_through_the_bastion_and_a_dropped_tunnel_is_reopened()
    {
        await using var h = NewHost();
        var p = ViaSsh();
        var sid = await OpenAsync(h, p);
        Assert.Equal("tcp", await ProtocolAsync(h, sid));

        var (host, port, refusal) = h.Services.Sessions.Get(sid).Provider.TunnelTarget(p);
        Assert.Equal(("oracle", 1521, (string?)null), (host, port, refusal));
        var tunnel = await h.Services.Sessions.Tunnels!.EnsureAsync(h.Services.Sessions.Get(sid).Profile, null, host, port, CancellationToken.None);
        tunnel.Dispose(); // the bastion went away
        // The pooled connection that used the old tunnel is dropped, not handed to the next query.
        Assert.Equal("tcp", await ProtocolAsync(h, sid));
        var reopened = await h.Services.Sessions.Tunnels.EnsureAsync(h.Services.Sessions.Get(sid).Profile, null, host, port, CancellationToken.None);
        Assert.NotSame(tunnel, reopened);
        Assert.True(reopened.Alive);
    }

    [IntegrationFact]
    public async Task Tcps_through_the_bastion_checks_the_certificate_against_the_servers_own_name()
    {
        var dir = Directory.CreateTempSubdirectory("qora-ssh-ca-").FullName;
        try
        {
            var ca = await OracleProviderIntegrationTests.FixtureCaAsync(dir);
            await using var h = NewHost();
            // Verify CA: the chain is checked, the name is not; TCPS end to end through the forward.
            var sid = await OpenAsync(h, ViaSsh(TlsMode.VerifyCa, ca));
            Assert.Equal("tcps", await ProtocolAsync(h, sid));

            // Verify full: the certificate names localhost, the server is "oracle" — refused before the driver dials.
            var full = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = ViaSsh(TlsMode.VerifyFull, ca), Secret = AppPassword });
            Assert.False(full.Ok);
            Assert.Contains("not issued for oracle", full.Error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [IntegrationFact]
    public async Task A_tunnel_lost_mid_query_ends_the_run_with_the_reason_and_the_tab_runs_again()
    {
        await using var h = NewHost();
        var p = ViaSsh();
        var sid = await OpenAsync(h, p);
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "BEGIN DBMS_SESSION.SLEEP(20); END;" });
        var id = r.ExecutionId!;
        await Task.Delay(1500); // sleeping on the server, its connection inside the tunnel
        var (host, port, _) = h.Services.Sessions.Get(sid).Provider.TunnelTarget(p);
        var tunnel = await h.Services.Sessions.Tunnels!.EnsureAsync(h.Services.Sessions.Get(sid).Profile, null, host, port, CancellationToken.None);
        var since = Stopwatch.StartNew();
        tunnel.Dispose();

        var done = await h.Notifier.WaitDone(id, 30_000);
        Assert.True(since.Elapsed < TimeSpan.FromSeconds(15), $"ended after {since.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(ExecState.Failed, done.State);
        var said = string.Join("\n", h.Messages(id)) + done.Error?.Message;
        Assert.Contains("connection to the server was lost", said);
        Assert.Null(h.Services.Sessions.Get(sid).Pinned);
        Assert.Equal("tcp", await ProtocolAsync(h, sid)); // a new tunnel, a new connection
    }

    [IntegrationFact]
    public async Task A_profile_naming_the_tunnel_options_itself_is_refused()
    {
        await using var h = NewHost();
        var planted = ViaSsh() with { Options = new() { ["tunnelHost"] = "elsewhere", ["tunnelPort"] = "1521" } };
        // Core's option list refuses it first; the provider refuses it too (OracleTunnelTests), so neither layer alone holds it.
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ProfileSave(planted));
        Assert.Contains("tunnelHost", ex.Message);
    }
}
