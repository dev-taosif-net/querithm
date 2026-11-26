using System.Runtime.CompilerServices;

namespace Querithm.Contract.Providers;

/// <summary>
/// Completion documentation a dialect service builds only for the items the answer keeps (completion audit 2026-11-25,
/// M4): on a 10,000-table schema T-SQL tier 2 built a column preview for every table on every request, then sent at most
/// 500 of them (<c>CompletionFilter</c>). A service attaches a builder to an item it returns; the filter runs it for the
/// items it keeps. Keyed by the item instance (a copy made with <c>with</c> has none), held weakly, never serialized.
/// </summary>
public static class DeferredDocs
{
    static readonly ConditionalWeakTable<CompletionItem, Func<string?>> Builders = new();

    /// <summary>Attaches <paramref name="build"/> to <paramref name="item"/> and returns the item.</summary>
    public static CompletionItem Defer(CompletionItem item, Func<string?> build)
    {
        Builders.AddOrUpdate(item, build);
        return item;
    }

    /// <summary>
    /// The item with its documentation built, when it was deferred and has none yet; otherwise the item itself. A builder
    /// that fails leaves that one item without documentation: before, it failed the whole completion (review 2026-11-26).
    /// </summary>
    public static CompletionItem Resolve(CompletionItem item)
    {
        if (item.Documentation != null || !Builders.TryGetValue(item, out var build)) return item;
        string? doc;
        try { doc = build(); }
        catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException)) { doc = null; }
        return doc == null ? item : item with { Documentation = doc };
    }
}
