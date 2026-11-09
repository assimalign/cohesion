using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed partial class StorageOwnerChainTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Index owners enumerate in bounded independent batches (#1808)")]
    public void GetIndexOwners_MultipleOwners_ShouldReturnBoundedOrderedSnapshots()
    {
        // Arrange.
        using var storage = ChainStorage.Create(new CrashSimulationStream(), new CrashSimulationStream(), poolCapacity: 64);
        var expected = new Dictionary<ulong, PageId>();
        using (var bracket = storage.BeginTransaction())
        {
            for (ulong owner = 140; owner > 10; owner--)
            {
                using var page = storage.AllocatePageForWrite(bracket, PageType.Index, ownerId: owner);
                page.MarkDirty(); expected.Add(owner, page.Id);
            }
            bracket.Commit();
        }

        // Act.
        var actual = new List<(ulong OwnerId, PageId Page)>();
        ulong after = 0;
        while (true)
        {
            var batch = storage.GetIndexOwners(after, 7);
            batch.Count.ShouldBeLessThanOrEqualTo(7);
            if (batch.Count == 0) { break; }
            actual.AddRange(batch); after = batch[^1].OwnerId;
        }

        // Assert.
        actual.ShouldBe(expected.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)));
        storage.GetIndexOwners(after).ShouldBeEmpty();
        Should.Throw<ArgumentOutOfRangeException>(() => storage.GetIndexOwners(0, 0));
        var snapshot = storage.GetIndexOwners(0, 1);
        using (var bracket = storage.BeginTransaction()) { storage.FreeOwnerPages(bracket, 11); bracket.Commit(); }
        snapshot.ShouldHaveSingleItem().OwnerId.ShouldBe(11UL);
        storage.GetIndexOwners(0, 1).ShouldHaveSingleItem().OwnerId.ShouldBe(12UL);
    }
}
