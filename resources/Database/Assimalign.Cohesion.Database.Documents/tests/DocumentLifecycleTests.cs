using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentLifecycleTests
{
    [Theory]
    [InlineData(true, IsolationLevel.Snapshot)]
    [InlineData(false, IsolationLevel.Snapshot)]
    [InlineData(true, IsolationLevel.ReadCommitted)]
    [InlineData(false, IsolationLevel.ReadCommitted)]
    public async Task Ended_waiter_releases_its_late_writer_grant(bool disposeSession, IsolationLevel isolation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("lifecycle", timeout.Token);
        var collection = await database.CreateCollectionAsync("items", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var waiting = await database.CreateSessionAsync(timeout.Token);
        await using var observer = await database.CreateSessionAsync(timeout.Token);
        await using var firstTransaction = await first.BeginTransactionAsync(timeout.Token);
        await collection.PutAsync(first, "first", "1"u8.ToArray(), cancellationToken: timeout.Token);
        await using var waitingTransaction = await waiting.BeginTransactionAsync(isolation, timeout.Token);

        // Put executes synchronously until the existing writer forces its first
        // asynchronous wait, so no timing delay is needed to establish the race.
        var pending = collection.PutAsync(waiting, "waiting", "2"u8.ToArray(), cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        if (disposeSession)
        {
            await waiting.DisposeAsync();
        }
        else
        {
            await waitingTransaction.RollbackAsync(timeout.Token);
        }
        waitingTransaction.State.ShouldBe(TransactionState.RolledBack);
        await firstTransaction.CommitAsync(timeout.Token);

        await Should.ThrowAsync<DatabaseException>(async () => await pending.WaitAsync(timeout.Token));
        var saved = await collection.PutAsync(observer, "after", "3"u8.ToArray(), cancellationToken: timeout.Token);
        saved.Content.ToArray().ShouldBe("3"u8.ToArray());
        (await collection.GetAsync(observer, "waiting", timeout.Token)).ShouldBeNull();
        (await collection.GetAsync(observer, "first", timeout.Token)).ShouldNotBeNull();
    }
}
