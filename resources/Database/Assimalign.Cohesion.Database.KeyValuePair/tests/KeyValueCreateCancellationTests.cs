using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[Collection(nameof(KeyValueDatabaseEventSourceCollection))]
public sealed class KeyValueCreateCancellationTests
{
    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Cancellation after file creation leaves a complete reopenable database (#1808)")]
    public async Task CreateDatabaseAsync_CancelAfterBothFileSets_ShouldCompleteAndReopen()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        int sets = 0;
        strategy.StorageCreated = _ => { if (++sets == 2) { cancellation.Cancel(); } };
        await using var engine = KeyValueDatabaseEngine.Create("create-cancellation", new() { StorageStrategy = strategy });

        // Act.
        var database = await engine.CreateDatabaseAsync("test", cancellation.Token);
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test", guard.Token);

        // Assert.
        sets.ShouldBe(2);
        cancellation.IsCancellationRequested.ShouldBeTrue();
        reopened.Catalog.GetIndexRegistrations().ShouldHaveSingleItem();
        await reopened.DisposeAsync();
        await engine.DropDatabaseAsync("test", guard.Token);
        strategy.StorageExists("test", false).ShouldBeFalse();
        strategy.StorageExists("test", true).ShouldBeFalse();
    }
}
