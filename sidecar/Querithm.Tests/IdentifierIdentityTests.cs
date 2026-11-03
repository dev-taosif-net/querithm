using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Metadata;
using Querithm.Core.Monitoring;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Phase D0e: identity keys keep exact names (distinct objects are never merged), name matching follows each engine's
/// traits, and every remaining engine-specific rule table refuses an engine it has no rules for.
/// </summary>
public class IdentifierIdentityTests
{
    static (SessionManager Sessions, FakeDbProvider Provider, Profile Profile) Rig(bool databaseScoped)
    {
        var profiles = new ProfileStore(LocalStore.InMemory());
        var provider = new FakeDbProvider { DatabaseScopedConnections = databaseScoped };
        var profile = profiles.Save(new Profile { Id = "", Name = "p", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Windows });
        return (new SessionManager(profiles, new ProviderRegistry([provider]), new CapturingNotifier(), Log.Null), provider, profile);
    }

    [Fact]
    public async Task Databases_that_differ_only_in_case_never_share_a_pool()
    {
        var (sessions, provider, profile) = Rig(databaseScoped: true);
        await using var _ = sessions;
        Assert.NotSame(sessions.PoolFor(profile, provider, "Sales"), sessions.PoolFor(profile, provider, "sales"));
        var upper = sessions.Get(sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Database = "Sales" }).SessionId);
        var lower = sessions.Get(sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Database = "sales" }).SessionId);
        await sessions.StateAsync(upper.Id, default);
        await sessions.StateAsync(lower.Id, default);
        Assert.Equal(["Sales", "sales"], provider.Opened.Select(c => c.Database).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Snapshots_of_case_distinct_databases_stay_apart()
    {
        var local = LocalStore.InMemory();
        var store = new MetadataStore(local);
        store.ApplyRefresh("p", "Sales", true, [new ObjectStamp(1, "public", "Orders", ObjectKind.Table, 1, null)], [], new MetadataBundle([], [], [], []), "16");
        store.ApplyRefresh("p", "sales", true, [new ObjectStamp(1, "public", "orders", ObjectKind.Table, 1, null)], [], new MetadataBundle([], [], [], []), "16");
        var cache = new SchemaCache(store);
        Assert.Equal("Orders", cache.Get("p", "Sales")!.Objects.Single().Name);
        Assert.Equal("orders", cache.Get("p", "sales")!.Objects.Single().Name);
    }

    [Fact]
    public async Task A_session_adopts_the_servers_spelling_so_later_borrows_skip_the_switch()
    {
        var (sessions, provider, profile) = Rig(databaseScoped: false);
        await using var _ = sessions;
        // The server's collation ignores case and reports its own spelling of the database it switched to.
        provider.CanonicalDatabase = d => string.Equals(d, "sales", StringComparison.OrdinalIgnoreCase) ? "Sales" : d;
        var s = sessions.Get(sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Database = "SALES" }).SessionId);
        await sessions.StateAsync(s.Id, default);
        Assert.Equal("Sales", s.Database);
        var conn = provider.Opened.Single();
        var switches = conn.Log.Count(l => l.StartsWith("USE ", StringComparison.Ordinal));
        var (c, borrowed) = await sessions.AcquireAsync(s, null, default);
        await sessions.ReleaseAsync(s, c, borrowed, TxState.None);
        Assert.Equal(switches, conn.Log.Count(l => l.StartsWith("USE ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Name_matching_follows_each_engines_traits()
    {
        Assert.True(new SqlServerProvider().Traits.NameMatch.Equals("Orders", "ORDERS"));
        Assert.False(new PostgresProvider().Traits.NameMatch.Equals("Orders", "orders"));
        foreach (IDbProvider p in new IDbProvider[] { new SqlServerProvider(), new PostgresProvider() })
        {
            Assert.Contains(p.Traits.DefaultAuth, p.Traits.AuthKinds);
            Assert.NotEmpty(p.Traits.Locations);
            Assert.False(string.IsNullOrEmpty(p.Traits.Name));
        }
        Assert.Equal(("dbo", (string?)null), (new SqlServerProvider().Traits.DefaultSchema, new SqlServerProvider().Traits.DefaultDatabase));
        Assert.Equal(("public", "postgres"), (new PostgresProvider().Traits.DefaultSchema, new PostgresProvider().Traits.DefaultDatabase));
    }

    [Fact]
    public void Rule_tables_refuse_an_engine_they_have_no_rules_for()
    {
        var unknown = (Engine)99;
        Assert.Equal("unsupported", Assert.Throws<QuerithmException>(() => MonitorRules.For(unknown)).Code);
        Assert.NotNull(MonitorRules.For(Engine.Mssql));
        Assert.NotNull(MonitorRules.For(Engine.Pg));
        Assert.False(WaitDictionary.Default.Info(unknown, "LCK_M_X").Known); // never another engine's descriptions
        Assert.True(WaitDictionary.Default.Info(Engine.Mssql, "LCK_M_X").Known);
    }
}
