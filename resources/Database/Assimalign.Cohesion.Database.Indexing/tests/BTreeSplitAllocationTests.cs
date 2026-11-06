#if !DEBUG
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Tests;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

[Collection(TimingCollection.Name)]
public sealed class BTreeSplitAllocationTests
{
    private readonly ITestOutputHelper _output;

    public BTreeSplitAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: empty-payload midpoint insertion retains its base allocation budget (#1809)")]
    [InlineData(1024, 7, 32664)]
    [InlineData(16, 183, 34656)]
    public async Task InsertVersionAsync_EmptyPayloadMidpointSplit_ShouldRetainBaseAllocationBudget(int keyLength, int count, long ceiling)
    {
        // Arrange: fill one leaf with even keys; the odd insertion lands at its split slot.
        var keys = new IndexKey[count];
        for (int i = 0; i < count; i++) { keys[i] = Key(2 * i); }
        await MeasureAsync();

        // Act: minimum of three fresh-tree rounds after warmup, including the incoming key and insertion.
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, await MeasureAsync()); }

        // Assert: e54e9465a's Release ceilings, including the existing split and storage work.
        _output.WriteLine($"{RuntimeInformation.ProcessArchitecture}, {keyLength}-byte key: midpoint split = {minimum:N0} bytes; base ceiling = {ceiling:N0} bytes.");
        minimum.ShouldBeLessThanOrEqualTo(ceiling);

        IndexKey Key(int value)
        {
            var bytes = new byte[keyLength];
            bytes.AsSpan().Fill(0x5A);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(keyLength - sizeof(int)), value);
            return new IndexKey(bytes);
        }

        async Task<long> MeasureAsync()
        {
            // Reserve stream capacity before measurement so journal growth cannot mask a split regression.
            await using var harness = new IndexTestHarness(new SimulatedDurableFileHandle(new MemoryStream(1 << 20)), new SimulatedDurableFileHandle(new MemoryStream(1 << 20)));
            var setup = await harness.BeginAsync();
            var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_split_allocation"));
            await harness.CommitAsync(setup);
            using var build = harness.Storage.BeginTransaction();
            for (int i = 0; i < count; i++)
            {
                await index.InsertVersionAsync(build, keys[i], (ulong)i, TransactionSequence.None, TransactionSequence.None, CancellationToken.None);
            }
            build.Commit();
            BTreeShape.Read(harness.Storage, index.RootPageId).Height.ShouldBe(1);
            using var mutation = harness.Storage.BeginTransaction();

            long before = GC.GetAllocatedBytesForCurrentThread();
            index.InsertVersionAsync(mutation, Key(count - 2), 999, TransactionSequence.None, TransactionSequence.None, CancellationToken.None).GetAwaiter().GetResult();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            var shape = BTreeShape.Read(harness.Storage, index.RootPageId);
            shape.Height.ShouldBe(2);
            shape.LeafCounts.ShouldBe(new[] { (count + 1) / 2, (count + 1) / 2 });
            mutation.Rollback();
            return allocated;
        }
    }
}
#endif
