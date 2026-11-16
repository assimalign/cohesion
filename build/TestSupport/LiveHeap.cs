using System;

namespace Assimalign.Cohesion.Testing;

/// <summary>Reads the live managed heap, for tests that bound what an operation retains.</summary>
/// <remarks>
/// <see cref="GC.GetTotalMemory(bool)"/> is not a live reading on every platform. On the macos-26-arm64
/// runner (.NET 10) it adds generations 1 and 2 again on top of heap size minus fragmentation, in 200 of
/// 226 samples to the byte, and that runner's collections did not compact in any of them, so the reading
/// grows with dead space and a retention test fails with no retained object. On win-arm64 the same method
/// returned heap size minus fragmentation in 253 of 261 samples, and that is what this reads directly.
/// </remarks>
internal static class LiveHeap
{
    /// <summary>Forces a full blocking collection, runs finalizers, and collects again.</summary>
    /// <returns>Heap size minus fragmentation of the last full blocking collection, in bytes.</returns>
    public static long Read()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }
}
