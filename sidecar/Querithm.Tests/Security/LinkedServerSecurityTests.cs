using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Metadata;
using Querithm.Providers.MySql;
using Querithm.Providers.Oracle;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests.Security;

/// <summary>
/// Security invariants for Linked Servers, PostgreSQL Foreign Servers, Oracle Database Links and MySQL external servers (§13):
/// 1. Zero secret leakage (mysql.servers Password never selected; pg_foreign_server srvoptions allowlist; Oracle never reads SYS.LINK$;
///    all RPC text fields and error messages scrubbed via LogScrubber).
/// 2. SQL injection resistance (crafted server/catalog names are parameterized in SQL Server stored proc calls and properly quoted/escaped
///    in PostgreSQL and Oracle DDL scripters).
/// 3. RPC boundary bounds (oversized/null/NUL session ids, server names, catalog names and out-of-range limits are rejected as "invalid"
///    before touching any session or database).
/// </summary>
public sealed class LinkedServerSecurityTests
{
    [Fact]
    public async Task Crafted_server_and_catalog_names_stay_parameterized_in_sql_server_linked_procs()
    {
        var maliciousServer = "ERP'; DROP TABLE users; -- [x]";
        var maliciousCatalog = "Db'; EXEC xp_cmdshell 'whoami'; --";
        var recorded = new List<(string Sql, Dictionary<string, object?> Params)>();

        using var conn = new FakeDbConnection((cmd, _) =>
        {
            var ps = new Dictionary<string, object?>();
            foreach (DbParameter p in cmd.Parameters) ps[p.ParameterName] = p.Value;
            recorded.Add((cmd.CommandText, ps));
            return Task.FromResult(FakeResult.None);
        });

        var lp = new SqlServerLinkedServers();
        _ = await lp.ListCatalogsAsync(conn, maliciousServer, CancellationToken.None);
        _ = await lp.ListTablesAsync(conn, maliciousServer, maliciousCatalog, 100, CancellationToken.None);
        _ = await lp.TestAsync(conn, maliciousServer, CancellationToken.None);

        Assert.Equal(3, recorded.Count);
        foreach (var (sql, ps) in recorded)
        {
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("xp_cmdshell", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(maliciousServer, ps["@server"]);
        }
        Assert.Equal(maliciousCatalog, recorded[1].Params["@catalog"]);
    }

    [Fact]
    public void Crafted_names_and_options_are_safely_quoted_and_credential_free_in_pg_and_oracle_scripters()
    {
        // PostgreSQL: quote identifier doubles quotes, string literal escapes single quotes & backslashes, unknown/secret options dropped.
        var pgDdl = PostgresScripter.BuildForeignServerCreate(
            srvName: "bad\"srv; DROP SERVER x; --",
            fdwName: "postgres_fdw",
            srvType: "type'inject",
            srvVersion: "1\\2",
            srvOptions:
            [
                "host=db'; DROP TABLE t; --",
                "port=5432",
                "password=hunter1",
                "sslpassword=hunter2",
                "oauth_token=hunter3",
                "malformed_no_equals",
            ]);

        Assert.Contains("\"bad\"\"srv; DROP SERVER x; --\"", pgDdl);
        Assert.Contains("TYPE 'type''inject'", pgDdl);
        Assert.Contains("VERSION E'1\\\\2'", pgDdl);
        Assert.Contains("host 'db''; DROP TABLE t; --'", pgDdl);
        Assert.DoesNotContain("hunter1", pgDdl);
        Assert.DoesNotContain("hunter2", pgDdl);
        Assert.DoesNotContain("hunter3", pgDdl);
        Assert.DoesNotContain("malformed_no_equals", pgDdl);

        // Oracle: names containing " are refused (Oracle has no escape inside quoted identifiers); other metacharacters are quoted,
        // host single quotes are doubled, password is never read, and SYS.LINK$ is never referenced.
        Assert.Throws<ArgumentException>(() => OracleScripter.BuildDbLinkCreate(true, "BAD\"LINK", "APP_USER", "host"));
        var oraDdl = OracleScripter.BuildDbLinkCreate(true, "bad; DROP USER hr", "APP_USER", "host'; DROP USER hr; --");
        Assert.Contains("CREATE PUBLIC DATABASE LINK \"bad; DROP USER hr\"", oraDdl);
        Assert.Contains("IDENTIFIED BY \"<password>\"", oraDdl);
        Assert.Contains("USING 'host''; DROP USER hr; --'", oraDdl);

        var stampSql = new OracleMetadata().StampSql(new HashSet<OracleMetadata.Piece>());
        Assert.DoesNotContain("SYS.LINK$", stampSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LINK$", stampSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MySql_external_servers_query_never_requests_password_column()
    {
        string? captured = null;
        using var conn = new FakeDbConnection((cmd, _) =>
        {
            captured = cmd.CommandText;
            return Task.FromResult(FakeResult.None);
        });

        _ = await new MySqlLinkedServers().ListServersAsync(conn, CancellationToken.None);
        Assert.NotNull(captured);
        Assert.Contains("mysql.servers", captured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", captured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT *", captured, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rpc_boundary_rejects_oversized_or_malformed_inputs_as_invalid_before_session_lookup()
    {
        await using var host = new TestHost(new FakeDbProvider());
        var bigSession = new string('s', LanguageService.MaxNameLength + 1);
        var bigName = new string('n', ObjectService.MaxLinkedNameLength + 1);

        // No session was opened; if validation ran after session lookup these would throw "notFound" instead of "invalid".
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedServers(new SessionRef { SessionId = "" }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedServers(new SessionRef { SessionId = bigSession }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedServers(new SessionRef { SessionId = "sid\0bad" }))).Code);

        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedCatalogs(new LinkedServerRef { SessionId = bigSession, ServerName = "ERP" }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedCatalogs(new LinkedServerRef { SessionId = "missing", ServerName = bigName }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedCatalogs(new LinkedServerRef { SessionId = "missing", ServerName = "ERP\0" }))).Code);

        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedTables(new LinkedTablesParams { SessionId = bigSession, ServerName = "ERP" }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedTables(new LinkedTablesParams { SessionId = "missing", ServerName = "ERP", Catalog = bigName }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedTables(new LinkedTablesParams { SessionId = "missing", ServerName = "ERP", Catalog = "db\0" }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedTables(new LinkedTablesParams { SessionId = "missing", ServerName = "ERP", Limit = 0 }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaLinkedTables(new LinkedTablesParams { SessionId = "missing", ServerName = "ERP", Limit = ObjectService.MaxLinkedTables + 1 }))).Code);

        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaTestLinkedServer(new LinkedServerRef { SessionId = bigSession, ServerName = "ERP" }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.MetaTestLinkedServer(new LinkedServerRef { SessionId = "missing", ServerName = "   " }))).Code);
    }

    sealed class SecretBearingLinkedProvider : ILinkedServerProvider
    {
        public Task<LinkedServersResult> ListServersAsync(DbConnection connection, CancellationToken ct) =>
            Task.FromResult(new LinkedServersResult
            {
                Servers = [new LinkedServerInfo
                {
                    Name = "ERP",
                    Product = "Driver;Password=hunter1",
                    Provider = "OLEDB;Pwd=hunter2",
                    DataSource = "Server=db;Password=hunter3;",
                    Catalog = "cat;token=hunter4",
                    Note = "Auth;ApiKey=hunter5",
                    CanBrowse = true,
                    CanTest = true,
                }],
                Note = "Authorization: Bearer hunter6",
            });

        public Task<LinkedCatalogInfo[]> ListCatalogsAsync(DbConnection connection, string serverName, CancellationToken ct) =>
            Task.FromResult<LinkedCatalogInfo[]>([new LinkedCatalogInfo { Name = "ErpDb", Description = "Password=hunter7;" }]);

        public Task<LinkedTablesResult> ListTablesAsync(DbConnection connection, string serverName, string? catalog, int limit, CancellationToken ct) =>
            Task.FromResult(new LinkedTablesResult
            {
                Tables = [new LinkedTableInfo { Schema = "dbo", Name = "Orders", Kind = LinkedTableKind.Table }],
                Note = "Pwd=hunter8;",
            });

        public Task<LinkedTestResult> TestAsync(DbConnection connection, string serverName, CancellationToken ct) =>
            Task.FromResult(new LinkedTestResult { Ok = false, ElapsedMs = 12, Error = "Login failed; Password=hunter9;" });
    }

    [Fact]
    public async Task All_linked_server_rpc_replies_scrub_embedded_secrets()
    {
        var fake = new FakeDbProvider { LinkedServers = new SecretBearingLinkedProvider() };
        await using var host = new TestHost(fake);
        var p = await host.Rpc.ProfileSave(new Profile { Id = "", Name = "ms", Engine = Engine.Mssql, Host = "h" });
        var sid = (await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id })).SessionId;

        var servers = await host.Rpc.MetaLinkedServers(new SessionRef { SessionId = sid });
        var s = Assert.Single(servers.Servers);
        Assert.DoesNotContain("hunter1", s.Product ?? "");
        Assert.DoesNotContain("hunter2", s.Provider ?? "");
        Assert.DoesNotContain("hunter3", s.DataSource ?? "");
        Assert.DoesNotContain("hunter4", s.Catalog ?? "");
        Assert.DoesNotContain("hunter5", s.Note ?? "");
        Assert.DoesNotContain("hunter6", servers.Note ?? "");

        var cats = await host.Rpc.MetaLinkedCatalogs(new LinkedServerRef { SessionId = sid, ServerName = "ERP" });
        Assert.DoesNotContain("hunter7", Assert.Single(cats).Description ?? "");

        var tables = await host.Rpc.MetaLinkedTables(new LinkedTablesParams { SessionId = sid, ServerName = "ERP", Catalog = "ErpDb" });
        Assert.DoesNotContain("hunter8", tables.Note ?? "");

        var test = await host.Rpc.MetaTestLinkedServer(new LinkedServerRef { SessionId = sid, ServerName = "ERP" });
        Assert.DoesNotContain("hunter9", test.Error ?? "");
    }
}
