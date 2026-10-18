using System;
using System.Text;
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

    [Fact(DisplayName = "Cohesion Test [Documents] - Scan: selective WHERE retains the pre-UNNEST allocation bound")]
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
        { await using var warm = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None); }

        // Act: execution must finish synchronously so the thread allocation count is complete.
        long allocated = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
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

        // Assert: 252d68847 measured 31,845,080 bytes in Debug and 31,283,400 in
        // Release on win-arm64. Use the lower measured baseline for both configurations.
        _output.WriteLine($"10,000-document selective scan allocated {allocated:N0} bytes.");
        allocated.ShouldBeLessThanOrEqualTo(31_283_400L);
    }
}
