using System.Text.Json;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Logging;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 1, per engine and sign-in: a secret that enters the sidecar (session.open, profile.test, a pasted connection
/// string) is never written to store.db, never reaches the log files (at Trace) and never comes back in an error or in
/// the parsed profile. SQLite and DuckDB sign in with nothing, so they only check that no secret is asked for or stored.
/// </summary>
public class SecretAtRestMatrixTests
{
    // ---- Log scrubber: every secret parameter spelling the active engines' drivers accept -------------------------------

    [Theory]
    // SQL Server (SqlClient): Password / PWD, any case, quoted forms.
    [InlineData("mssql", "Server=h;User ID=sa;Password=fake-s3cret-1;", "fake-s3cret-1")]
    [InlineData("mssql", "Server=h;UID=sa;PWD=fake-s3cret-2", "fake-s3cret-2")]
    [InlineData("mssql", "server=h;pwd = 'fake s3cret 3';x=1", "fake s3cret 3")]
    [InlineData("mssql", "Server=h;Password={fake;s3cret;4}", "fake;s3cret;4")]
    [InlineData("mssql", "Server=h;PASSWORD=\"fake;s3cret5\"", "fake;s3cret5")]
    // SQL Server Entra access token (SqlClient keyword).
    [InlineData("mssql", "Server=h;Access Token=eyJfake.tok.en6;Encrypt=True", "eyJfake.tok.en6")]
    // PostgreSQL (Npgsql): Password, spaced key form.
    [InlineData("pg", "Host=h;Username=u;Password=fake-s3cret-7;SSL Mode=Require", "fake-s3cret-7")]
    [InlineData("pg", "Host=h;SSL Password=fake-s3cret-8", "fake-s3cret-8")]
    // MySQL / MariaDB (MySqlConnector): Password / pwd.
    [InlineData("mySql", "Server=h;User=root;Password=fake-s3cret-9", "fake-s3cret-9")]
    [InlineData("mySql", "Server=h;Uid=root;Pwd=fake-s3cret-10", "fake-s3cret-10")]
    // HTTP-style headers (Entra token flows).
    [InlineData("any", "Authorization: Bearer eyJfake.header.11", "eyJfake.header.11")]
    [InlineData("any", "authorization: Basic ZmFrZTpzM2NyZXQ=", "ZmFrZTpzM2NyZXQ=")]
    // RPC-shaped JSON: every secret field name of the contract (secret, sshSecret) and the generic ones.
    [InlineData("rpc", "{\"profileId\":\"p\",\"secret\":\"fake-s3cret-12\"}", "fake-s3cret-12")]
    [InlineData("rpc", "{\"sshSecret\":\"fake-s3cret-13\",\"profileId\":\"p\"}", "fake-s3cret-13")]
    [InlineData("rpc", "{\"Passphrase\":\"fake-s3cret-14\"}", "fake-s3cret-14")]
    [InlineData("rpc", "{\"password\":\"fake \\\"s3cret\\\" 15\"}", "s3cret")]
    [InlineData("rpc", "{\"accessToken\":\"fake-s3cret-16\"}", "fake-s3cret-16")]
    [InlineData("rpc", "{\"token\" : \"fake-s3cret-17\"}", "fake-s3cret-17")]
    public void Scrubber_masks_each_engines_secret_keywords(string engine, string line, string secret)
    {
        var scrubbed = LogScrubber.Scrub(line);
        Assert.False(scrubbed.Contains(secret, StringComparison.Ordinal), $"{engine}: '{secret}' survived in '{scrubbed}'");
        Assert.Contains("***", scrubbed);
    }

    // Regression (Windows CI flake): another test registered the secret "fresh", which masked part of the key name and
    // left the refresh_token value unmasked.
    [Fact]
    public void A_registered_secret_inside_a_key_name_does_not_hide_that_keys_value()
    {
        LogScrubber.RegisterSecret("fresh");
        var scrubbed = LogScrubber.Scrub("{\"refresh_token\" : \"fake-s3cret-37\"}");
        Assert.False(scrubbed.Contains("fake-s3cret-37", StringComparison.Ordinal), scrubbed);
    }

    // Regressions: shapes the scrubber missed (defence in depth behind RegisterSecret).
    [Theory]
    [InlineData("passphrase=fake-s3cret-21", "fake-s3cret-21")]
    [InlineData("Passphrase: fake-s3cret-22", "fake-s3cret-22")]
    [InlineData("secret=fake-s3cret-23", "fake-s3cret-23")]
    [InlineData("SSH_PASSPHRASE=fake-s3cret-24", "fake-s3cret-24")]
    [InlineData("password: fake-s3cret-25", "fake-s3cret-25")]
    [InlineData("Host=h;SslPassword=fake-s3cret-26", "fake-s3cret-26")]
    [InlineData("CertificatePassword=fake-s3cret-27;x=1", "fake-s3cret-27")]
    [InlineData("{\"access_token\":\"fake-s3cret-28\"}", "fake-s3cret-28")]
    [InlineData("{\"refresh_token\" : \"fake-s3cret-29\"}", "fake-s3cret-29")]
    [InlineData("{\"pwd\":\"fake-s3cret-30\"}", "fake-s3cret-30")]
    [InlineData("dial postgresql://app:fake-s3cret-31@db.example.test:5432/app failed", "fake-s3cret-31")]
    [InlineData("dial mysql://root:fake-s3cret-32@db.example.test/shop failed", "fake-s3cret-32")]
    [InlineData("Password=\"fake\"\"s3cret-33\";x=1", "s3cret-33")]
    [InlineData("Pwd={fake}}s3cret-34};x=1", "s3cret-34")]
    [InlineData("Pwd='fake''s3cret-35';x=1", "s3cret-35")]
    [InlineData("Password=\"fake-unclosed-s3cret-36", "s3cret-36")]
    public void Scrubber_masks_more_secret_shapes(string line, string secret)
    {
        var scrubbed = LogScrubber.Scrub(line);
        Assert.False(scrubbed.Contains(secret, StringComparison.Ordinal), $"'{secret}' survived in '{scrubbed}'");
        Assert.Contains("***", scrubbed);
    }

    // A registered secret that contains another registered one is masked whole (the shorter must not go first and leave
    // "pre-***-post" behind); A_registered_secret_is_masked_even_without_a_keyword failed that way once "s3cret" was known.
    [Fact]
    public void A_registered_secret_containing_another_is_masked_whole()
    {
        for (var i = 0; i < 20; i++)
        {
            var inner = $"zqin{i}{Guid.NewGuid():N}"[..12];
            LogScrubber.RegisterSecret(inner);
            LogScrubber.RegisterSecret($"pre-{inner}-post");
            Assert.Equal("x *** y", LogScrubber.Scrub($"x pre-{inner}-post y"));
        }
    }

    [Theory]
    [InlineData("Host=h;SslPassword=fake-s3cret-26;Port=5432", "Host=h;SslPassword=***;Port=5432")]
    [InlineData("Passphrase: fake-s3cret-22 and more", "Passphrase: *** and more")]
    [InlineData("Password=\"a\"\"b\";Server=x", "Password=***;Server=x")]
    [InlineData("Pwd={a}}b};Server=x", "Pwd=***;Server=x")]
    [InlineData("postgresql://app:pw-fake@h/db", "postgresql://app:***@h/db")]
    // Not secrets: MySQL's "using password: YES" says only whether one was sent; a URI without userinfo password stays.
    [InlineData("Access denied for user 'u'@'h' (using password: YES)", "Access denied for user 'u'@'h' (using password: YES)")]
    [InlineData("postgresql://app@h:5432/db", "postgresql://app@h:5432/db")]
    [InlineData("https://h:8443/path@x", "https://h:8443/path@x")]
    public void Scrubber_keeps_the_rest_of_the_line(string line, string expected) => Assert.Equal(expected, LogScrubber.Scrub(line));

    [Fact]
    public void Scrubber_keeps_text_without_secrets()
    {
        const string line = "session opened for profile 01ABC on host db.local (engine pg, user app)";
        Assert.Equal(line, LogScrubber.Scrub(line));
    }

    [Fact]
    public void A_registered_secret_is_masked_even_without_a_keyword()
    {
        LogScrubber.RegisterSecret("fake-registered-s3cret");
        Assert.Equal("driver said: bad credential *** for u", LogScrubber.Scrub("driver said: bad credential fake-registered-s3cret for u"));
    }

    // ---- The sidecar end to end: profile.save, session.open, profile.test and parseConnectionString per engine ----------

    sealed class Harness : IAsyncDisposable
    {
        public string LogDir { get; } = Directory.CreateTempSubdirectory("qsecret-log-").FullName;
        public string DataDir { get; } = Directory.CreateTempSubdirectory("qsecret-data-").FullName;
        public LocalStore Store { get; } = LocalStore.InMemory();
        readonly ScrubbingLoggerProvider _provider;
        readonly ILoggerFactory _logs;
        public Services Services { get; }
        public RpcServer Rpc { get; }

        public Harness()
        {
            _provider = new ScrubbingLoggerProvider(LogDir, LogLevel.Trace, echoToStderr: false);
            _logs = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(_provider));
            var providers = new ProviderRegistry([new SqlServerProvider(), new PostgresProvider(), new MySqlProvider(), new SqliteProvider(), new DuckDbProvider(DataDir)]);
            Services = Services.Create(Store, new CapturingFrames(), new CapturingNotifier(), _logs, providers, DataDir);
            Rpc = new RpcServer(Services, () => { });
        }

        /// <summary>Every cell of every table of store.db, as text.</summary>
        public string StoreText()
        {
            using var c = Store.Open();
            var tables = new List<string>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            Assert.NotEmpty(tables);
            var sb = new System.Text.StringBuilder();
            foreach (var t in tables)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{t.Replace("\"", "\"\"")}\"";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    for (var i = 0; i < r.FieldCount; i++)
                        sb.Append(r.IsDBNull(i) ? "" : r.GetValue(i) is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : r.GetValue(i).ToString()).Append('\u001f');
            }
            return sb.ToString();
        }

        public string LogText()
        {
            _provider.Dispose();
            return string.Concat(Directory.GetFiles(LogDir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            _logs.Dispose();
            _provider.Dispose();
            foreach (var d in new[] { LogDir, DataDir }) try { Directory.Delete(d, true); } catch { /* best effort */ }
        }
    }

    static Profile Server(Engine engine, AuthKind auth, string host = "127.0.0.1", int? port = 1) => new()
    {
        Id = "", Name = $"{engine}-{auth}", Engine = engine, Host = host, Port = port, Auth = auth,
        Username = auth is AuthKind.Windows or AuthKind.EntraDeviceCode ? null : "u", Environment = EnvironmentKind.Dev, TimeoutS = 1,
        PasswordRetention = "forever",
    };

    /// <summary>Every server engine × every sign-in that takes a secret (BRIEF: Engines and sign-in kinds).</summary>
    public static TheoryData<Engine, AuthKind> SecretSignIns => new()
    {
        { Engine.Mssql, AuthKind.Sql }, { Engine.Mssql, AuthKind.EntraToken },
        { Engine.Pg, AuthKind.Password }, { Engine.Pg, AuthKind.EntraToken },
        { Engine.MySql, AuthKind.Password },
    };

    [Theory]
    [MemberData(nameof(SecretSignIns))]
    public async Task A_secret_given_to_open_and_test_never_reaches_store_logs_or_the_error(Engine engine, AuthKind auth)
    {
        var secret = $"fake-{engine}-{auth}-db-s3cret";
        var ssh = $"fake-{engine}-{auth}-ssh-s3cret";
        await using var h = new Harness();
        var saved = await h.Rpc.ProfileSave(Server(engine, auth));
        // session.open dials nothing (§6); it only holds the secret in memory.
        await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = secret, SshSecret = ssh });
        // profile.test dials: a closed local port refuses at once; the error must not quote the secret.
        var test = await h.Rpc.ProfileTest(new ProfileTestParams { Profile = saved, Secret = secret });
        Assert.False(test.Ok);
        Assert.DoesNotContain(secret, test.Error ?? "");
        // And through an SSH bastion that refuses: neither secret in the error.
        var viaSsh = await h.Rpc.ProfileTest(new ProfileTestParams
        {
            Profile = saved with { Host = "db.internal", Port = 5000, Ssh = new SshSettings { Host = "127.0.0.1", Port = 1, Username = "deploy", Auth = SshAuthKind.Password } },
            Secret = secret, SshSecret = ssh,
        });
        Assert.False(viaSsh.Ok);
        Assert.DoesNotContain(secret, viaSsh.Error ?? "");
        Assert.DoesNotContain(ssh, viaSsh.Error ?? "");
        // Re-saving (a retention change) must not pull the in-memory secret into the row either.
        await h.Rpc.ProfileSave(saved with { PasswordRetention = "duration", PasswordRetentionMinutes = 60 });

        var store = h.StoreText();
        var logs = h.LogText();
        foreach (var s in new[] { secret, ssh })
        {
            Assert.DoesNotContain(s, store);
            Assert.DoesNotContain(s, logs);
        }
        // The stored and listed profile carries no secret field.
        var listed = JsonSerializer.Serialize(await h.Rpc.ProfileList());
        Assert.DoesNotContain(secret, listed);
        Assert.DoesNotContain(ssh, listed);
    }

    public static TheoryData<Engine, string, string> PastedStrings => new()
    {
        { Engine.Mssql, "Server=db.local,1433;User ID=app;Password=fake-mssql-paste-1;Encrypt=True", "fake-mssql-paste-1" },
        { Engine.Mssql, "Server=db.local;PWD='fake;mssql;paste;2'", "fake;mssql;paste;2" },
        { Engine.Pg, "Host=db.local;Username=app;Password=fake-pg-paste-3;SSL Mode=Require", "fake-pg-paste-3" },
        { Engine.Pg, "postgresql://app:fake-pg-paste-4@db.local:5432/sales", "fake-pg-paste-4" },
        { Engine.MySql, "Server=db.local;User ID=root;Password=fake-my-paste-5;SslMode=Required", "fake-my-paste-5" },
        { Engine.MySql, "Server=db.local;Uid=root;Pwd=fake-my-paste-6", "fake-my-paste-6" },
    };

    [Theory]
    [MemberData(nameof(PastedStrings))]
    public async Task A_pasted_connection_string_returns_its_secret_apart_and_stores_none(Engine engine, string cs, string secret)
    {
        await using var h = new Harness();
        var basis = Server(engine, engine == Engine.Mssql ? AuthKind.Sql : AuthKind.Password, "localhost", null);
        var r = await h.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = cs });
        Assert.Equal(secret, r.Secret);
        // The secret is only in Secret: not in the profile, its options, the unknown or not-applied keyword lists, or notes.
        var rest = JsonSerializer.Serialize(new { r.Profile, r.Unknown, r.NotApplied, r.Notes });
        Assert.DoesNotContain(secret, rest);
        // Saving the parsed profile (what the editor does) stores no trace of it.
        await h.Rpc.ProfileSave(r.Profile with { Id = "", Name = "pasted" });
        Assert.DoesNotContain(secret, h.StoreText());
        Assert.DoesNotContain(secret, h.LogText());
        // And the scrubber learned it: a later driver message quoting it is masked.
        Assert.DoesNotContain(secret, LogScrubber.Scrub($"login failed: {secret}"));
    }

    [Theory]
    [InlineData(Engine.Mssql, "Server=db.local;Password=\"fake-unterminated")]
    [InlineData(Engine.Pg, "Host=db.local;Password='fake-unterminated")]
    [InlineData(Engine.MySql, "Server=db.local;Password={fake-unterminated")]
    public async Task A_malformed_pasted_string_is_refused_without_echoing_its_secret(Engine engine, string cs)
    {
        await using var h = new Harness();
        var basis = Server(engine, engine == Engine.Mssql ? AuthKind.Sql : AuthKind.Password, "localhost", null);
        var ex = await Record.ExceptionAsync(() => h.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = cs }));
        if (ex != null) Assert.DoesNotContain("fake-unterminated", ex.Message);
        Assert.DoesNotContain("fake-unterminated", h.StoreText());
        Assert.DoesNotContain("fake-unterminated", h.LogText());
    }

    [Theory]
    [InlineData(Engine.Mssql, AuthKind.Windows)]
    [InlineData(Engine.Mssql, AuthKind.Kerberos)]
    [InlineData(Engine.Mssql, AuthKind.EntraInteractive)]
    [InlineData(Engine.Mssql, AuthKind.EntraDeviceCode)]
    [InlineData(Engine.Pg, AuthKind.Kerberos)]
    public async Task A_sign_in_without_a_secret_stores_none_even_when_one_is_sent(Engine engine, AuthKind auth)
    {
        const string stray = "fake-stray-s3cret-for-secretless-auth";
        await using var h = new Harness();
        var saved = await h.Rpc.ProfileSave(Server(engine, auth));
        await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = stray });
        Assert.DoesNotContain(stray, h.StoreText());
        Assert.DoesNotContain(stray, h.LogText());
    }

    [Theory]
    [InlineData(Engine.Sqlite)]
    [InlineData(Engine.DuckDb)]
    public async Task File_engines_store_no_secret_for_an_in_memory_profile(Engine engine)
    {
        const string stray = "fake-stray-s3cret-for-file-engine";
        await using var h = new Harness();
        var saved = await h.Rpc.ProfileSave(new Profile
        {
            Id = "", Name = $"{engine}-mem", Engine = engine, Location = LocationKind.Memory, Auth = AuthKind.None, Environment = EnvironmentKind.Local,
        });
        Assert.Equal(AuthKind.None, saved.Auth);
        await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = stray });
        Assert.DoesNotContain(stray, h.StoreText());
        Assert.DoesNotContain(stray, h.LogText());
    }
}
