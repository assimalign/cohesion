#if !DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentSetOperationAllocationTests
{
    private readonly ITestOutputHelper _output;

    public DocumentSetOperationAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNION ALL: typed and RAW rows avoid discarded JSON representations")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UnorderedAll_ShouldStayWithinAllocationCeilingAsync(bool raw)
    {
        // Arrange: both nested compounds must inherit the consumer's cell-only demand.
        await using var engine = DocumentDatabaseEngine.Create("set-allocations", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var rows = JsonDocument.Parse("[" + string.Join(",", Enumerable.Repeat("1", 512)) + "]");
        var parameters = new Dictionary<string, object?> { ["rows"] = rows.RootElement };
        string projection = raw ? "RAW 1" : "1 AS n";
        var statement = DocumentQueryRequest.FromOql("(SELECT " + projection + " FROM @rows AS x UNION ALL SELECT " + projection + " FROM [] AS empty) UNION ALL (SELECT " + projection + " FROM @rows AS y UNION ALL SELECT " + projection + " FROM [] AS z)").Statement;
        // Act: snapshot acquisition and test assertions stay outside each measured execution.
        var samples = await database.RunAsync(session, operation =>
        {
            long[] samples = new long[3];
            for (int round = 0; round < 6; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var result = (DatabaseResultSet)DocumentPlanExecutor.ExecuteAsync(database, operation, statement, parameters, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                CountAsync(result).GetAwaiter().GetResult().ShouldBe(1024);
                result.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (round >= 3) { samples[round - 3] = allocated; }
            }
            return ValueTask.FromResult(samples);
        }, CancellationToken.None);
        // Assert: all three warmed rounds have a per-row ceiling; elapsed time never decides the outcome.
        _output.WriteLine($"RAW={raw}: {string.Join(", ", samples.Select(bytes => bytes / 1024))} bytes/row.");
        samples.ShouldAllBe(bytes => bytes <= 1024L * 1900);
    }

    private static async Task<int> CountAsync(DatabaseResultSet result)
    {
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
        return count;
    }
}
#endif
