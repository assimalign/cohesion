using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[CollectionDefinition("Document index concurrency", DisableParallelization = true)]
public sealed class DocumentIndexConcurrencyCollection { }

[Collection("Document index concurrency")]
public sealed class DocumentIndexConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Waiting writers check latest UNIQUE state and snapshot conflicts (#1135)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutAsync_WaitingWriter_ShouldCheckLatestStateAsync(bool sameDocument)
    {
        // Arrange: exercise real writer-lock contention, with no timing-dependent release.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = DocumentDatabaseEngine.Create("index-concurrency", new());
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.CreateCollectionAsync("items", timeout.Token);
        await first.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a,b)", cancellationToken: timeout.Token);
        var one = await first.GetCollectionAsync("items", timeout.Token);
        var two = await second.GetCollectionAsync("items", timeout.Token);
        int blocked = 0, refused = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            await using var writer = await first.BeginTransactionAsync(timeout.Token);
            await using var stale = await second.BeginTransactionAsync(timeout.Token);
            string id = "one" + blocked;
            await one.PutAsync(first, id, "{\"a\":1,\"b\":2}"u8.ToArray(), cancellationToken: timeout.Token);
            var pending = two.PutAsync(second, sameDocument ? id : "other", "{\"a\":1,\"b\":2}"u8.ToArray(), cancellationToken: timeout.Token).AsTask();
            pending.IsCompleted.ShouldBeFalse();
            blocked++;

            // Act: the waiting writer resumes with its old snapshot after this commit.
            await writer.CommitAsync(timeout.Token);
            if (sameDocument) { await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending); }
            else { await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await pending); }
            refused++;
            await stale.RollbackAsync(timeout.Token);
            await one.DeleteAsync(first, id, cancellationToken: timeout.Token);
        } while ((elapsed.Elapsed < TimeSpan.FromSeconds(1) || blocked < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(20));

        // Assert: every observed contention exercised the refusal path.
        blocked.ShouldBeGreaterThanOrEqualTo(2);
        refused.ShouldBe(blocked);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - A newly committed index aborts a stale writer (#1135)")]
    public async Task PutAsync_IndexSetChanged_ShouldTakeConflictPathAsync()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("index-conflict", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var old = await database.CreateSessionAsync();
        await old.CreateCollectionAsync("items");
        var collection = await old.GetCollectionAsync("items");
        await using var transaction = await old.BeginTransactionAsync();
        await using var ddl = await database.CreateSessionAsync();
        await ddl.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a,b)");

        // Act / Assert.
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await collection.PutAsync(old, "stale", "{\"a\":1,\"b\":2}"u8.ToArray()));
        await transaction.RollbackAsync();
        await collection.PutAsync(old, "fresh", "{\"a\":1,\"b\":2}"u8.ToArray());
    }
}
