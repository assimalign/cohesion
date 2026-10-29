using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[CollectionDefinition(nameof(DocumentKeyJoinSnapshotCollection), DisableParallelization = true)]
public sealed class DocumentKeyJoinSnapshotCollection;

[Collection(nameof(DocumentKeyJoinSnapshotCollection))]
public sealed class DocumentKeyJoinSnapshotTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: cached and first-time lookups share the statement snapshot during a writer commit")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task ExecuteAsync_ConcurrentWriter_ShouldPreserveStatementSnapshotAsync(IsolationLevel isolation)
    {
        // Arrange: hold the second key evaluation after the first document was cached.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("join-snapshot");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("observe_key", value =>
        {
            Interlocked.Increment(ref calls);
            if (value == 2)
            {
                reached.TrySetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("Join snapshot writer did not release the reader."); }
            }
            return value == 2 ? "b" : "a";
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(writer);
        await using var transaction = await reader.BeginTransactionAsync(isolation, CancellationToken.None);
        var reading = Task.Run(() => DocumentSubqueryTests.RowsAsync(reader,
            "SELECT b.n, META(b).id FROM [1,2,1] AS n JOIN items AS b ON META(b).id = observe_key(n)"));
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Act: change both the cached identity and an identity not looked up yet.
            await writer.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('a',{n:10}), ('b',{n:20})", cancellationToken: CancellationToken.None);
        }
        finally { resume.Set(); }
        var rows = await reading.WaitAsync(TimeSpan.FromSeconds(30));
        // Assert: content and identities use the old horizon for both cache paths.
        rows.Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 2m, 1m });
        rows.Select(row => row.GetValue(1)).ShouldBe(new object?[] { "a", "b", "a" });
        calls.ShouldBe(3);
        reader.LastQueryKeyLookups.ShouldBe(2);
        await transaction.RollbackAsync(CancellationToken.None);
        (await DocumentSubqueryTests.RowsAsync(reader, "SELECT RAW b.n FROM [1] AS a JOIN items AS b ON META(b).id = 'a'")).Single().GetValue(0).ShouldBe(10m);
        reader.LastQueryKeyLookups.ShouldBe(1);
    }
}
