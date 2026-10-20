using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class StorageCorruptionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Conformance: a corrupt page fails the operation with database, page and COHDBS003")]
    public async Task Read_CorruptPage_ShouldReportDatabaseAndPage()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = BlobDatabaseEngine.Create("corruption-engine", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("corrupt", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var container = await session.CreateContainerAsync("files", CancellationToken.None);
        await using (var output = await container.OpenWriteAsync("one", cancellationToken: CancellationToken.None))
        {
            await output.WriteAsync(new byte[1000], CancellationToken.None);
        }
        var storage = database.DataStorage;
        var pageId = storage.GetOwnerPages(1UL << 63).First();
        var other = Enumerable.Range(1, (int)storage.PageManager.PageCount - 1)
            .Select(page => (PageId)(long)page)
            .First(page => page != pageId && storage.FreeSpaceMap.IsAllocated(page));
        int capacity = storage.BufferPoolCapacity;
        storage.BufferPoolCapacity = 1;
        using (storage.PageManager.GetPage(other)) { }
        storage.BufferPoolCapacity = capacity;
        var faults = strategy.Faults("corrupt");
        faults.RottenPage = pageId;

        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseCorruptionException>(async () =>
            {
                await using var input = await container.OpenReadAsync("one", CancellationToken.None);
                await input.CopyToAsync(System.IO.Stream.Null, CancellationToken.None);
            });

            // Assert
            failure.Code.ShouldBe("COHDBS003");
            failure.Database.ShouldBe((DatabaseName)"corrupt");
            failure.PageId.ShouldBe(pageId);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(failure.Code);
            failure.InnerException.ShouldBeOfType<StorageCorruptionException>().PageId.ShouldBe(pageId);
            failure.Message.ShouldContain("corrupt", Case.Sensitive);
            failure.Message.ShouldContain("page " + (long)pageId, Case.Sensitive);
            faults.RottenPageReads.ShouldBeGreaterThan(0);
        }
        finally { faults.Clear(); }
    }
}