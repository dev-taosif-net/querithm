namespace Querithm.Tests;

/// <summary>
/// The live managed heap, for tests that bound retained memory. Not <see cref="GC.GetTotalMemory(bool)"/>: on macOS the
/// runtime runs the segments GC (no regions), whose total counts free space between survivors. On CI macos-14 it gave
/// 424–534 B for a 192 B string and 800–1,027 B per 451 B text-store entry, while heap size minus fragmentation (and the
/// GC's own survived-bytes count) gave 192 and 451, the same as Linux and Windows, where all of these agree.
/// </summary>
static class Heap
{
    /// <summary>Bytes held by reachable objects after a full blocking collection: the heap size minus its free space.</summary>
    public static long LiveBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers(); // finalizable objects (and what they hold) go in the next collection
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }
}
