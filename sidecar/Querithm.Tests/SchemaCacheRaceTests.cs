using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Metadata;
using Querithm.Core.Store;

namespace Querithm.Tests;

/// <summary>
/// A read that loads a snapshot while a refresh applies and invalidates it must not put the old snapshot back in the
/// cache. Regression: CI (SessionOwnedTests.Two_memory_tabs_with_the_same_object_names_stay_isolated) saw a memory tab's
/// view stay without its new table: the reader loaded before ApplyRefresh, the invalidation found nothing to remove, and
/// GetOrAdd then cached the old view, already marked Fresh, so nothing ever read it again.
/// </summary>
public class SchemaCacheRaceTests
{
    static readonly ObjectStamp T = new(1, "main", "t", ObjectKind.Table, 1, null);

    static void Apply(MetadataStore store, string db) =>
        store.ApplyRefresh("p", db, true, [T], [], new MetadataBundle([], [], [], []), "1");

    [Fact]
    public void A_session_view_loaded_before_a_refresh_is_not_cached_over_it()
    {
        var shared = new MetadataStore(LocalStore.InMemory());
        var cache = new SchemaCache(shared);
        var db = SchemaCache.SessionDatabase("main", "s1");
        cache.AddScope("s1", "p", inMemory: true);
        // The refresh lands between the reader's load and its store into the cache.
        cache.AfterLoad = key =>
        {
            cache.AfterLoad = null;
            Apply(cache.Ephemeral, db);
            cache.InvalidateSession("p", "main", "s1");
        };
        Assert.Empty(cache.SessionView("p", "main", "s1").Objects); // read before the refresh: old, that is fine
        Assert.Equal(["t"], cache.SessionView("p", "main", "s1").Objects.Select(o => o.Name)); // the next read sees it
    }

    [Fact]
    public void A_session_view_is_the_same_instance_until_its_shared_or_own_snapshot_changes()
    {
        // Regression: every request built a new merged record, so tier 2's per-snapshot schema index was rebuilt on each
        // keystroke in SQLite / DuckDB tabs.
        var shared = new MetadataStore(LocalStore.InMemory());
        var cache = new SchemaCache(shared);
        Apply(shared, "main");
        cache.AddScope("s1", "p", inMemory: false);
        cache.Ephemeral.ApplyRefresh("p", SchemaCache.SessionDatabase("main", "s1"), true, [new ObjectStamp(2, "temp", "tmp", ObjectKind.Table, 1, null)], [], new MetadataBundle([], [], [], []), "1");
        var first = cache.Get("p", "main", "s1")!;
        Assert.Same(first, cache.Get("p", "main", "s1"));
        Assert.Equal(["t", "tmp"], first.Objects.Select(o => o.Name));
        cache.Invalidate("p", "main");
        Assert.NotSame(first, cache.Get("p", "main", "s1"));
        cache.AddScope("s2", "p", inMemory: true);
        var mem = cache.Get("p", "main", "s2")!;
        Assert.Same(mem, cache.Get("p", "main", "s2"));
        cache.RemoveScope("s1", "p");
        Assert.Equal(["t"], cache.Get("p", "main", "s1")!.Objects.Select(o => o.Name));
    }

    [Fact]
    public void A_shared_snapshot_loaded_before_a_refresh_is_not_cached_over_it()
    {
        var shared = new MetadataStore(LocalStore.InMemory());
        var cache = new SchemaCache(shared);
        cache.AfterLoad = key =>
        {
            cache.AfterLoad = null;
            Apply(shared, "main");
            cache.Invalidate("p", "main");
        };
        Assert.Empty(cache.Get("p", "main")!.Objects);
        Assert.Equal(["t"], cache.Get("p", "main")!.Objects.Select(o => o.Name));
    }

    [Fact]
    public void A_profile_invalidated_during_a_load_is_not_cached_over_either()
    {
        var shared = new MetadataStore(LocalStore.InMemory());
        var cache = new SchemaCache(shared);
        cache.AfterLoad = key =>
        {
            cache.AfterLoad = null;
            Apply(shared, "main");
            cache.InvalidateProfile("p");
        };
        Assert.Empty(cache.Get("p", "main")!.Objects);
        Assert.Equal(["t"], cache.Get("p", "main")!.Objects.Select(o => o.Name));
    }

    [Fact]
    public void Reads_with_no_refresh_in_between_are_served_from_the_cache()
    {
        var shared = new MetadataStore(LocalStore.InMemory());
        var cache = new SchemaCache(shared);
        var loads = 0;
        cache.AfterLoad = _ => loads++;
        var first = cache.Get("p", "main");
        Assert.Same(first, cache.Get("p", "main"));
        Assert.Equal(1, loads);
    }
}
