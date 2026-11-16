using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

[Collection(DatabaseEngineInventoryCollection.Name)]
public sealed class DatabaseLifecycleAllocationTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Completed create and drop retain the base allocation pin (#1790)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_WithCompletedCore_ShouldAllocateBaseBytes(bool drop)
    {
        // Arrange
        await using var engine = new TestEngine();
        await using var database = new TestDatabase("stored", engine);
        engine.CreateResult = database;
        engine.DropOperation = () => ValueTask.CompletedTask;
        DatabaseName name = "stored";
        for (int index = 0; index < 1000; index++) { Invoke(); }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++) { Invoke(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert: the identical harness is measured against f9cd7efa3 in both configurations.
        allocated.ShouldBe(0);

        void Invoke()
        {
            if (drop) { engine.DropDatabaseAsync(name, CancellationToken.None).GetAwaiter().GetResult(); }
            else { _ = engine.CreateDatabaseAsync(name, CancellationToken.None).GetAwaiter().GetResult(); }
        }
    }
}
