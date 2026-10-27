using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class StorageCorruptionTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Conformance: a corrupt page fails the operation with database, page and CK06003")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Read_CorruptPage_ShouldReportDatabaseAndPage(int wrapper)
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
        if (wrapper != 0)
        {
            faults.WrapCorruption = corruption =>
            {
                var coded = new DatabaseException("Storage read failed.", [new Diagnostic
                { Code = "CB06004", Message = "Storage read failed.", Severity = DiagnosticSeverity.Error }], corruption);
                return wrapper == 1 ? coded : new AggregateException(new InvalidOperationException("Cleanup failed."), coded);
            };
        }

        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseCorruptionException>(async () =>
            {
                await using var input = await container.OpenReadAsync("one", CancellationToken.None);
                await input.CopyToAsync(System.IO.Stream.Null, CancellationToken.None);
            });

            // Assert
            failure.Code.ShouldBe("CK06003");
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

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Open: wrapped corruption retains the area and model diagnostics")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_WrappedCorruption_ShouldRetainModelDiagnostic(bool aggregate)
    {
        // Arrange: a coded storage-open failure, optionally grouped with a cleanup failure.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = BlobDatabaseEngine.Create("corruption-engine", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("corrupt", CancellationToken.None);
        await database.DisposeAsync();
        var cause = new StorageCorruptionException((PageId)9L, "Page 9 failed checksum verification.");
        var coded = new DatabaseException("Storage open failed.", [new Diagnostic
        { Code = "CB06004", Message = "Storage open failed.", Severity = DiagnosticSeverity.Error }], cause);
        strategy.OpenFailure = aggregate ? new AggregateException(new InvalidOperationException("Cleanup failed."), coded) : coded;

        // Act
        var failure = await Should.ThrowAsync<DatabaseCorruptionException>(() => engine.OpenDatabaseAsync("corrupt", CancellationToken.None).AsTask());

        // Assert: a failed open publishes nothing, and the files remain usable after the fault clears.
        failure.Code.ShouldBe("CK06003");
        failure.PageId.ShouldBe((PageId)9L);
        failure.InnerException.ShouldBeSameAs(cause);
        failure.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldBe(["CK06003", "CB06004"]);
        strategy.OpenFailure = null;
        (await engine.OpenDatabaseAsync("corrupt", CancellationToken.None)).Name.ShouldBe((DatabaseName)"corrupt");
    }
}
