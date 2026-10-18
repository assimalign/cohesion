using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentScanAllocationTests
{
    private readonly ITestOutputHelper _output;

    public DocumentScanAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Scan: selective WHERE stays within 10% of a borrowed-root control")]
    public async Task Execute_SelectiveScan_ShouldAllocateWithinBaselineAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("scan-allocations", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None))
        {
            for (int i = 0; i < 10000; i++)
            {
                string json = "{\"name\":\"entry\",\"keep\":" + (i == 0 ? "true" : "false") +
                    ",\"padding\":\"" + new string('p', 256) + "\"}";
                await collection.PutAsync(session, i.ToString("D5"), Encoding.UTF8.GetBytes(json), cancellationToken: CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        var request = DocumentQueryRequest.FromOql("SELECT d.name FROM items d WHERE d.name = 'entry' AND d.keep = TRUE");
        for (int i = 0; i < 2; i++)
        {
            (await ScanBorrowedRootsAsync()).ShouldBe(1);
            await using var warm = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        }

        // Act: execution must finish synchronously so the thread allocation count is complete.
        long controlAllocated = long.MaxValue;
        long allocated = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var control = ScanBorrowedRootsAsync();
            long controlSample = GC.GetAllocatedBytesForCurrentThread() - before;
            control.IsCompletedSuccessfully.ShouldBeTrue();
            (await control).ShouldBe(1);
            controlAllocated = Math.Min(controlAllocated, controlSample);

            before = GC.GetAllocatedBytesForCurrentThread();
            var execution = session.ExecuteAsync(request, CancellationToken.None);
            long sample = GC.GetAllocatedBytesForCurrentThread() - before;
            execution.IsCompletedSuccessfully.ShouldBeTrue();
            await using var result = (DatabaseResultSet)await execution;
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { row.GetString(0).ShouldBe("entry"); count++; }
            count.ShouldBe(1);
            allocated = Math.Min(allocated, sample);
        }

        // Assert: the control scans the same catalog/storage snapshot, reads and parses every
        // document, and applies the equivalent predicate directly to borrowed JSON roots.
        // It includes the platform/configuration-dependent catalog, storage and JSON costs.
        // d1183905e cloned all 10,000 roots and made an expansion iterator/stack per document
        // before WHERE, unlike this control. The clones alone copy nearly 3 MB of JSON plus
        // metadata and object headers, exceeding 10% of the roughly 31 MB common scan cost.
        _output.WriteLine($"10,000-document selective scan allocated {allocated:N0} bytes; borrowed-root control {controlAllocated:N0} bytes.");
        allocated.ShouldBeLessThanOrEqualTo((long)(controlAllocated * 1.10));

        ValueTask<int> ScanBorrowedRootsAsync()
            => database.RunAsync(session, operation =>
            {
                var metadata = database.Catalog.FindCollection("items", operation.Context.Snapshot)!.Value;
                var candidates = database.Catalog.GetDocuments(metadata.Id, null, operation.Context.Snapshot);
                int count = 0;
                foreach (var entry in candidates.OrderBy(entry => entry.Id, StringComparer.Ordinal))
                {
                    operation.EnsureActive();
                    var document = database.ReadDocument(entry);
                    using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
                    var root = json.RootElement;
                    if (root.GetProperty("name").GetString() == "entry" && root.GetProperty("keep").GetBoolean()) { count++; }
                }
                return ValueTask.FromResult(count);
            }, CancellationToken.None);
    }
}
