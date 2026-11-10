using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Oracle;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// Phase D0g / merge model: engines as data for the profile editor, and providers that ship hidden behind the dev setting —
/// not offered, not used to read connection strings, their profiles unsupported until exposed.
/// </summary>
public class EngineRegistryTests
{
    [Fact]
    public async Task System_engines_lists_each_exposed_engine_with_its_defaults()
    {
        await using var host = new TestHost();
        var engines = await host.Rpc.Engines();
        var ms = engines.Single(e => e.Engine == Engine.Mssql);
        var pg = engines.Single(e => e.Engine == Engine.Pg);
        Assert.True(ms.Exposed && pg.Exposed);
        Assert.Equal((Dialect.Tsql, 1433, AuthKind.Sql, "dbo"), (ms.Dialect, ms.DefaultPort, ms.DefaultAuth, ms.DefaultSchema));
        Assert.Equal((Dialect.Pg, 5432, AuthKind.Password, "postgres"), (pg.Dialect, pg.DefaultPort, pg.DefaultAuth, pg.DefaultDatabase));
        Assert.Equal([LocationKind.Server], pg.Locations);
    }

    [Theory]
    [InlineData(null, new[] { Engine.Pg })]
    [InlineData("", new[] { Engine.Pg })]
    [InlineData("pg", new Engine[0])]
    [InlineData(" PG , mssql ", new Engine[0])]
    [InlineData("1,oracle", new[] { Engine.Pg })]
    public void The_dev_setting_exposes_engines_behind_the_flag(string? setting, Engine[] stillHidden) =>
        Assert.Equal(stillHidden, ProviderRegistry.HiddenEngines([Engine.Pg], setting));

    [Fact]
    public void The_shipped_build_keeps_Oracle_behind_the_flag_and_every_other_engine_exposed()
    {
        Assert.Equal([Engine.Oracle], EngineSetup.BehindFlag);
        var dir = Directory.CreateTempSubdirectory("qreg-").FullName;
        try
        {
            var shipped = EngineSetup.Providers(dir);
            // One provider per engine, and every engine of the contract has one.
            Assert.Equal(Enum.GetValues<Engine>().Order(), shipped.Select(p => p.Engine).Order());
            var registry = new ProviderRegistry(shipped, ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, null));
            Assert.Null(registry.TryGet(Engine.Oracle));
            Assert.Null(registry.ForDialect(Dialect.Oracle));
            Assert.Equal(Engine.Oracle, Assert.Single(registry.Hidden).Engine);
            Assert.DoesNotContain(registry.All, p => p.Engine == Engine.Oracle);
            Assert.Equal(5, registry.All.Count());
            // The dev setting exposes it.
            var exposed = new ProviderRegistry(shipped, ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle"));
            Assert.IsType<OracleProvider>(exposed.TryGet(Engine.Oracle));
            Assert.Empty(exposed.Hidden);
        }
        finally { Directory.Delete(dir, true); }
    }

    static async Task<(Services Services, RpcServer Rpc, string Dir)> HostWithHiddenOracle()
    {
        var dir = Directory.CreateTempSubdirectory("qhide-").FullName;
        var registry = new ProviderRegistry(EngineSetup.Providers(dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, null));
        var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, registry, dir);
        await Task.CompletedTask;
        return (services, new RpcServer(services, () => { }), dir);
    }

    [Fact]
    public async Task Oracle_is_listed_hidden_and_its_profiles_never_open()
    {
        var (services, rpc, dir) = await HostWithHiddenOracle();
        await using var _ = services;
        var engines = await rpc.Engines();
        Assert.False(engines.Single(e => e.Engine == Engine.Oracle).Exposed);
        Assert.All(engines.Where(e => e.Engine != Engine.Oracle), e => Assert.True(e.Exposed));
        var profile = new Profile { Id = "", Name = "ora", Engine = Engine.Oracle, Host = "h", Port = 1521, Auth = AuthKind.Password, Username = "u", DatabaseDefault = "FREEPDB1" };
        Assert.Equal("invalidProfile", (await Assert.ThrowsAsync<QuerithmException>(() => rpc.ProfileSave(profile))).Code);
        var stored = services.Profiles.Save(profile);
        Assert.NotNull(Assert.Single(await rpc.ProfileList()).Unsupported);
        Assert.Equal("unsupportedProfile", Assert.Throws<QuerithmException>(() => services.Sessions.Open(new SessionOpenParams { ProfileId = stored.Id })).Code);
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("Data Source=//db.example:1521/FREEPDB1;User Id=scott")]
    [InlineData("User Id=scott;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db.example)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=FREEPDB1)))")]
    public async Task Oracle_never_reads_a_pasted_connection_string_while_hidden(string connectionString)
    {
        var (services, rpc, dir) = await HostWithHiddenOracle();
        await using var _ = services;
        var basis = new Profile { Id = "", Name = "", Engine = Engine.Mssql, Auth = AuthKind.Sql };
        // Another engine's builder may take some of these keywords; Oracle never answers (no auto-detection while hidden).
        try
        {
            var r = await rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = connectionString });
            Assert.NotEqual(Engine.Oracle, r.Profile.Engine);
        }
        catch (QuerithmException) { /* no exposed engine reads it */ }
        Directory.Delete(dir, true);
    }

    static async Task<(Services Services, RpcServer Rpc, string Dir)> HostWithHiddenPg()
    {
        var registry = new ProviderRegistry([new SqlServerProvider(), new PostgresProvider()], hidden: [Engine.Pg]);
        var dir = Directory.CreateTempSubdirectory("qhide-").FullName;
        var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, registry, dir);
        await Task.CompletedTask;
        return (services, new RpcServer(services, () => { }), dir);
    }

    [Fact]
    public async Task A_hidden_engine_is_listed_as_hidden_and_its_profiles_never_open()
    {
        var (services, rpc, dir) = await HostWithHiddenPg();
        await using var _ = services;
        var engines = await rpc.Engines();
        Assert.False(engines.Single(e => e.Engine == Engine.Pg).Exposed);
        Assert.True(engines.Single(e => e.Engine == Engine.Mssql).Exposed);
        Assert.Null(services.Sessions.Providers.TryGet(Engine.Pg));
        var save = await Assert.ThrowsAsync<QuerithmException>(() => rpc.ProfileSave(new Profile { Id = "", Name = "pg", Engine = Engine.Pg, Host = "h", Auth = AuthKind.Password }));
        Assert.Equal("invalidProfile", save.Code);
        var stored = services.Profiles.Save(new Profile { Id = "", Name = "pg", Engine = Engine.Pg, Host = "h", Auth = AuthKind.Password });
        Assert.NotNull(Assert.Single(await rpc.ProfileList()).Unsupported);
        Assert.Equal("unsupportedProfile", Assert.Throws<QuerithmException>(() => services.Sessions.Open(new SessionOpenParams { ProfileId = stored.Id })).Code);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task A_hidden_engine_never_reads_a_pasted_connection_string()
    {
        var (services, rpc, dir) = await HostWithHiddenPg();
        await using var _ = services;
        var basis = new Profile { Id = "", Name = "", Engine = Engine.Mssql, Auth = AuthKind.Sql };
        // A PostgreSQL URI: with PostgreSQL hidden only SQL Server's builder reads it, and it cannot.
        await Assert.ThrowsAsync<QuerithmException>(() => rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = "postgres://u@h:5432/db" }));
        var ms = await rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = "Server=h,1433;Database=d;User ID=u" });
        Assert.Equal(Engine.Mssql, ms.Profile.Engine);
        Directory.Delete(dir, true);
    }
}
