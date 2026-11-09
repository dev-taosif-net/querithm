using Microsoft.Data.SqlClient;
using Querithm.Contract;
using Querithm.Core.Logging;
using Querithm.Core.Sessions;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// SQL Server sign-in matrix (connection-page verification): what each AuthKind turns into on the driver's connection
/// string, which kinds need a secret, and that the secret stays out of the connection string where it is not used and
/// out of errors and logs. A real login is not tried here (no server); the failure-path tests dial a closed local port.
/// TLS keywords are covered elsewhere and are not asserted here.
/// </summary>
public class SignInMatrixSqlServerTests
{
    const string Secret = "s3cret-fake-pw";
    static readonly SqlServerProvider Provider = new();

    static Profile P(AuthKind auth, string? user = "app") => new()
    {
        Id = "p1", Name = "x", Engine = Engine.Mssql, Host = "db.local", Auth = auth, Username = user, Environment = EnvironmentKind.Dev, TimeoutS = 15,
    };

    static SqlConnectionStringBuilder Built(AuthKind auth, string? secret = Secret, string? user = "app") =>
        new(Provider.BuildConnectionString(P(auth, user), secret, null));

    [Fact]
    public void Sql_login_sets_user_and_password_only()
    {
        var b = Built(AuthKind.Sql);
        Assert.Equal("app", b.UserID);
        Assert.Equal(Secret, b.Password);
        Assert.False(b.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
    }

    [Fact]
    public void Password_is_the_same_sql_login()
    {
        var b = Built(AuthKind.Password);
        Assert.Equal("app", b.UserID);
        Assert.Equal(Secret, b.Password);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
    }

    [Fact]
    public void Sql_login_without_a_secret_or_user_sends_empty_values()
    {
        var b = Built(AuthKind.Sql, secret: null, user: null);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.Password);
    }

    [Theory]
    [InlineData(AuthKind.Windows)]
    [InlineData(AuthKind.Kerberos)]
    public void Windows_and_kerberos_use_integrated_security_and_drop_user_and_secret(AuthKind auth)
    {
        var b = Built(auth);
        Assert.True(b.IntegratedSecurity);
        Assert.Equal("", b.UserID); // the editor shows a User name field for Kerberos; SqlClient ignores it (SSPI)
        Assert.Equal("", b.Password);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
        Assert.DoesNotContain(Secret, b.ConnectionString);
    }

    [Fact]
    public void Entra_interactive_uses_the_browser_flow_with_the_user_as_a_hint_and_no_secret()
    {
        var b = Built(AuthKind.EntraInteractive);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, b.Authentication);
        Assert.Equal("app", b.UserID);
        Assert.Equal("", b.Password);
        Assert.False(b.IntegratedSecurity);
        Assert.DoesNotContain(Secret, b.ConnectionString);
        Assert.Equal("", Built(AuthKind.EntraInteractive, user: null).UserID);
    }

    [Fact]
    public void Entra_device_code_sets_the_device_flow_and_no_user_or_secret()
    {
        var b = Built(AuthKind.EntraDeviceCode);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow, b.Authentication);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.Password);
        Assert.DoesNotContain(Secret, b.ConnectionString);
    }

    [Fact]
    public void Entra_token_keeps_the_token_off_the_connection_string()
    {
        // The token is set on SqlConnection.AccessToken, which SqlClient refuses beside User ID/Password/Integrated
        // Security/Authentication: none of them may be on the string.
        var b = Built(AuthKind.EntraToken);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.Password);
        Assert.False(b.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
        Assert.DoesNotContain(Secret, b.ConnectionString);
    }

    [Fact]
    public void Entra_token_string_is_one_SqlClient_accepts_an_access_token_on()
    {
        using var conn = new SqlConnection(Provider.BuildConnectionString(P(AuthKind.EntraToken), Secret, null));
        conn.AccessToken = Secret; // throws InvalidOperationException if the string carried a conflicting sign-in
        Assert.Equal(Secret, conn.AccessToken);
    }

    [Fact]
    public void No_sign_in_is_refused_for_sql_server()
    {
        Assert.NotNull(Provider.ValidateProfile(P(AuthKind.None)));
        foreach (var a in Provider.Traits.AuthKinds) Assert.Null(Provider.ValidateProfile(P(a)));
    }

    [Fact]
    public void The_six_kinds_are_offered_sql_login_first()
    {
        Assert.Equal(
            [AuthKind.Sql, AuthKind.Windows, AuthKind.Kerberos, AuthKind.EntraInteractive, AuthKind.EntraDeviceCode, AuthKind.EntraToken],
            Provider.Traits.AuthKinds);
        Assert.Equal(AuthKind.Sql, Provider.Traits.DefaultAuth);
    }

    // Mirrors NEEDS_SECRET in src/connections/auth.ts; a change on either side must change this table too.
    [Theory]
    [InlineData(AuthKind.Sql, true)]
    [InlineData(AuthKind.Password, true)]
    [InlineData(AuthKind.EntraToken, true)]
    [InlineData(AuthKind.Windows, false)]
    [InlineData(AuthKind.Kerberos, false)]
    [InlineData(AuthKind.EntraInteractive, false)]
    [InlineData(AuthKind.EntraDeviceCode, false)]
    [InlineData(AuthKind.None, false)]
    public void Needs_secret_matches_the_webview(AuthKind auth, bool needs) => Assert.Equal(needs, ProfileRules.NeedsSecret(auth));

    [Fact]
    public void Needs_secret_table_in_auth_ts_says_the_same()
    {
        var ts = File.ReadAllText(Path.Combine(RepoRoot(), "src", "connections", "auth.ts"));
        foreach (var a in Enum.GetValues<AuthKind>())
        {
            var key = char.ToLowerInvariant(a.ToString()[0]) + a.ToString()[1..];
            var expected = ProfileRules.NeedsSecret(a) ? "true" : "false";
            Assert.Contains($"  {key}: {expected},", ts);
        }
    }

    [Theory]
    [InlineData(AuthKind.EntraInteractive, true)]
    [InlineData(AuthKind.EntraDeviceCode, true)]
    [InlineData(AuthKind.Sql, false)]
    [InlineData(AuthKind.Windows, false)]
    [InlineData(AuthKind.Kerberos, false)]
    [InlineData(AuthKind.EntraToken, false)]
    public void Only_the_browser_and_device_code_flows_are_interactive(AuthKind auth, bool interactive) =>
        Assert.Equal(interactive, ProfileRules.Interactive(auth));

    [Theory]
    [InlineData("s3cret-fake-pw")]
    [InlineData("p;w=fake")]
    [InlineData("fa'ke\"pw")]
    [InlineData("{fake}pw")]
    public void A_built_connection_string_is_scrubbed_of_its_password(string pw)
    {
        var cs = Provider.BuildConnectionString(P(AuthKind.Sql), pw, null);
        var s = LogScrubber.Scrub(cs);
        Assert.DoesNotContain(pw, s);
        Assert.Contains("Password=***", s);
    }

    // SqlConnectionStringBuilder quotes a value with a space, so the keyword pattern masks it whole; RegisterSecret (called
    // by profile.test, RememberSecret and parseConnectionString) masks it anywhere else it appears.
    [Fact]
    public void A_password_with_a_space_is_masked()
    {
        const string pw = "fake pass phrase-91";
        var cs = Provider.BuildConnectionString(P(AuthKind.Sql), pw, null);
        Assert.DoesNotContain("phrase-91", LogScrubber.Scrub(cs));
        LogScrubber.RegisterSecret(pw);
        Assert.DoesNotContain(pw, LogScrubber.Scrub($"login failed for '{pw}'"));
    }

    [Fact]
    public void An_access_token_is_scrubbed_from_log_text_and_json()
    {
        Assert.DoesNotContain("tok-fake-123", LogScrubber.Scrub("Access Token=tok-fake-123;Data Source=x"));
        Assert.DoesNotContain("tok-fake-123", LogScrubber.Scrub("{\"accessToken\":\"tok-fake-123\"}"));
        Assert.DoesNotContain("tok-fake-123", LogScrubber.Scrub("{\"secret\":\"tok-fake-123\"}"));
        Assert.DoesNotContain("tok-fake-123", LogScrubber.Scrub("Authorization: Bearer tok-fake-123"));
    }

    // Failure path: profile.test against a closed local port (no server needed). The error the user sees carries no secret.
    [Theory]
    [InlineData(AuthKind.Sql)]
    [InlineData(AuthKind.EntraToken)]
    public async Task A_failed_test_reports_no_secret(AuthKind auth)
    {
        await using var host = new TestHost();
        var secret = $"zz-fake-{auth}-4471";
        var profile = P(auth) with { Host = "127.0.0.1", Port = 1, TimeoutS = 2 };
        var r = await host.Rpc.ProfileTest(new ProfileTestParams { Profile = profile, Secret = secret });
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.DoesNotContain(secret, r.Error!);
        Assert.DoesNotContain(secret, string.Join(" ", r.Notes ?? []));
    }

    // ---- regressions: an unreachable server (no server needed: a closed loopback port, and a TEST-NET address) ----

    static int ClosedPort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // Before: Strict failed after the full connect timeout, then Mandatory ran for another full timeout (about 2x), the
    // error was SqlClient's generic network paragraph, and the failure marked the profile "Strict unsupported" for good.
    [Fact]
    public async Task Unreachable_port_fails_within_the_timeout_names_the_endpoint_and_keeps_strict()
    {
        await using var host = new TestHost();
        var port = ClosedPort();
        var profile = P(AuthKind.Sql) with { Id = "p-unreachable-" + Guid.NewGuid().ToString("N"), Host = "127.0.0.1", Port = port, TimeoutS = 3 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await host.Rpc.ProfileTest(new ProfileTestParams { Profile = profile, Secret = Secret });
        sw.Stop();
        Assert.False(r.Ok);
        Assert.StartsWith($"Could not reach SQL Server at 127.0.0.1:{port} within 3 s.", r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3) + SqlServerProvider.OpenMargin + TimeSpan.FromSeconds(1.5), $"took {sw.Elapsed}");
        Assert.False(SqlServerProvider.StrictKnownUnsupported(profile.Id));
    }

    // Before: on Windows a host without a port is also tried over named pipes, which SqlClient's timeout does not bound
    // (30 s measured with Connect Timeout=3); profile.test then gave up with "A task was canceled.".
    [Fact]
    public async Task Unroutable_host_without_a_port_is_bounded_by_the_timeout_and_named()
    {
        // Windows only (named pipes). Elsewhere a host without a port is plain TCP, covered by the closed-port case above,
        // and a dial to an unroutable address kept one of SqlClient's few non-pooled open slots for the OS connect timeout
        // (about 2 min on Linux) after this test ended: later SQL Server opens in the run failed with "too many
        // simultaneous non-pooled connection attempts" (CI, MonitorMatrixTests C2/C3).
        if (!OperatingSystem.IsWindows()) return;
        await using var host = new TestHost();
        var profile = P(AuthKind.Sql) with { Host = "192.0.2.1", Port = null, TimeoutS = 3 }; // RFC 5737 TEST-NET-1: never routed
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await host.Rpc.ProfileTest(new ProfileTestParams { Profile = profile, Secret = Secret });
        sw.Stop();
        Assert.False(r.Ok);
        Assert.DoesNotContain("task was canceled", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Could not reach SQL Server at 192.0.2.1:1433 within 3 s.", r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3) + SqlServerProvider.OpenMargin + TimeSpan.FromSeconds(1.5), $"took {sw.Elapsed}");
    }

    // Before: unsaved profiles all have Id "", so one failed Test turned Strict off for every new profile.
    [Fact]
    public async Task A_failed_test_of_an_unsaved_profile_never_turns_strict_off()
    {
        await using var host = new TestHost();
        var profile = P(AuthKind.Sql) with { Id = "", Host = "127.0.0.1", Port = ClosedPort(), TimeoutS = 2 };
        Assert.False((await host.Rpc.ProfileTest(new ProfileTestParams { Profile = profile, Secret = Secret })).Ok);
        Assert.False(SqlServerProvider.StrictKnownUnsupported(""));
        SqlServerProvider.MarkStrictUnsupported("", DateTime.UtcNow);
        Assert.False(SqlServerProvider.StrictKnownUnsupported(""));
    }

    [Fact]
    public void A_strict_finding_expires_and_a_changed_profile_is_probed_again()
    {
        var id = "p-ttl-" + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        SqlServerProvider.MarkStrictUnsupported(id, now);
        Assert.True(SqlServerProvider.StrictKnownUnsupported(id, now));
        Assert.True(SqlServerProvider.StrictKnownUnsupported(id, now + SqlServerProvider.StrictProbeTtl - TimeSpan.FromSeconds(1)));
        Assert.False(SqlServerProvider.StrictKnownUnsupported(id, now + SqlServerProvider.StrictProbeTtl + TimeSpan.FromSeconds(1)));
        Assert.False(SqlServerProvider.StrictKnownUnsupported(id, now)); // expired entries are dropped

        SqlServerProvider.MarkStrictUnsupported(id, now);
        Provider.ForgetConnections(id);
        Assert.False(SqlServerProvider.StrictKnownUnsupported(id, now));
    }

    // The bound must never cut a caller's own cancel short or turn it into a timeout message.
    [Fact]
    public async Task A_cancel_by_the_caller_stays_a_cancel()
    {
        // A local server that accepts and never answers: stopping it ends SqlClient's dial at once, so the open does not
        // keep one of SqlClient's non-pooled open slots after the test (an unroutable address did, for minutes).
        var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var profile = P(AuthKind.Sql) with { Host = "127.0.0.1", Port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port, TimeoutS = 10 };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.OpenAsync(profile, Secret, null, null, cts.Token));
        }
        finally { silent.Stop(); }
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "CLAUDE.md"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
