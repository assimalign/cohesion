using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// A rollback whose undo the journal rejects (#1226): the transaction ends, the writer keeps the
/// database writer lock until the version-purge pass completes the undo, a late operation of the
/// ended transaction cannot hand that lock to the next writer early, and an undo that still fails
/// when the engine closes leaves nothing of the transaction after the next open.
/// </summary>
public sealed class DocumentTransactionFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: a late operation of a rolled-back transaction does not release its deferred writer lock")]
    public async Task LockWriterAsync_LateOperationOfRolledBackTransaction_ShouldNotReleaseItsDeferredWriterLock()
    {
        // Arrange: a rollback whose undo cannot write its journal bracket, and a waiting writer.
        await using var engine = DocumentDatabaseEngine.Create(QuietOptions(new FaultInjectingJournalDocumentStorageStrategy()));
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var transaction = (DocumentDatabaseTransaction)await session.BeginTransactionAsync();
        await collection.PutAsync(session, "rolled", "1"u8.ToArray());
        int unspent;
        using (var failures = FaultInjectingJournalDocumentStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }

        int deferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        var waiting = collection.PutAsync(other, "other", "2"u8.ToArray()).AsTask();

        // Act: the late operation gets the lock its transaction still holds, finds the
        // transaction ended, and cleans up; then the purge pass completes the undo.
        var late = await Should.ThrowAsync<DatabaseException>(async () => await database.LockWriterAsync(transaction.Context, CancellationToken.None));
        await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(250)));
        bool otherProceededBeforeTheUndo = waiting.IsCompleted;
        database.Coordinator.Checkpoint();
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await waiting.WaitAsync(Timeout);

        // Assert
        unspent.ShouldBe(0);
        deferred.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        late.Message.ShouldContain("ended while waiting for the writer lock");
        otherProceededBeforeTheUndo.ShouldBeFalse();
        (await collection.GetAsync(other, "rolled")).ShouldBeNull();
        (await collection.GetAsync(other, "other")).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: an undo that still fails at close is scrubbed at the next open")]
    public async Task Dispose_UndoStillFailsAtClose_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange
        var strategy = new FaultInjectingJournalDocumentStorageStrategy();
        var engine = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using (var session = await database.CreateSessionAsync())
        {
            await collection.PutAsync(session, "kept", "1"u8.ToArray());
            var transaction = await session.BeginTransactionAsync();
            await collection.PutAsync(session, "rolled", "2"u8.ToArray());
            await collection.DeleteAsync(session, "kept");

            // The undo's storage bracket begins (the first write) and fails at its first page
            // image (the second), so the bracket rolls itself back and the undo is deferred.
            using (var failures = FaultInjectingJournalDocumentStorageStrategy.FailJournalWrites(1, skip: 1))
            {
                await transaction.RollbackAsync();
                failures.Remaining.ShouldBe(0);
            }

            transaction.State.ShouldBe(TransactionState.RolledBack);
        }

        // Act: the close retries the undo, which fails the same way.
        AggregateException closeFailure;
        using (var failures = FaultInjectingJournalDocumentStorageStrategy.FailJournalWrites(1, skip: 1))
        {
            closeFailure = Should.Throw<AggregateException>(() => engine.Dispose());
            failures.Remaining.ShouldBe(0);
        }

        await using var reopened = DocumentDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = (IDocumentDatabase)await reopened.OpenDatabaseAsync("test");
        var items = await recovered.GetCollectionAsync("items");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the insert is gone and the delete is undone.
        closeFailure.InnerExceptions.ShouldContain(error => error is IOException);
        (await items.GetAsync(observer, "rolled")).ShouldBeNull();
        (await items.GetAsync(observer, "kept")).ShouldNotBeNull();
    }

    // The engine's own maintenance workers stay out of the way: these tests drive the purge
    // pass and the checkpoint themselves.
    private static DocumentDatabaseEngineOptions QuietOptions(FaultInjectingJournalDocumentStorageStrategy strategy) => new()
    {
        StorageStrategy = strategy,
        MaintenanceInterval = TimeSpan.FromHours(1),
        CheckpointInterval = TimeSpan.FromHours(1),
    };
}
