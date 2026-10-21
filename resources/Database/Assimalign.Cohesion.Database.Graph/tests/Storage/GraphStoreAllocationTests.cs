using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

[CollectionDefinition(nameof(GraphStoreAllocationTests), DisableParallelization = true)]
[Collection(nameof(GraphStoreAllocationTests))]
public sealed class GraphStoreAllocationTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Update: retained history does not cause quadratic decoding allocation (#1149)")]
    public async Task Update_RepeatedInOneTransaction_ShouldBoundHistoryAllocation()
    {
        // Arrange
        using var storage = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "allocation");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var transaction = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        ulong id = (ulong)storage.ReserveTransactionSequence();
        await store.CreateNodeAsync(new StoredGraphNode(id, ["N"], new Dictionary<string, object?> { ["value"] = 0 }), transaction);
        long before = GC.GetTotalAllocatedBytes(true);

        // Act
        for (int i = 1; i <= 2_000; i++)
        { await store.UpdateNodeAsync(id, ["N"], new Dictionary<string, object?> { ["value"] = i }, transaction, CancellationToken.None); }
        long allocated = GC.GetTotalAllocatedBytes(true) - before;

        // Assert
        store.FindNode(id, transaction.Snapshot)!.Value.Properties["value"].ShouldBe(2_000);
        allocated.ShouldBeLessThan(128L * 1024 * 1024);
        await coordinator.RollbackAsync(transaction);
    }
}
