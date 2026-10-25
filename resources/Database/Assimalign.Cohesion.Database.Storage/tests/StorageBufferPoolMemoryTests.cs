using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageBufferPoolMemoryTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Buffer pool resize: a partially failed shrink releases buffers beyond its recovered capacity (found during #1541)")]
    public void Resize_WriteBackFailsAfterOneEviction_ShouldReleaseSurplusRecycledBuffers()
    {
        using var stream = StorageStream.FromInMemory();
        stream.SetLength(16L * Page.Size);
        using var pool = new StorageBufferPool(16);
        var buffers = LoadBuffers(pool, stream, dirty: true);
        var failure = new IOException("Injected WAL gate failure.");
        int attempts = 0;
        pool.WriteAheadGate = _ => { if (++attempts == 2) { throw failure; } };

        Should.Throw<IOException>(() => pool.Resize(4, stream)).ShouldBeSameAs(failure);
        attempts.ShouldBe(2);
        pool.WriteAheadGate = null;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        pool.Capacity.ShouldBe(15);
        pool.Count.ShouldBe(15);
        CountLive(buffers).ShouldBe(15);
        pool.CheckInvariants();
        GC.KeepAlive(pool);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Buffer pool resize: shrinking releases recycled buffers beyond the new capacity (found during #1541)")]
    [InlineData(1)]
    [InlineData(4)]
    public void Resize_SmallerCapacity_ShouldReleaseSurplusRecycledBuffers(int capacity)
    {
        // Arrange: weak references observe actual retained buffers, not process-wide heap noise.
        using var stream = StorageStream.FromInMemory();
        stream.SetLength(16L * Page.Size);
        using var pool = new StorageBufferPool(16);
        var buffers = LoadBuffers(pool, stream);

        // Act
        pool.Resize(capacity, stream);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert: detached, unpinned buffers must not keep the historical capacity alive.
        CountLive(buffers).ShouldBe(capacity);
        pool.Capacity.ShouldBe(capacity);
        pool.Count.ShouldBe(capacity);
        pool.CheckInvariants();
        GC.KeepAlive(pool);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<byte[]>> LoadBuffers(StorageBufferPool pool, StorageStream stream, bool dirty = false)
    {
        var references = new List<WeakReference<byte[]>>();
        for (long id = 0; id < 16; id++)
        {
            using var handle = pool.Pin((PageId)id, stream);
            if (dirty)
            {
                var page = handle.Page;
                page.Id = id;
                page.Type = PageType.Data;
                page.Lsn = 1;
                handle.MarkDirty();
            }
            references.Add(new WeakReference<byte[]>(handle.Entry.Buffer));
        }
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountLive(List<WeakReference<byte[]>> references)
    {
        int count = 0;
        foreach (var reference in references) { if (reference.TryGetTarget(out _)) { count++; } }
        return count;
    }
}
