using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

[Collection(DatabaseEngineInventoryCollection.Name)]
public sealed class DatabaseEngineInventoryTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Open inventory copies instances and keeps offline databases (#1790)")]
    public async Task OpenDatabases_WithOpenAndOfflineInstances_ShouldReturnReadOnlySnapshots()
    {
        // Arrange
        await using var engine = new TestEngine();
        var first = await engine.CreateDatabaseAsync("First");
        engine.Offline = ["First"];

        // Act
        var snapshot = engine.OpenDatabases;
        await engine.CreateDatabaseAsync("second");

        // Assert
        snapshot.ShouldBe([first]);
        snapshot.ShouldNotBeSameAs(engine.OpenDatabases);
        ((IList<DatabaseInstance>)snapshot).IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => ((IList<DatabaseInstance>)snapshot).Clear());
        engine.OpenDatabases.Count.ShouldBe(2);
        engine.OfflineDatabases.ShouldBe([(DatabaseName)"First"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Open inventory excludes closing instances without waiting and is empty after disposal (#1790)")]
    public async Task OpenDatabases_DuringClose_ShouldReturnWithoutWaiting()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new TestEngine { CloseGate = release };
        var database = (TestDatabase)await engine.CreateDatabaseAsync("closing", guard.Token);
        var close = database.DisposeAsync().AsTask();
        await database.Closing.WaitAsync(guard.Token);

        // Act
        var snapshot = engine.OpenDatabases;
        release.SetResult();
        await close.WaitAsync(guard.Token);
        await engine.DisposeAsync();

        // Assert
        snapshot.ShouldBeEmpty();
        engine.OpenDatabases.ShouldBeEmpty();
    }
}
