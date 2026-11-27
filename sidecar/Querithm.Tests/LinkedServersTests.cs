using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Host;
using Querithm.Providers.MySql;
using Querithm.Providers.Oracle;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Unit and security tests for Linked Servers, Foreign Servers, Database Links and MySQL external servers across
/// all applicable providers, Core RPC boundary validation, secret scrubbing and failure/timeout recovery.
/// </summary>
public class LinkedServersTests
{
    [Fact]
    public async Task SqlServer_linked_servers_lists_and_browses_with_parameterized_calls_and_row_cap()
    {
        var seen = new List<(string Sql, Dictionary<string, object?> Params)>();
        using var conn = new FakeDbConnection((cmd, _) =>
        {
            var ps = new Dictionary<string, object?>();
            foreach (DbParameter p in cmd.Parameters) ps[p.ParameterName] = p.Value;
            seen.Add((cmd.CommandText, ps));

            if (cmd.CommandText.Contains("sys.servers", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new FakeResult([FakeResult.Table(
                    [("name", typeof(string)), ("product", typeof(string)), ("provider", typeof(string)), ("data_source", typeof(string)), ("catalog", typeof(string))],
                    ["ERP_LINK", "SQL Server", "MSOLEDBSQL", "10.0.1.5", "ErpProd"])]));
            }
            if (cmd.CommandText.Contains("sp_catalogs", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new FakeResult([FakeResult.Table(
                    [("CATALOG_NAME", typeof(string)), ("DESCRIPTION", typeof(string))],
                    ["ErpProd", "Main ERP"], ["ArchiveDb", null])]));
            }
            if (cmd.CommandText.Contains("sp_tables_ex", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new FakeResult([FakeResult.Table(
                    [("TABLE_CAT", typeof(string)), ("TABLE_SCHEM", typeof(string)), ("TABLE_NAME", typeof(string)), ("TABLE_TYPE", typeof(string))],
                    ["ErpProd", "dbo", "sysdiagrams", "SYSTEM TABLE"],
                    ["ErpProd", "dbo", "Invoices", "TABLE"],
                    ["ErpProd", "dbo", "v_Open", "VIEW"],
                    ["ErpProd", "dbo", "ExtraTable", "TABLE"])]));
            }
            return Task.FromResult(FakeResult.None);
        });

        var lp = new SqlServerLinkedServers();
        var servers = await lp.ListServersAsync(conn, CancellationToken.None);
        var srv = Assert.Single(servers.Servers);
        Assert.Equal("ERP_LINK", srv.Name);
        Assert.True(srv.CanBrowse);
        Assert.True(srv.CanTest);
        Assert.Equal("SQL Server · 10.0.1.5 · ErpProd", srv.Note);

        var catalogs = await lp.ListCatalogsAsync(conn, "ERP_LINK", CancellationToken.None);
        Assert.Equal(["ArchiveDb", "ErpProd"], catalogs.Select(c => c.Name).ToArray());
        Assert.Equal("ERP_LINK", seen[^1].Params["@server"]);

        var tables = await lp.ListTablesAsync(conn, "ERP_LINK", "ErpProd", limit: 2, CancellationToken.None);
        Assert.True(tables.Truncated);
        Assert.NotNull(tables.Note);
        Assert.Equal([("dbo", "Invoices", LinkedTableKind.Table), ("dbo", "v_Open", LinkedTableKind.View)],
            tables.Tables.Select(t => (t.Schema, t.Name, t.Kind)).ToArray());
        Assert.Equal("ERP_LINK", seen[^1].Params["@server"]);
        Assert.Equal("ErpProd", seen[^1].Params["@catalog"]);

        var test = await lp.TestAsync(conn, "ERP_LINK", CancellationToken.None);
        Assert.True(test.Ok);
        Assert.Equal("ERP_LINK", seen[^1].Params["@server"]);
    }

    [Fact]
    public async Task SqlServer_linked_catalogs_falls_back_to_virtual_default_catalog_when_provider_returns_no_catalogs()
    {
        using var conn = new FakeDbConnection((_, _) => Task.FromResult(FakeResult.None));
        var cats = await new SqlServerLinkedServers().ListCatalogsAsync(conn, "ORACLE_OLEDB", CancellationToken.None);
        var single = Assert.Single(cats);
        Assert.Equal("", single.Name);
        Assert.Equal(SqlServerLinkedServers.DefaultCatalogDescription, single.Description);
    }

    [Fact]
    public async Task Refactor_service_refuses_dbLink_and_foreignServer_with_clear_reason()
    {
        await using var host = new TestHost();
        var p = await host.Rpc.ProfileSave(new Profile { Id = "", Name = "pg", Engine = Engine.Pg, Host = "h" });
        var sid = (await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "app" })).SessionId;
        host.Services.Metadata.Store.ApplyRefresh(p.Id, "app", true,
            [
                new ObjectStamp(1, "", "crm_srv", ObjectKind.ForeignServer, 1, null),
                new ObjectStamp(-5, "APP", "HR_LINK", ObjectKind.DbLink, 1, null),
            ], [], new MetadataBundle([], [], [], []), "16");
        host.Services.Metadata.Cache.Invalidate(p.Id, "app");

        var fsEx = await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.RefactorFindUsages(new FindUsagesParams { SessionId = sid, Database = "app", Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = "", Name = "crm_srv" } }));
        Assert.Equal("refactorTarget", fsEx.Code);
        Assert.Contains("crm_srv is a foreign server", fsEx.Message);
        Assert.DoesNotContain(".crm_srv", fsEx.Message);

        var dlEx = await Assert.ThrowsAsync<QuerithmException>(() =>
            host.Rpc.RefactorFindUsages(new FindUsagesParams { SessionId = sid, Database = "app", Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = "APP", Name = "HR_LINK" } }));
        Assert.Equal("refactorTarget", dlEx.Code);
        Assert.Contains("APP.HR_LINK is a database link", dlEx.Message);
    }

    [Fact]
    public async Task MySql_linked_servers_never_selects_password_column()
    {
        string? sql = null;
        using var conn = new FakeDbConnection((cmd, _) =>
        {
            sql = cmd.CommandText;
            return Task.FromResult(new FakeResult([FakeResult.Table(
                [("Server_name", typeof(string)), ("Host", typeof(string)), ("Db", typeof(string)), ("Username", typeof(string)), ("Port", typeof(int)), ("Socket", typeof(string)), ("Wrapper", typeof(string)), ("Owner", typeof(string))],
                ["billing_srv", "10.0.3.4", "billing", "app", 3306, "", "mysql", ""])]));
        });

        var res = await new MySqlLinkedServers().ListServersAsync(conn, CancellationToken.None);
        Assert.NotNull(sql);
        Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
        var srv = Assert.Single(res.Servers);
        Assert.Equal("billing_srv", srv.Name);
        Assert.Equal("mysql · 10.0.3.4:3306/billing", srv.Note);
        Assert.False(srv.CanBrowse);
        Assert.False(srv.CanTest);
    }

    [Fact]
    public void Postgres_foreign_server_options_allowlist_drops_credentials_and_unknown_keys()
    {
        var raw = new[] { "host=db.internal", "port=5432", "dbname=crm", "password=hunter2", "sslkey=/etc/ssl/private.key", "custom_token=tok1" };
        var safe = PostgresMetadata.SafeForeignOptions(raw, out var omitted);
        Assert.True(omitted);
        Assert.Equal([("host", "db.internal"), ("port", "5432"), ("dbname", "crm")], safe);

        var note = PostgresMetadata.FormatForeignServerNote("postgres_fdw", raw);
        Assert.Equal("postgres_fdw · db.internal:5432/crm", note);
        Assert.DoesNotContain("hunter2", note);
        Assert.DoesNotContain("private.key", note);

        var ddl = PostgresScripter.BuildForeignServerCreate("crm_srv", "postgres_fdw", null, null, raw);
        Assert.Contains("-- Non-public or credential options are omitted for safety.", ddl);
        Assert.Contains("CREATE SERVER crm_srv", ddl);
        Assert.Contains("FOREIGN DATA WRAPPER postgres_fdw", ddl);
        Assert.DoesNotContain("hunter2", ddl);
        Assert.DoesNotContain("sslkey", ddl);
        Assert.DoesNotContain("custom_token", ddl);
    }

    [Fact]
    public void Oracle_db_links_appear_in_stamp_sql_assemble_cleanly_and_script_without_passwords()
    {
        var meta = new OracleMetadata();
        var withLinks = meta.StampSql(new HashSet<OracleMetadata.Piece>());
        Assert.Contains("ALL_DB_LINKS", withLinks);
        Assert.DoesNotContain("SYS.LINK$", withLinks, StringComparison.OrdinalIgnoreCase);

        var withoutLinks = meta.StampSql(new HashSet<OracleMetadata.Piece> { OracleMetadata.Piece.DbLinks });
        Assert.DoesNotContain("ALL_DB_LINKS", withoutLinks);

        var assembled = OracleMetadata.Assemble([
            new OracleMetadata.StampRow(-42, "APP", "HR_PROD", "DATABASE LINK", 101, null, null, "VALID", "L:HR@hrdb:1521/HRPDB"),
            new OracleMetadata.StampRow(-43, "PUBLIC", "DW_LINK", "DATABASE LINK", 102, null, null, "VALID", "L:(DESCRIPTION=\n  (ADDRESS=(HOST=dwdb))\n)"),
        ]);
        Assert.Equal(2, assembled.Count);
        Assert.Equal((ObjectKind.DbLink, "HR@hrdb:1521/HRPDB"), (assembled[0].Kind, assembled[0].Note));
        Assert.Equal((ObjectKind.DbLink, "(DESCRIPTION= (ADDRESS=(HOST=dwdb)) )"), (assembled[1].Kind, assembled[1].Note));

        var pubDdl = OracleScripter.BuildDbLinkCreate(true, "DW_LINK", "DW_USER", "dwdb:1521/DW");
        Assert.Contains("CREATE PUBLIC DATABASE LINK DW_LINK", pubDdl);
        Assert.Contains("IDENTIFIED BY \"<password>\"", pubDdl);
        Assert.Contains("USING 'dwdb:1521/DW'", pubDdl);

        var privDdl = OracleScripter.BuildDbLinkCreate(false, "HR_PROD", null, "o'host");
        Assert.Contains("CREATE DATABASE LINK HR_PROD", privDdl);
        Assert.DoesNotContain("PUBLIC", privDdl);
        Assert.Contains("USING 'o''host'", privDdl);
    }

    sealed class FaultyLinkedProvider : ILinkedServerProvider
    {
        public Func<CancellationToken, Task<LinkedServersResult>>? OnList { get; init; }
        public Func<CancellationToken, Task<LinkedTestResult>>? OnTest { get; init; }

        public Task<LinkedServersResult> ListServersAsync(DbConnection connection, CancellationToken ct) =>
            OnList != null ? OnList(ct) : Task.FromResult(new LinkedServersResult { Servers = [] });

        public Task<LinkedTestResult> TestAsync(DbConnection connection, string serverName, CancellationToken ct) =>
            OnTest != null ? OnTest(ct) : Task.FromException<LinkedTestResult>(new NotSupportedException("Testing external servers is not supported on this engine."));
    }

    [Fact]
    public async Task Rpc_boundary_validates_inputs_scrubs_secrets_maps_unsupported_and_bounds_remote_waits()
    {
        var fake = new FakeDbProvider
        {
            LinkedServers = new FaultyLinkedProvider
            {
                OnList = _ => Task.FromResult(new LinkedServersResult
                {
                    Servers = [new LinkedServerInfo
                    {
                        Name = "ERP",
                        Provider = "MSDASQL;Pwd=hunter1;",
                        DataSource = "Server=h;Password=hunter2;",
                        Catalog = "db;token=hunter3",
                        Note = "Pwd=hunter4;host=h",
                        CanBrowse = false,
                        CanTest = true,
                    }],
                    Note = "Access issue with token=hunter5",
                }),
                OnTest = _ => Task.FromResult(new LinkedTestResult { Ok = false, ElapsedMs = 5, Error = "Login failed with Password=hunter6;" }),
            },
        };
        var pgNoLinked = new FakeDbProvider().As(Engine.Pg, new Querithm.Intelligence.PgSql.PgLanguageService(), transactions: new FakePgTransactions());
        pgNoLinked.Traits = new() { Name = "PostgreSQL", DefaultAuth = AuthKind.Password, AuthKinds = [AuthKind.Password], Locations = [LocationKind.Server], NameMatch = StringComparer.Ordinal };
        await using var host = new TestHost(fake, pgNoLinked);
        var pMs = await host.Rpc.ProfileSave(new Profile { Id = "", Name = "ms", Engine = Engine.Mssql, Host = "h" });
        var pPg = await host.Rpc.ProfileSave(new Profile { Id = "", Name = "pg", Engine = Engine.Pg, Host = "h" });
        var sMs = (await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = pMs.Id })).SessionId;
        var sPg = (await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = pPg.Id })).SessionId;

        // Secret scrubbing on returned Product, Provider, DataSource, Catalog, Note and Test Error
        var list = await host.Rpc.MetaLinkedServers(new SessionRef { SessionId = sMs });
        var srv = Assert.Single(list.Servers);
        Assert.DoesNotContain("hunter1", srv.Provider ?? "");
        Assert.DoesNotContain("hunter2", srv.DataSource ?? "");
        Assert.DoesNotContain("hunter3", srv.Catalog ?? "");
        Assert.DoesNotContain("hunter4", srv.Note ?? "");
        Assert.DoesNotContain("hunter5", list.Note ?? "");

        var tested = await host.Rpc.MetaTestLinkedServer(new LinkedServerRef { SessionId = sMs, ServerName = "ERP" });
        Assert.False(tested.Ok);
        Assert.DoesNotContain("hunter6", tested.Error ?? "");

        // Engine without LinkedServers degrades cleanly with code "unsupported"
        var unsupported = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.MetaLinkedServers(new SessionRef { SessionId = sPg }));
        Assert.Equal("unsupported", unsupported.Code);

        // Default NotSupportedException from ILinkedServerProvider maps to "unsupported"
        var noBrowse = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.MetaLinkedCatalogs(new LinkedServerRef { SessionId = sMs, ServerName = "ERP" }));
        Assert.Equal("unsupported", noBrowse.Code);

        // Input validation at RPC boundary
        var badName = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.MetaLinkedCatalogs(new LinkedServerRef { SessionId = sMs, ServerName = "   " }));
        Assert.Equal("invalid", badName.Code);
        var nulName = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.MetaLinkedCatalogs(new LinkedServerRef { SessionId = sMs, ServerName = "ERP\0X" }));
        Assert.Equal("invalid", nulName.Code);
        var badLimit = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.MetaLinkedTables(new LinkedTablesParams { SessionId = sMs, ServerName = "ERP", Limit = 99_999 }));
        Assert.Equal("invalid", badLimit.Code);

        // Bounded remote wait: a hung linked-server probe ends with "timeout"
        fake.LinkedServers = new FaultyLinkedProvider { OnTest = async ct => { await Task.Delay(Timeout.Infinite, ct); return new LinkedTestResult { Ok = true }; } };
        var fastRpc = new RpcServer(host.Services, () => { }) { LinkedServerDeadline = TimeSpan.FromMilliseconds(150) };
        var timedOut = await Assert.ThrowsAsync<QuerithmException>(() => fastRpc.MetaTestLinkedServer(new LinkedServerRef { SessionId = sMs, ServerName = "ERP" }));
        Assert.Equal("timeout", timedOut.Code);
    }
}
