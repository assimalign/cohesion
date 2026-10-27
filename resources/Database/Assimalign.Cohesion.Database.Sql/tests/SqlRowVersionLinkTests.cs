using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL upsert concurrency")]
public sealed class SqlRowVersionLinkTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Lineage: pruning distinct or overwritten links releases spare backing capacity (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Prune_LargeBurstWithFewSurvivors_ShouldTrimStorage(bool overwritten)
    {
        // Arrange
        var links = new SqlRowVersionLinks();
        for (ulong id = 1; id <= 16384; id++) { links.Add(overwritten ? 1 : id, id + 1, new(1)); }
        for (ulong id = 20000; id < 20008; id++) { links.Add(id, id + 1, new(2)); }

        // Act: a subsequent refill must allocate again after the spare arrays were released.
        links.Prune(new(2));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong id = 1; id <= 4096; id++) { links.Add(overwritten ? 1 : id, id + 1, new(3)); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        links.Count.ShouldBe(overwritten ? 9 : 4104);
        links.TryGet(20000, new(2), out var successor).ShouldBeTrue();
        successor.ShouldBe(20001UL);
        allocated.ShouldBeGreaterThan(100_000);
    }
}
