using System.Globalization;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// meta.foreignKeys (completion audit 2026-11-25, H12): the cached foreign keys declared on the given tables, with a
/// session's own view's, which the webview patches into a snapshot after an incremental refresh. Untested until M12.
/// Also the store's id lists under a culture whose minus sign is not ASCII (G#10).
/// </summary>
public sealed class MetaForeignKeysTests
{
    static ObjectStamp T(long id, string name) => new(id, "main", name, ObjectKind.Table, id, null);
    static FkRow Fk(string name, long parent, long referenced) => new(name, parent, referenced, [new FkColumnPair { Parent = "ref_id", Referenced = "id" }]);
    static MetadataBundle Fks(params FkRow[] fks) => new([], [], [], fks);

    static (MetadataService Metadata, SchemaCache Cache, MetadataStore Store, Session Session) Rig(bool memory)
    {
        var local = LocalStore.InMemory();
        var profiles = new ProfileStore(local);
        var provider = new FakeDbProvider { Lifetime = ConnectionLifetime.SessionOwned };
        var profile = profiles.Save(memory
            ? new Profile { Id = "", Name = "mem", Engine = Engine.Mssql, Auth = AuthKind.None, Location = LocationKind.Memory, DatabaseDefault = "main" }
            : new Profile { Id = "", Name = "file", Engine = Engine.Mssql, Auth = AuthKind.Windows, Host = "h", DatabaseDefault = "main" });
        var sessions = new SessionManager(profiles, new ProviderRegistry([provider]), new CapturingNotifier(), Log.Null);
        var store = new MetadataStore(local);
        var cache = new SchemaCache(store);
        var metadata = new MetadataService(sessions, store, cache, new CapturingNotifier(), Log.Null);
        var s = sessions.Get(sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Database = "main" }).SessionId);
        cache.AddScope(s.Id, profile.Id, s.InMemory);
        return (metadata, cache, store, s);
    }

    [Fact]
    public void The_foreign_keys_declared_on_the_given_tables_come_back_and_no_others()
    {
        var (metadata, _, store, s) = Rig(memory: false);
        var pid = s.Profile.Id;
        store.ApplyRefresh(pid, "main", true, [T(10, "orders"), T(20, "customers"), T(30, "lines")], [],
            Fks(Fk("fk_orders_customers", 10, 20), Fk("fk_lines_orders", 30, 10)), "1");
        ForeignKeysParams P(params long[] ids) => new() { ProfileId = pid, Database = "main", Ids = ids };

        Assert.Equal(["fk_orders_customers"], metadata.ForeignKeys(P(10)).Select(f => f.Name));
        Assert.Empty(metadata.ForeignKeys(P(20))); // referenced only: an FK belongs to the table it is declared on
        Assert.Equal(["fk_lines_orders", "fk_orders_customers"], metadata.ForeignKeys(P(10, 30)).Select(f => f.Name).Order());
        Assert.Empty(metadata.ForeignKeys(P()));
        Assert.Empty(metadata.ForeignKeys(P(99)));
        var fk = metadata.ForeignKeys(P(10)).Single();
        Assert.Equal((10, 20), (fk.Parent, fk.Referenced));
        Assert.Equal(("ref_id", "id"), (fk.Columns.Single().Parent, fk.Columns.Single().Referenced));
    }

    [Fact]
    public void A_file_sessions_view_adds_its_own_foreign_keys_to_the_shared_ones()
    {
        var (metadata, cache, store, s) = Rig(memory: false);
        var pid = s.Profile.Id;
        store.ApplyRefresh(pid, "main", true, [T(10, "orders"), T(20, "customers")], [], Fks(Fk("fk_shared", 10, 20)), "1");
        // The tab's own temporary table, with an FK of its own (its view only: other tabs never see it).
        cache.Ephemeral.ApplyRefresh(pid, SchemaCache.SessionDatabase("main", s.Id), true, [T(40, "scratch")], [], Fks(Fk("fk_own", 40, 10)), "1");

        var scoped = metadata.ForeignKeys(new ForeignKeysParams { ProfileId = pid, Database = "main", Ids = [10, 40], SessionId = s.Id });
        Assert.Equal(["fk_own", "fk_shared"], scoped.Select(f => f.Name).Order());
        var shared = metadata.ForeignKeys(new ForeignKeysParams { ProfileId = pid, Database = "main", Ids = [10, 40] });
        Assert.Equal(["fk_shared"], shared.Select(f => f.Name));
    }

    [Fact]
    public void An_in_memory_sessions_view_is_all_there_is()
    {
        var (metadata, cache, store, s) = Rig(memory: true);
        var pid = s.Profile.Id;
        // Nothing of an in-memory database is in the shared store; a row there (an older build, a bug) is never shown.
        store.ApplyRefresh(pid, "main", true, [T(10, "orders"), T(20, "customers")], [], Fks(Fk("fk_stray", 10, 20)), "1");
        cache.Ephemeral.ApplyRefresh(pid, SchemaCache.SessionDatabase("main", s.Id), true, [T(10, "orders"), T(20, "customers")], [], Fks(Fk("fk_mem", 10, 20)), "1");

        var scoped = metadata.ForeignKeys(new ForeignKeysParams { ProfileId = pid, Database = "main", Ids = [10], SessionId = s.Id });
        Assert.Equal(["fk_mem"], scoped.Select(f => f.Name));
    }

    [Fact]
    public void A_scoped_request_names_a_live_session_of_the_same_profile()
    {
        var (metadata, _, _, s) = Rig(memory: true);
        Assert.Equal("notFound", Assert.Throws<QuerithmException>(() =>
            metadata.ForeignKeys(new ForeignKeysParams { ProfileId = s.Profile.Id, Database = "main", Ids = [1], SessionId = "01JUNKNOWN" })).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() =>
            metadata.ForeignKeys(new ForeignKeysParams { ProfileId = "another", Database = "main", Ids = [1], SessionId = s.Id })).Code);
    }

    /// <summary>A culture whose minus sign is U+2212, as sv-SE, fi-FI and nb-NO have under ICU (built here so the test does not depend on the OS's data).</summary>
    static CultureInfo UnicodeMinus()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.NumberFormat.NegativeSign = "−";
        return c;
    }

    [Fact]
    public void Id_lists_read_the_same_under_a_culture_whose_minus_sign_is_not_ascii()
    {
        // G#10: ids were joined into the SQL (and the JSON) with the current culture: `IN (−1)`, the empty list's sentinel,
        // and `[−5]` were syntax / JSON errors, so meta.objects for no ids, meta.foreignKeys for a negative id and a
        // refresh that removed one failed.
        var store = new MetadataStore(LocalStore.InMemory());
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = UnicodeMinus();
        try
        {
            Assert.Equal("−5", (-5L).ToString()); // the premise
            store.ApplyRefresh("p", "db", true, [T(1, "a"), T(2, "b")], [], Fks(Fk("fk_a_b", 1, 2)), "1");
            Assert.Empty(store.Load("p", "db", []).Objects);
            Assert.Single(store.Load("p", "db", [1]).Objects);
            Assert.Empty(store.ForeignKeysOf("p", "db", [-5]));
            Assert.Single(store.ForeignKeysOf("p", "db", [1]));
            store.ApplyRefresh("p", "db", false, [], [-7, 2], Fks(), "1"); // an incremental refresh that removed objects
            Assert.Equal(["a"], store.Load("p", "db").Objects.Select(o => o.Name));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
