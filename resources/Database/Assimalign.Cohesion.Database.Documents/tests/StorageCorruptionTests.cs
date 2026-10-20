using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class StorageCorruptionTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Conformance: a corrupt page fails the operation with database, page and COHDBS003")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_CorruptPage_ShouldReportDatabaseAndPage(bool query)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = DocumentDatabaseEngine.Create("corruption-engine", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("corrupt", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", "{\"value\":1}"u8.ToArray(), cancellationToken: CancellationToken.None);
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
                if (query)
                {
                    var result = (await session.ExecuteAsync("SELECT * FROM items", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
                    await using (result)
                    {
                        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { }
                    }
                }
                else
                {
                    await collection.GetAsync(session, "one", CancellationToken.None);
                }
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