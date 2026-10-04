using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// An operation still waiting for the database writer lock when its transaction ends must release
/// the grant it receives later: the lock manager removes grants, not pending requests, when a
/// transaction ends. The Documents engine pins the same rule in <c>DocumentLifecycleTests</c>.
/// </summary>
public sealed class BlobLifecycleTests
{
    /// <summary>An ended waiter releases its late writer grant, so the next writer is not blocked forever.</summary>
    /// <param name="disposeSession">True to end the waiter by closing its session; false to roll its transaction back.</param>
    /// <param name="isolation">The waiter's isolation level.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Lifecycle: an ended waiter releases its late writer grant")]
    [InlineData(true, IsolationLevel.Snapshot)]
    [InlineData(false, IsolationLevel.Snapshot)]
    [InlineData(true, IsolationLevel.ReadCommitted)]
    [InlineData(false, IsolationLevel.ReadCommitted)]
    public async Task Ended_waiter_releases_its_late_writer_grant(bool disposeSession, IsolationLevel isolation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("lifecycle", timeout.Token);
        var container = await database.CreateContainerAsync("files", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var waiting = await database.CreateSessionAsync(timeout.Token);
        var firstFiles = await ((IBlobDatabase)first.Database).GetContainerAsync("files", timeout.Token);
        var waitingFiles = await ((IBlobDatabase)waiting.Database).GetContainerAsync("files", timeout.Token);
        await using var firstTransaction = await first.BeginTransactionAsync(timeout.Token);
        await Write(firstFiles, "first", timeout.Token);
        await using var waitingTransaction = await waiting.BeginTransactionAsync(isolation, timeout.Token);

        // The delete runs synchronously until the existing writer forces its first asynchronous
        // wait, so no timing delay is needed to establish the race.
        var pending = waitingFiles.DeleteAsync("first", timeout.Token).AsTask();
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
        await Write(container, "after", timeout.Token);
        (await container.GetPropertiesAsync("after", timeout.Token)).ShouldNotBeNull();
        (await container.GetPropertiesAsync("first", timeout.Token)).ShouldNotBeNull();
    }

    private static async Task Write(IBlobContainer container, string name, CancellationToken cancellationToken)
    {
        await using var stream = await container.OpenWriteAsync(name, cancellationToken: cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(name), cancellationToken);
    }
}
