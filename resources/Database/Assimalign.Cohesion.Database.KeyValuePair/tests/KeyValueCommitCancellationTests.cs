using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class KeyValueCommitCancellationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Commit: pre-cancellation preserves the transaction; cancellation during explicit or automatic commit preserves durable success (#1246)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_CancellationAtEndBoundary_ShouldPreserveOutcome(bool explicitTransaction)
    {
        // Arrange: only successful durable flushes survive the simulated reopen.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = true };
        await using var engine = KeyValueDatabaseEngine.Create("commit-cancellation", new()
        {
            StorageStrategy = strategy,
            Durability = StorageCommitDurability.Synchronous,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);

        using var canceled = new CancellationTokenSource();
        int flushes = 0;
        void CancelDuringCommit()
        {
            flushes++;
            canceled.Cancel();
        }
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(token) : null;
        if (transaction is not null)
        {
            using var early = new CancellationTokenSource();
            early.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(early.Token));
            transaction.State.ShouldBe(TransactionState.Active);
            session.CurrentTransaction.ShouldBeSameAs(transaction);
        }

        // Act: the journal callback cancels after the commit record has been appended.
        try
        {
            if (!explicitTransaction) { FaultInjectingJournalStorageStrategy.BeforeCommitFlush = CancelDuringCommit; }
            token = canceled.Token;
            var outcome = await database.PutAsync(session, new byte[] { 1 }, new byte[] { 42 }, cancellationToken: token);
            outcome.Applied.ShouldBeTrue();
            outcome.ETag.ShouldNotBeNull();
            if (transaction is not null)
            {
                FaultInjectingJournalStorageStrategy.BeforeCommitFlush = CancelDuringCommit;
                await transaction.CommitAsync(token);
            }
        }
        finally { FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null; }

        // Assert: both the outcome and the session remain usable, and the write survives reopen.
        canceled.IsCancellationRequested.ShouldBeTrue();
        flushes.ShouldBe(1);
        if (transaction is not null) { transaction.State.ShouldBe(TransactionState.Committed); }
        session.CurrentTransaction.ShouldBeNull();
        token = timeout.Token;
        await VerifyAsync(database, token);
        await session.DisposeAsync();
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test", token);
        await VerifyAsync(reopened, token);
    }

    private static async Task VerifyAsync(KeyValueDatabase database, CancellationToken token)
    {
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        var entry = await database.GetAsync(session, new byte[] { 1 }, token);
        entry.ShouldNotBeNull();
        entry.Value.Value.ToArray().ShouldBe(new byte[] { 42 });
    }
}
