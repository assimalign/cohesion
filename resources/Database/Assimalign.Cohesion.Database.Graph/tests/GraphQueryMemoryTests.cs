using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Internal;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphQueryMemoryTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Cached accounting buffers contain no values and are discarded on session disposal (#1533)")]
    public void Dispose_CachedWorkspace_ShouldKeepNoValueReferences()
    {
        using var pool = new GraphQueryMemoryPool();
        var reference = CacheValue(pool);
        pool.CachedCapacity.ShouldBeGreaterThan(0);
        pool.CachedCapacity.ShouldBeLessThanOrEqualTo(2560);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        reference.TryGetTarget(out _).ShouldBeFalse();
        pool.Dispose();
        pool.CachedCapacity.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Large accounting high-water storage is not retained by the session cache (#1533)")]
    public void Dispose_LargeWorkspace_ShouldDiscardOversizedBuffers()
    {
        using var pool = new GraphQueryMemoryPool();
        var memory = new GraphQueryMemory(2 * 1024 * 1024, pool);
        using var owner = new GraphMemoryOwner(memory);
        owner.RetainValue(Enumerable.Range(0, 2000).Select(value => (object?)new object?[] { value }).ToArray(), CancellationToken.None);
        owner.Dispose();
        memory.RetainedBytes.ShouldBe(0);
        memory.RetainedBufferCapacity.ShouldBe(0);
        pool.HasCachedValueBuffers.ShouldBeFalse();
        pool.CachedCapacity.ShouldBeLessThanOrEqualTo(8 * 128);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Session cache disposal never mutates an actively rented collection (#1533)")]
    public void Dispose_WhileWorkspaceIsRented_ShouldLetOwnershipUnwind()
    {
        using var pool = new GraphQueryMemoryPool();
        var memory = new GraphQueryMemory(65536, pool);
        using var owner = new GraphMemoryOwner(memory);
        var value = new object?[] { new string('v', 1024) };
        owner.RetainValue(value, CancellationToken.None);
        pool.Dispose();
        owner.ReleaseValue(value);
        owner.Dispose();
        memory.RetainedBytes.ShouldBe(0);
        memory.RetainedBufferCapacity.ShouldBe(0);
        memory.ActiveLeases.ShouldBe(0);
        pool.CachedCapacity.ShouldBe(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> CacheValue(GraphQueryMemoryPool pool)
    {
        var memory = new GraphQueryMemory(65536, pool);
        using var owner = new GraphMemoryOwner(memory);
        var value = new object?[] { new string('v', 1024) };
        var reference = new WeakReference<object>(value);
        owner.RetainValue(value, CancellationToken.None);
        owner.Dispose();
        memory.RetainedBytes.ShouldBe(0);
        memory.RetainedBufferCapacity.ShouldBe(0);
        return reference;
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Memory ownership counts a shared value once across operators (#1533)")]
    public void RetainValue_SharedAcrossOperators_ShouldReleaseOnlyAfterLastOwner()
    {
        var memory = new GraphQueryMemory(65536);
        var shared = Enumerable.Range(0, 512).Select(value => (object?)value).ToArray();
        using var first = new GraphMemoryOwner(memory);
        using var second = new GraphMemoryOwner(memory);
        for (int i = 0; i < 50; i++)
        { first.RetainValue(new object?[] { shared }, CancellationToken.None); second.RetainValue(new object?[] { shared }, CancellationToken.None); }
        memory.PeakRetainedBytes.ShouldBeLessThanOrEqualTo(65536);
        first.Dispose();
        memory.RetainedBytes.ShouldBeGreaterThan(0);
        second.Dispose();
        memory.RetainedBytes.ShouldBe(0);
        memory.ActiveLeases.ShouldBe(0);
        memory.RetainedBufferCapacity.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Refused nested value retention unwinds partial ownership and remains reusable (#1533)")]
    public void RetainValue_RefusedMidValue_ShouldUnwindEveryIncrement()
    {
        var memory = new GraphQueryMemory(2048);
        using var owner = new GraphMemoryOwner(memory);
        var error = Should.Throw<GraphPlanningException>(() => owner.RetainValue(
            Enumerable.Range(0, 30).Select(value => (object?)new string((char)('a' + value % 26), 40)).ToArray(), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        memory.RetainedBytes.ShouldBe(0);
        var small = new object?[] { 7L };
        owner.RetainValue(small, CancellationToken.None);
        owner.ReleaseValue(small);
        memory.RetainedBytes.ShouldBe(0);
        owner.Dispose();
        memory.ActiveLeases.ShouldBe(0);
        memory.RetainedBufferCapacity.ShouldBe(0);
    }
}
