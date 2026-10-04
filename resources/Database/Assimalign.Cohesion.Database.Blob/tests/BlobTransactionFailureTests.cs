using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// A rollback whose undo the journal rejects at the rollback and again when the engine closes
/// (#1226): the close reports the failure, and the next open's recovery removes everything the
/// rolled-back transaction wrote, because the close kept the journal's classification of it.
/// </summary>
public sealed class BlobTransactionFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Rollback: an undo that still fails at close is scrubbed at the next open")]
    public async Task Dispose_UndoStillFailsAtClose_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange
        var strategy = new FaultInjectingJournalBlobStorageStrategy();
        var engine = BlobDatabaseEngine.Create(QuietOptions(strategy));
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        await database.CreateContainerAsync("files");
        await using (var session = await database.CreateSessionAsync())
        {
            var scoped = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
            await BlobEngineTests.Write(scoped, "kept", "kept"u8.ToArray());
            var transaction = await session.BeginTransactionAsync();
            await BlobEngineTests.Write(scoped, "rolled", "rolled"u8.ToArray());
            await BlobEngineTests.Write(scoped, "kept", "overwritten"u8.ToArray());

            // The undo's storage bracket begins (the first write) and fails at its first page
            // image (the second), so the bracket rolls itself back and the undo is deferred.
            using (var failures = FaultInjectingJournalBlobStorageStrategy.FailJournalWrites(1, skip: 1))
            {
                await transaction.RollbackAsync();
                failures.Remaining.ShouldBe(0);
            }

            transaction.State.ShouldBe(TransactionState.RolledBack);
        }

        // Act: the close retries the undo, which fails the same way.
        AggregateException closeFailure;
        using (var failures = FaultInjectingJournalBlobStorageStrategy.FailJournalWrites(1, skip: 1))
        {
            closeFailure = Should.Throw<AggregateException>(() => engine.Dispose());
            failures.Remaining.ShouldBe(0);
        }

        await using var reopened = BlobDatabaseEngine.Create(QuietOptions(strategy));
        var recovered = (IBlobDatabase)await reopened.OpenDatabaseAsync("test");
        var files = await recovered.GetContainerAsync("files");

        // Assert: the new blob is gone and the overwrite is undone.
        closeFailure.InnerExceptions.ShouldContain(error => error is IOException);
        (await files.GetPropertiesAsync("rolled")).ShouldBeNull();
        (await BlobEngineTests.Read(files, "kept")).ShouldBe("kept"u8.ToArray());
    }

    // The engine's own maintenance workers stay out of the way: the test drives the close itself.
    private static BlobDatabaseEngineOptions QuietOptions(FaultInjectingJournalBlobStorageStrategy strategy) => new()
    {
        StorageStrategy = strategy,
        MaintenanceInterval = TimeSpan.FromHours(1),
        CheckpointInterval = TimeSpan.FromHours(1),
    };
}
