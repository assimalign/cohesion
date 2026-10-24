using System;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageBufferPoolAllocationTests
{
    private readonly ITestOutputHelper _output;

    public StorageBufferPoolAllocationTests(ITestOutputHelper output) => _output = output;

#if !DEBUG
    [Fact(DisplayName = "Cohesion Test [Storage] - Cold pins: uncontended faults allocate no completion object")]
    public void Pin_UncontendedColdFaults_ShouldAllocateOnlyTheHandleAndMappingNode()
    {
        // Arrange: every access faults, with stable recycled frames and verified bytes.
        using var stream = StorageStream.FromInMemory();
        stream.SetLength(8L * Page.Size);
        using var pool = new StorageBufferPool(4);
        for (long id = 0; id < 8; id++)
        {
            using var handle = pool.PinForOverwrite((PageId)id, stream);
            var page = handle.Page; page.Id = id; page.Type = PageType.Data;
            page.AsBodySpan().Fill((byte)(id + 1));
            handle.MarkDirty();
        }
        pool.FlushAll(stream);
        for (int i = 0; i < 4096; i++) { using var handle = pool.Pin((PageId)(long)(i % 8), stream); }
        var samples = new long[12];
        long digest = 0;

        // Act
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4096; i++)
            {
                using var handle = pool.Pin((PageId)(long)(i % 8), stream);
                digest += handle.Page.Id + handle.Page.AsBodySpan()[0] + handle.Page.AsBodySpan()[^1];
            }
            samples[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Assert
        foreach (long bytes in samples) { _output.WriteLine($"Cold fault: {bytes / 4096d} B/read"); }
        digest.ShouldBe(614400L);
        samples.ShouldAllBe(bytes => bytes == 96L * 4096);
        pool.CheckInvariants();
    }
#endif
}
