using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageFreeSpaceMapTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Free map: negative page identities are never allocated (found during #1365)")]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void IsAllocated_NegativeIdentity_ShouldRejectItInEveryAllocationCheck(long value)
    {
        // Arrange
        var map = new StorageFreeSpaceMap();
        map.Allocate().ShouldBe((PageId)0L);
        var page = (PageId)value;

        // Act / Assert
        map.IsAllocated(page).ShouldBeFalse();
        map.TryGetAllocationGeneration(page, out _).ShouldBeFalse();
        map.IsAllocationCurrent(page, 0).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free map: freeing a negative identity cannot poison the next allocation (found during #1365)")]
    public void Free_NegativeIdentity_ShouldRefuseItAndKeepTheAllocatorUnchanged()
    {
        // Arrange
        var map = new StorageFreeSpaceMap();
        map.Allocate().ShouldBe((PageId)0L);
        long before = map.TotalPageCount;

        // Act: observe the next real allocation even if the invalid free was accepted.
        var failure = Record.Exception(() => map.Free((PageId)(-1L)));
        var next = map.Allocate();

        // Assert
        next.ShouldBe((PageId)1L);
        failure.ShouldBeOfType<StorageIOException>();
        map.FreePageCount.ShouldBe(0);
        map.TotalPageCount.ShouldBe(before + 1);
    }
}
