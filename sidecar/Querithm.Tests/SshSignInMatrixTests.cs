using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Logging;
using Querithm.Core.Ssh;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// SSH tunnel sign-in matrix without a bastion: each auth kind is driven up to the TCP dial of a closed loopback port,
/// so a "could not reach" error proves the credentials were accepted locally (key read, passphrase right) and every
/// earlier failure names its own problem. Also: which secret field the sidecar uses, host-key trust (M2), and the Host
/// field "as the bastion sees it" for each server engine. SQLite and DuckDB refuse a tunnel.
/// </summary>
public sealed class SshSignInMatrixTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qsshm-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    /// <summary>A loopback port that refuses connections (bound, then released).</summary>
    static int ClosedPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static Profile Pg(SshSettings ssh) => new() { Id = "p", Name = "n", Engine = Engine.Pg, Host = "db.internal", TimeoutS = 3, Ssh = ssh };

    SshSettings Ssh(SshAuthKind auth, string? keyPath = null) => new() { Host = "127.0.0.1", Port = ClosedPort(), Username = "me", Auth = auth, KeyPath = keyPath };

    string RsaPem()
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(_dir, $"id_rsa_{Guid.NewGuid():N}");
        File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        return path;
    }

    string RsaPkcs8Encrypted(string passphrase)
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(_dir, $"id_rsa_enc_{Guid.NewGuid():N}");
        File.WriteAllText(path, rsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
        return path;
    }

    /// <summary>An OpenSSH-format ed25519 key from ssh-keygen when this machine has one (null otherwise).</summary>
    string? OpenSshKey(string passphrase)
    {
        var path = Path.Combine(_dir, $"id_ed25519_{Guid.NewGuid():N}");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ssh-keygen", ["-q", "-t", "ed25519", "-N", passphrase, "-C", "test", "-f", path])
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true });
            if (proc == null || !proc.WaitForExit(20_000) || proc.ExitCode != 0) return null;
            return File.Exists(path) ? path : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    async Task<QuerithmException> Open(Profile p, string? secret)
    {
        await using var mgr = new SshTunnelManager(new KnownHosts(Path.Combine(_dir, "kh")), Log.Null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await Assert.ThrowsAsync<QuerithmException>(() => mgr.EnsureAsync(p, secret, "db.internal", 5432, cts.Token));
    }

    // ---- key file, no passphrase ----

    [Fact]
    public async Task Key_file_without_passphrase_is_read_and_the_tunnel_dials_the_bastion()
    {
        var ex = await Open(Pg(Ssh(SshAuthKind.PrivateKey, RsaPem())), null);
        Assert.Equal("sshConnect", ex.Code);
        Assert.Contains("Could not reach the SSH server 127.0.0.1", ex.Message);
    }

    [Fact]
    public async Task Key_file_without_passphrase_ignores_a_passphrase_typed_anyway()
    {
        var ex = await Open(Pg(Ssh(SshAuthKind.PrivateKey, RsaPem())), "typed-by-mistake");
        Assert.Equal("sshConnect", ex.Code);
    }

    [Fact]
    public async Task Relative_key_path_is_refused_before_anything_is_read()
    {
        var ex = await Open(Pg(Ssh(SshAuthKind.PrivateKey, "id_rsa")), null);
        Assert.Equal("invalid", ex.Code);
        Assert.Contains("absolute path", ex.Message);
    }

    [Fact]
    public async Task A_file_that_is_not_a_key_is_named_without_echoing_its_content()
    {
        var path = Path.Combine(_dir, "notakey");
        File.WriteAllText(path, "hello-not-a-key-content-77");
        var ex = await Open(Pg(Ssh(SshAuthKind.PrivateKey, path)), null);
        Assert.Equal("sshKey", ex.Code);
        Assert.Contains("notakey", ex.Message);
        Assert.DoesNotContain("hello-not-a-key-content-77", ex.Message);
        Assert.DoesNotContain(_dir, ex.Message); // only the file name, not the full path
    }

    // ---- key file with passphrase ----

    [Fact]
    public async Task Encrypted_key_with_the_right_passphrase_dials_the_bastion()
    {
        var ex = await Open(Pg(Ssh(SshAuthKind.PrivateKey, RsaPkcs8Encrypted("s3cret-phrase"))), "s3cret-phrase");
        Assert.Equal("sshConnect", ex.Code);
    }

    // Regression: an encrypted PKCS#8 or legacy PEM key with a missing or wrong passphrase let the cipher's own error
    // escape LoadKey ("pad block corrupted", "Padding is invalid and cannot be removed.").

    [Fact]
    public async Task Encrypted_pkcs8_key_without_a_passphrase_asks_for_it()
    {
        var key = RsaPkcs8Encrypted("s3cret-phrase");
        var ex = await Open(Pg(Ssh(SshAuthKind.PrivateKey, key)), null);
        Assert.Equal("sshNeedsSecret", ex.Code);
        Assert.Equal($"The SSH key {Path.GetFileName(key)} is protected by a passphrase. Enter it to connect.", ex.Message);
    }

    [Fact]
    public async Task Encrypted_pkcs8_key_with_a_wrong_passphrase_names_the_key_not_the_cipher()
    {
        var key = RsaPkcs8Encrypted("s3cret-phrase");
        var ex = await Open(Pg(Ssh(SshAuthKind.PrivateKey, key)), "wrong-phrase-42");
        Assert.Equal("sshKey", ex.Code);
        Assert.StartsWith($"The SSH key {Path.GetFileName(key)} could not be used: the passphrase is wrong", ex.Message);
        Assert.DoesNotContain("pad block", ex.Message);
        Assert.DoesNotContain("wrong-phrase-42", ex.Message);
        Assert.DoesNotContain(_dir, ex.Message);
    }

    /// <summary>"ssh-keygen -m PEM": a legacy "Proc-Type: 4,ENCRYPTED" key, when this machine has ssh-keygen.</summary>
    string? LegacyPemKey(string passphrase)
    {
        var path = Path.Combine(_dir, $"id_rsa_pem_{Guid.NewGuid():N}");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ssh-keygen", ["-q", "-t", "rsa", "-b", "2048", "-m", "PEM", "-N", passphrase, "-C", "test", "-f", path])
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true });
            if (proc == null || !proc.WaitForExit(20_000) || proc.ExitCode != 0) return null;
            return File.Exists(path) ? path : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    [Fact]
    public async Task Legacy_pem_key_follows_the_same_rules_when_ssh_keygen_is_available()
    {
        var key = LegacyPemKey("s3cret-phrase");
        if (key == null) return; // no ssh-keygen on this machine: the PKCS#8 cases above still run
        Assert.Equal("sshConnect", (await Open(Pg(Ssh(SshAuthKind.PrivateKey, key)), "s3cret-phrase")).Code);
        Assert.Equal("sshNeedsSecret", (await Open(Pg(Ssh(SshAuthKind.PrivateKey, key)), null)).Code);
        var wrong = await Open(Pg(Ssh(SshAuthKind.PrivateKey, key)), "wrong-phrase-42");
        Assert.Equal("sshKey", wrong.Code);
        Assert.StartsWith($"The SSH key {Path.GetFileName(key)} could not be used: the passphrase is wrong", wrong.Message);
        Assert.DoesNotContain("Padding", wrong.Message);
    }

    [Fact]
    public async Task OpenSsh_format_keys_follow_the_same_rules_when_ssh_keygen_is_available()
    {
        var plain = OpenSshKey("");
        var locked = OpenSshKey("s3cret-phrase");
        if (plain == null || locked == null) return; // no ssh-keygen on this machine: the PEM/PKCS#8 cases above still run
        Assert.Equal("sshConnect", (await Open(Pg(Ssh(SshAuthKind.PrivateKey, plain)), null)).Code);
        Assert.Equal("sshConnect", (await Open(Pg(Ssh(SshAuthKind.PrivateKey, locked)), "s3cret-phrase")).Code);
        var asked = await Open(Pg(Ssh(SshAuthKind.PrivateKey, locked)), null);
        Assert.Equal("sshNeedsSecret", asked.Code);
        Assert.Contains("protected by a passphrase", asked.Message);
        var wrong = await Open(Pg(Ssh(SshAuthKind.PrivateKey, locked)), "wrong-phrase-42");
        Assert.Equal("sshKey", wrong.Code);
        Assert.StartsWith($"The SSH key {Path.GetFileName(locked)} could not be used", wrong.Message);
        Assert.DoesNotContain("wrong-phrase-42", wrong.Message);
        Assert.DoesNotContain("s3cret-phrase", wrong.Message);
    }

    // ---- password ----

    [Fact]
    public async Task Password_sign_in_dials_the_bastion_with_the_password()
    {
        var ex = await Open(Pg(Ssh(SshAuthKind.Password)), "pw");
        Assert.Equal("sshConnect", ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Password_sign_in_without_a_password_asks_for_it(string? secret)
    {
        var ex = await Open(Pg(Ssh(SshAuthKind.Password)), secret);
        Assert.Equal("sshNeedsSecret", ex.Code);
        Assert.Contains("SSH password is not loaded", ex.Message);
    }

    // ---- agent: unsupported ----

    [Fact]
    public async Task Agent_sign_in_is_refused_with_a_reason_and_dials_nothing()
    {
        var ex = await Open(Pg(Ssh(SshAuthKind.Agent)), "pw");
        Assert.Equal("sshAgent", ex.Code);
        Assert.Contains("not supported yet", ex.Message);
    }

    // ---- which secret field the sidecar uses ----

    [Fact]
    public async Task Profile_test_uses_sshSecret_for_the_tunnel_never_the_database_secret()
    {
        await using var h = new TestHost();
        var p = Pg(Ssh(SshAuthKind.Password));
        var dbOnly = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = "db-pw" });
        Assert.False(dbOnly.Ok);
        Assert.Contains("SSH password is not loaded", dbOnly.Error);

        var sshOnly = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, SshSecret = "ssh-pw" });
        Assert.False(sshOnly.Ok);
        Assert.Contains("Could not reach the SSH server", sshOnly.Error);
    }

    [Fact]
    public async Task Profile_test_uses_sshSecret_as_the_key_passphrase()
    {
        await using var h = new TestHost();
        var right = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Pg(Ssh(SshAuthKind.PrivateKey, RsaPkcs8Encrypted("s3cret-phrase"))), Secret = "db-pw", SshSecret = "s3cret-phrase" });
        Assert.Contains("Could not reach the SSH server", right.Error);
        var locked = OpenSshKey("s3cret-phrase");
        if (locked == null) return; // no ssh-keygen on this machine
        var p = Pg(Ssh(SshAuthKind.PrivateKey, locked));
        var dbOnly = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = "s3cret-phrase" });
        Assert.Contains("protected by a passphrase", dbOnly.Error);
        var wrong = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, SshSecret = "wrong-phrase-42" });
        Assert.Contains("could not be used", wrong.Error);
        Assert.DoesNotContain("wrong-phrase-42", wrong.Error);
    }

    [Fact]
    public async Task Ssh_and_database_secrets_are_kept_apart_per_profile()
    {
        await using var h = new TestHost();
        h.Services.Sessions.RememberSshSecret("p", "ssh-only-pw");
        Assert.Equal("ssh-only-pw", h.Services.Sessions.SshSecretFor("p"));
        Assert.NotEqual("ssh-only-pw", h.Services.Sessions.SecretFor("p"));
        Assert.Null(h.Services.Sessions.SshSecretFor("other"));
        Assert.DoesNotContain("ssh-only-pw", LogScrubber.Scrub("failed: ssh-only-pw"));
    }

    // ---- host-key trust (M2) ----

    [Fact]
    public async Task Declining_trusts_nothing_and_a_key_never_presented_cannot_be_trusted()
    {
        await using var h = new TestHost();
        var key = new SshHostKey { Host = "127.0.0.1", Port = 22, Algorithm = "ssh-ed25519", Fingerprint = "AAAAfake" };
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SshTrustHostKey(key));
        Assert.Equal("sshHostKeyNotPresented", ex.Code);
        Assert.Equal(HostKeyVerdict.Unknown, h.Services.Sessions.Tunnels!.KnownHosts.Check("127.0.0.1", 22, "ssh-ed25519", "AAAAfake").Verdict);
    }

    [Fact]
    public async Task Accepting_a_presented_key_trusts_exactly_that_key_once()
    {
        await using var h = new TestHost();
        var tunnels = h.Services.Sessions.Tunnels!;
        var key = new SshHostKey { Host = "bastion", Port = 22, Algorithm = "ssh-ed25519", Fingerprint = "AAAApresented" };
        tunnels.Presented.Record(key);
        // Another fingerprint for the same host is not the presented key.
        await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SshTrustHostKey(key with { Fingerprint = "AAAAother" }));
        await h.Rpc.SshTrustHostKey(key);
        Assert.Equal(HostKeyVerdict.Trusted, tunnels.KnownHosts.Check("bastion", 22, "ssh-ed25519", "AAAApresented").Verdict);
        // The entry is used up: replaying the same call is refused.
        await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.SshTrustHostKey(key));
    }

    // ---- Host "as the bastion sees it", per server engine ----

    public static TheoryData<string> ServerEngines => new() { "mssql", "pg", "mySql" };

    static IDbProvider Provider(string e) => e switch
    {
        "mssql" => new SqlServerProvider(),
        "pg" => new PostgresProvider(),
        _ => new MySqlProvider(),
    };

    static Profile ServerProfile(string e, string? host) => new()
    {
        Id = "p", Name = "n", Engine = e switch { "mssql" => Engine.Mssql, "pg" => Engine.Pg, _ => Engine.MySql }, Host = host,
        Auth = e == "mssql" ? AuthKind.Sql : AuthKind.Password, Ssh = new SshSettings { Host = "bastion", Username = "u" },
    };

    [Theory]
    [MemberData(nameof(ServerEngines))]
    public void Host_is_the_target_as_the_bastion_sees_it_and_the_driver_dials_the_local_forward(string e)
    {
        var provider = Provider(e);
        var dflt = e switch { "mssql" => 1433, "pg" => 5432, _ => 3306 };
        // "localhost" means the bastion's own loopback: it is forwarded as is, not resolved here.
        Assert.Equal(("localhost", dflt, (string?)null), provider.TunnelTarget(ServerProfile(e, "localhost")));
        Assert.Equal(("db.internal", dflt, (string?)null), provider.TunnelTarget(ServerProfile(e, "db.internal")));
        // No host is the engine's default, localhost on the bastion.
        Assert.Equal("localhost", provider.TunnelTarget(ServerProfile(e, null)).Host);
        var (routed, _) = provider.RouteThrough(ServerProfile(e, "db.internal"), "127.0.0.1", 40123);
        Assert.Equal(("127.0.0.1", 40123), (routed.Host, routed.Port));
    }

    [Fact]
    public void MySql_unix_socket_cannot_go_through_a_tunnel()
    {
        Assert.Contains("Unix socket", new MySqlProvider().TunnelTarget(ServerProfile("mySql", "/var/run/mysqld/mysqld.sock")).Refusal);
    }

    [Fact]
    public void MySql_verify_full_keeps_checking_the_real_host_name_through_the_tunnel()
    {
        // PT5-4: no drop to VerifyCA; the certificate is checked for db.internal (ProviderTlsHardeningTests has the handshake).
        var (routed, note) = new MySqlProvider().RouteThrough(ServerProfile("mySql", "db.internal"), "127.0.0.1", 1);
        Assert.Equal(TlsMode.Default, routed.TlsMode);
        Assert.Null(note);
        Assert.Equal("db.internal", MySqlProvider.TunnelTlsHost(routed));
    }

    [Fact]
    public async Task MySql_profile_test_goes_through_the_tunnel_with_sshSecret()
    {
        await using var h = new TestHost(new MySqlProvider());
        var p = ServerProfile("mySql", "db.internal") with { Ssh = Ssh(SshAuthKind.Password), TimeoutS = 3 };
        Assert.Contains("SSH password is not loaded", (await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = "db-pw" })).Error);
        Assert.Contains("Could not reach the SSH server", (await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, SshSecret = "ssh-pw" })).Error);
    }

    [Fact]
    public async Task Sql_server_profile_test_goes_through_the_tunnel_with_sshSecret()
    {
        await using var h = new TestHost();
        var p = ServerProfile("mssql", "db.internal") with { Ssh = Ssh(SshAuthKind.Password), TimeoutS = 3 };
        Assert.Contains("SSH password is not loaded", (await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, Secret = "db-pw" })).Error);
        Assert.Contains("Could not reach the SSH server", (await h.Rpc.ProfileTest(new ProfileTestParams { Profile = p, SshSecret = "ssh-pw" })).Error);
    }

    [Fact]
    public async Task File_engines_refuse_a_tunnel_with_a_reason()
    {
        await using var h = new TestHost(new SqliteProvider(), new DuckDbProvider(_dir));
        foreach (var engine in new[] { Engine.Sqlite, Engine.DuckDb })
        {
            var memory = new Profile { Id = "p", Name = "n", Engine = engine, Location = LocationKind.Memory, Auth = AuthKind.None, Ssh = new SshSettings { Host = "b", Username = "u" } };
            var r = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = memory });
            Assert.False(r.Ok);
            Assert.Contains("cannot go through an SSH tunnel", r.Error);
        }
    }

    [Fact]
    public async Task Bastion_host_and_port_are_validated_at_the_rpc_boundary()
    {
        await using var h = new TestHost();
        var badHost = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Pg(new SshSettings { Host = "bad\nhost", Username = "u" }) });
        Assert.Contains("SSH host", badHost.Error);
        var badPort = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = Pg(new SshSettings { Host = "b", Port = 70000, Username = "u" }) });
        Assert.Contains("SSH port", badPort.Error);
    }
}
