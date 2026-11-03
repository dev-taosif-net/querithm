using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Monitoring;
using Querithm.Core.Store;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;
using Querithm.Tests.Fakes;

namespace Querithm.Tests.Security;

/// <summary>
/// L4: a secret typed in SQL is masked on the engine's tokens before the text is kept (history, the monitor's text
/// store); the rest of the statement stays as typed. On Unix the data folder, store.db and the logs are the user's alone.
/// </summary>
public class SecretRedactionTests
{
    static string Redact(IDialectService language, string sql) => language.RedactSecrets(sql).Text;

    public static TheoryData<string, string> SqlServer => new()
    {
        { "ALTER LOGIN sa WITH PASSWORD = 'Hunter2!'", "ALTER LOGIN sa WITH PASSWORD = <redacted>" },
        { "ALTER LOGIN app WITH PASSWORD = N'n3w' OLD_PASSWORD = N'0ld'", "ALTER LOGIN app WITH PASSWORD = <redacted> OLD_PASSWORD = <redacted>" },
        { "CREATE LOGIN app WITH PASSWORD = 'p@ss', CHECK_POLICY = OFF", "CREATE LOGIN app WITH PASSWORD = <redacted>, CHECK_POLICY = OFF" },
        { "CREATE LOGIN app WITH PASSWORD = 0x0200A1B2 HASHED", "CREATE LOGIN app WITH PASSWORD = <redacted> HASHED" },
        { "CREATE USER u WITH PASSWORD = 'contained1'", "CREATE USER u WITH PASSWORD = <redacted>" },
        { "CREATE CREDENTIAL c WITH IDENTITY = 'acct', SECRET = 'sv=2025&sig=abc'", "CREATE CREDENTIAL c WITH IDENTITY = 'acct', SECRET = <redacted>" },
        { "OPEN MASTER KEY DECRYPTION BY PASSWORD = 'mk-pass'", "OPEN MASTER KEY DECRYPTION BY PASSWORD = <redacted>" },
        { "BACKUP DATABASE d TO DISK = 'd.bak' WITH PASSWORD = 'bk1', MEDIAPASSWORD = 'm2'", "BACKUP DATABASE d TO DISK = 'd.bak' WITH PASSWORD = <redacted>, MEDIAPASSWORD = <redacted>" },
        { "CREATE APPLICATION ROLE r WITH PASSWORD = 'ar1'", "CREATE APPLICATION ROLE r WITH PASSWORD = <redacted>" },
        { "EXEC sp_addlinkedsrvlogin @rmtsrvname = 'S', @useself = 'false', @rmtuser = 'u', @rmtpassword = 'lnk'", "EXEC sp_addlinkedsrvlogin @rmtsrvname = 'S', @useself = 'false', @rmtuser = 'u', @rmtpassword = <redacted>" },
        { "EXEC sp_addlinkedsrvlogin 'S', 'false', NULL, 'u', 'lnk'", "EXEC sp_addlinkedsrvlogin 'S', 'false', NULL, 'u', <redacted>" },
        { "EXEC sp_setapprole 'r', 'ar1'; SELECT 'ar1-not-a-secret'", "EXEC sp_setapprole 'r', <redacted>; SELECT 'ar1-not-a-secret'" },
        { "EXEC sp_password @old = 'a1b', @new = 'c2d', @loginame = 'x'", "EXEC sp_password @old = <redacted>, @new = <redacted>, @loginame = 'x'" },
        { "DECLARE @pwd nvarchar(50) = N'v4r'; SET @password = 'v5r';", "DECLARE @pwd nvarchar(50) = <redacted>; SET @password = <redacted>;" },
        { "SELECT ENCRYPTBYPASSPHRASE('phr', 'data')", "SELECT ENCRYPTBYPASSPHRASE(<redacted>, 'data')" },
        { "SELECT name, 'password' FROM sys.sql_logins -- PASSWORD = 'in a comment'", "SELECT name, 'password' FROM sys.sql_logins -- PASSWORD = 'in a comment'" },
        { "SELECT 1", "SELECT 1" },
    };

    [Theory]
    [MemberData(nameof(SqlServer))]
    public void Sql_server_secrets_are_masked_on_scriptdom_tokens(string sql, string expected) => Assert.Equal(expected, Redact(new TSqlLanguageService(), sql));

    public static TheoryData<string, string> Postgres => new()
    {
        { "ALTER ROLE app PASSWORD 'Hunter2!' VALID UNTIL '2027-01-01'", "ALTER ROLE app PASSWORD <redacted> VALID UNTIL '2027-01-01'" },
        { "CREATE USER u WITH LOGIN ENCRYPTED PASSWORD 'md5abc'", "CREATE USER u WITH LOGIN ENCRYPTED PASSWORD <redacted>" },
        { "ALTER USER u WITH PASSWORD E'it\\'s'", "ALTER USER u WITH PASSWORD <redacted>" },
        { "CREATE ROLE r PASSWORD $$dollar$$", "CREATE ROLE r PASSWORD <redacted>" },
        { "CREATE USER MAPPING FOR u SERVER s OPTIONS (user 'remote', password 'fdw1')", "CREATE USER MAPPING FOR u SERVER s OPTIONS (user 'remote', password <redacted>)" },
        { "CREATE SUBSCRIPTION s CONNECTION 'host=h dbname=d password=sub1' PUBLICATION p", "CREATE SUBSCRIPTION s CONNECTION <redacted> PUBLICATION p" },
        { "SELECT crypt('pw1', gen_salt('bf')), pgp_sym_encrypt('data', 'k3y')", "SELECT crypt(<redacted>, gen_salt('bf')), pgp_sym_encrypt('data', <redacted>)" },
        { "ALTER ROLE app PASSWORD NULL", "ALTER ROLE app PASSWORD NULL" },
        { "SELECT 'PASSWORD' AS label", "SELECT 'PASSWORD' AS label" },
    };

    [Theory]
    [MemberData(nameof(Postgres))]
    public void Postgres_secrets_are_masked_on_its_tokens(string sql, string expected) => Assert.Equal(expected, Redact(new PgLanguageService(), sql));

    public static TheoryData<string, string> MySql => new()
    {
        { "CREATE USER 'bob'@'%' IDENTIFIED BY 'Hunter2!'", "CREATE USER 'bob'@'%' IDENTIFIED BY <redacted>" },
        { "ALTER USER u IDENTIFIED BY 'n3w' REPLACE '0ld' RETAIN CURRENT PASSWORD", "ALTER USER u IDENTIFIED BY <redacted> REPLACE <redacted> RETAIN CURRENT PASSWORD" },
        { "CREATE USER u IDENTIFIED WITH mysql_native_password AS '*2470C0C0'", "CREATE USER u IDENTIFIED WITH mysql_native_password AS <redacted>" },
        { "CREATE USER u IDENTIFIED VIA ed25519 USING PASSWORD('m4r')", "CREATE USER u IDENTIFIED VIA ed25519 USING PASSWORD(<redacted>)" },
        { "SET PASSWORD FOR 'bob'@'localhost' = 'pw9'", "SET PASSWORD FOR 'bob'@'localhost' = <redacted>" },
        { "SET PASSWORD = \"dq1\"", "SET PASSWORD = <redacted>" },
        { "CHANGE REPLICATION SOURCE TO SOURCE_USER = 'r', SOURCE_PASSWORD = 'rp1'", "CHANGE REPLICATION SOURCE TO SOURCE_USER = 'r', SOURCE_PASSWORD = <redacted>" },
        { "SELECT AES_ENCRYPT('data', 'k3y')", "SELECT AES_ENCRYPT('data', <redacted>)" },
        { "SET @pwd = 'v1'", "SET @pwd = <redacted>" },
    };

    [Theory]
    [MemberData(nameof(MySql))]
    public void Mysql_secrets_are_masked_on_its_tokens(string sql, string expected) => Assert.Equal(expected, Redact(new MySqlLanguageService(), sql));

    public static TheoryData<string, string> DuckDb => new()
    {
        { "CREATE SECRET (TYPE s3, KEY_ID 'AKIAEXAMPLE', SECRET 'wJalr', REGION 'eu-west-1')", "CREATE SECRET (TYPE s3, KEY_ID <redacted>, SECRET <redacted>, REGION 'eu-west-1')" },
        { "CREATE SECRET s (TYPE s3, KEY_ID 'k', SECRET 's', SESSION_TOKEN 'tok')", "CREATE SECRET s (TYPE s3, KEY_ID <redacted>, SECRET <redacted>, SESSION_TOKEN <redacted>)" },
        { "CREATE SECRET (TYPE azure, CONNECTION_STRING 'AccountKey=abc')", "CREATE SECRET (TYPE azure, CONNECTION_STRING <redacted>)" },
        { "CREATE SECRET (TYPE huggingface, TOKEN 'hf_x')", "CREATE SECRET (TYPE huggingface, TOKEN <redacted>)" },
        { "CREATE SECRET (TYPE postgres, HOST 'h', PASSWORD 'pg1')", "CREATE SECRET (TYPE postgres, HOST 'h', PASSWORD <redacted>)" },
        { "SELECT 'KEY_ID' AS k", "SELECT 'KEY_ID' AS k" },
    };

    [Theory]
    [MemberData(nameof(DuckDb))]
    public void Duckdb_secrets_are_masked_on_its_tokens(string sql, string expected) => Assert.Equal(expected, Redact(new DuckDbLanguageService(), sql));

    public static TheoryData<string, string> Sqlite => new()
    {
        { "PRAGMA key = 'k3y'", "PRAGMA key = <redacted>" },
        { "PRAGMA main.rekey('n3w')", "PRAGMA main.rekey(<redacted>)" },
        { "ATTACH DATABASE 'other.db' AS o KEY 'att'", "ATTACH DATABASE 'other.db' AS o KEY <redacted>" },
        { "UPDATE users SET password = 'pw1' WHERE id = 1", "UPDATE users SET password = <redacted> WHERE id = 1" },
        { "SELECT * FROM kv WHERE key = 'not-a-secret'", "SELECT * FROM kv WHERE key = 'not-a-secret'" },
    };

    [Theory]
    [MemberData(nameof(Sqlite))]
    public void Sqlite_secrets_are_masked_on_its_tokens(string sql, string expected) => Assert.Equal(expected, Redact(new SqliteLanguageService(), sql));

    [Fact]
    public void The_secret_is_also_masked_in_an_error_that_quotes_it()
    {
        var r = new MySqlLanguageService().RedactSecrets("CREATE USER u IDENTIFIED BYY 'Hunter2!'; ALTER USER u IDENTIFIED BY 'Hunter2!'");
        Assert.Equal("You have an error near 'Hunter2!'' at line 1".Replace("Hunter2!", "***"), r.Mask("You have an error near 'Hunter2!'' at line 1"));
    }

    // ---------------------------------------------------------------------------------------------- where text is kept

    [Fact]
    public async Task History_keeps_the_statement_with_its_password_masked()
    {
        await using var host = new TestHostWithFake();
        host.Provider.Handler = (_, _) => throw new FakeDbException("Incorrect syntax near 'Hunter2!'.");
        var p = await host.Rpc.ProfileSave(new Profile { Id = "", Name = "s", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var s = await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var r = await host.Rpc.QueryExecute(new QueryExecuteParams { SessionId = s.SessionId, Sql = "ALTER LOGIN sa WITH PASSWORD = 'Hunter2!'", Options = new ExecuteOptions { ConfirmedDestructive = true } });
        await host.Notifier.WaitDone(r.ExecutionId!);
        var entry = await WaitForHistory(host.Services.History);
        Assert.Equal("ALTER LOGIN sa WITH PASSWORD = <redacted>", entry.Sql);
        Assert.DoesNotContain("Hunter2!", entry.Error ?? "", StringComparison.Ordinal);
        Assert.Empty(host.Services.History.Search(new HistorySearchParams { Text = "Hunter2" }));
    }

    [Fact]
    public async Task History_uses_the_session_engines_own_tokens()
    {
        await using var host = new TestHostWithFake();
        host.Provider.As(Engine.Pg, new PgLanguageService(), transactions: new FakePgTransactions());
        var p = await host.Rpc.ProfileSave(new Profile { Id = "", Name = "s", Engine = Engine.Pg, Host = "h", Auth = AuthKind.Password, Username = "u" });
        var s = await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var r = await host.Rpc.QueryExecute(new QueryExecuteParams { SessionId = s.SessionId, Sql = "ALTER ROLE app PASSWORD 'Hunter2!'", Options = new ExecuteOptions { ConfirmedDestructive = true } });
        await host.Notifier.WaitDone(r.ExecutionId!);
        Assert.Equal("ALTER ROLE app PASSWORD <redacted>", (await WaitForHistory(host.Services.History)).Sql);
    }

    /// <summary>The history row is written right after query.done.</summary>
    static async Task<HistoryEntry> WaitForHistory(HistoryStore history)
    {
        for (var i = 0; i < 200; i++)
        {
            if (history.List(new HistoryListParams { Limit = 1 }).FirstOrDefault() is { } e) return e;
            await Task.Delay(10);
        }
        throw new TimeoutException("no history row");
    }

    [Fact]
    public void The_monitor_text_store_keeps_texts_with_secrets_masked()
    {
        var store = new TextStore { Redact = t => new PgLanguageService().RedactSecrets(t).Text };
        var id = store.Intern("ALTER ROLE other_user PASSWORD 'Hunter2!'");
        Assert.Equal("ALTER ROLE other_user PASSWORD <redacted>", store.TryGet(id));
    }

    // ---------------------------------------------------------------------------------------------- permissions (Unix)

    static UnixFileMode Mode(string path) => File.GetUnixFileMode(path) & (UnixFileMode)0x1FF;

    [Fact]
    public void Store_and_folder_are_private_and_wider_permissions_are_narrowed_on_start()
    {
        if (OperatingSystem.IsWindows()) return; // the profile folder's ACL keeps other users out
        var root = Directory.CreateTempSubdirectory("qperm-").FullName;
        try
        {
            var dir = Path.Combine(root, "querithm");
            var db = Path.Combine(dir, "store.db");
            new LocalStore(db);
            Assert.Equal(PrivateFiles.FolderMode, Mode(dir));
            Assert.Equal(PrivateFiles.FileMode, Mode(db));
            foreach (var side in new[] { db + "-wal", db + "-shm" })
                if (File.Exists(side)) Assert.Equal(PrivateFiles.FileMode, Mode(side));

            // An older build (or a 022 umask) left them world-readable: the next start narrows them.
            File.SetUnixFileMode(dir, (UnixFileMode)0x1ED); // 0755
            File.SetUnixFileMode(db, (UnixFileMode)0x1A4); // 0644
            File.WriteAllBytes(db + "-wal", []);
            File.SetUnixFileMode(db + "-wal", (UnixFileMode)0x1A4);
            new LocalStore(db);
            Assert.Equal(PrivateFiles.FolderMode, Mode(dir));
            Assert.Equal(PrivateFiles.FileMode, Mode(db));
            Assert.Equal(PrivateFiles.FileMode, Mode(db + "-wal"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Logs_are_private()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("qperm-").FullName;
        try
        {
            var logs = Path.Combine(root, "logs");
            using (var provider = new Host.ScrubbingLoggerProvider(logs, Microsoft.Extensions.Logging.LogLevel.Information))
                provider.CreateLogger("t").LogWarning("hello");
            Assert.Equal(PrivateFiles.FolderMode, Mode(logs));
            var file = Assert.Single(Directory.GetFiles(logs));
            Assert.Equal(PrivateFiles.FileMode, Mode(file));
        }
        finally { Directory.Delete(root, true); }
    }
}
