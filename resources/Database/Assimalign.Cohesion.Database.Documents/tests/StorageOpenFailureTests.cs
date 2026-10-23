using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class StorageOpenFailureTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Open: index registration I/O retains its I/O code and allows retry")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_RegistrationIoFailure_ShouldCodeAndAllowRetry(bool wrapped)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = DocumentDatabaseEngine.Create("open-io", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using (var session = await database.CreateSessionAsync(CancellationToken.None))
        {
            await session.CreateCollectionAsync("items", CancellationToken.None);
            await session.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: CancellationToken.None);
        }
        var registration = database.DataStorage.GetOwnerPages(1).ShouldHaveSingleItem();
        await database.DisposeAsync();
        var faults = strategy.Faults("test");
        faults.RottenPage = registration;
        Exception cause = new IOException("Injected registration device failure.");
        faults.WrapCorruption = _ => wrapped ? new Catalog.DocumentCatalogException("Registration read failed.", cause) : cause;

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("test", CancellationToken.None).AsTask());
        faults.Clear();

        // Assert
        faults.RottenPageReads.ShouldBeGreaterThan(0);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06104");
        engine.TryGetDatabase("test", out _).ShouldBeFalse();
        var reopened = await engine.OpenDatabaseAsync("test", CancellationToken.None);
        await using var reader = await reopened.CreateSessionAsync(CancellationToken.None);
        (await reader.GetCollectionAsync("items", CancellationToken.None)).Name.ShouldBe("items");
    }
}
