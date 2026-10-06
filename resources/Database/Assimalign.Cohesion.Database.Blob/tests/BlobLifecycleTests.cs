using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// A transaction can end on another thread while one of its operations still runs: a caller's
/// rollback, or the session closing. An operation still waiting for the database writer lock then
/// fails and never keeps a grant, and an operation between its physical brackets applies none
/// after the end, so nothing the rolled-back transaction did outlives it. The Documents engine pins
/// the same rules in <c>DocumentLifecycleTests</c>.
/// </summary>
public sealed class BlobLifecycleTests
{
    /// <summary>An operation waiting for the writer lock when its transaction ends never keeps the lock, so the next writer is not blocked.</summary>
    /// <param name="disposeSession">True to end the waiter by closing its session; false to roll its transaction back.</param>
    /// <param name="isolation">The waiter's isolation level.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Lifecycle: an ended waiter never keeps the writer lock")]
    [InlineData(true, IsolationLevel.Snapshot)]
    [InlineData(false, IsolationLevel.Snapshot)]
    [InlineData(true, IsolationLevel.ReadCommitted)]
    [InlineData(false, IsolationLevel.ReadCommitted)]
    public async Task DeleteAsync_TransactionEndsWhileWaitingForWriterLock_ShouldReleaseLateGrant(bool disposeSession, IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("lifecycle", timeout.Token);
        var container = await database.CreateContainerAsync("files", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var waiting = await database.CreateSessionAsync(timeout.Token);
        var firstFiles = await first.GetContainerAsync("files", timeout.Token);
        var waitingFiles = await waiting.GetContainerAsync("files", timeout.Token);
        await using var firstTransaction = await first.BeginTransactionAsync(timeout.Token);
        await Write(firstFiles, "first", timeout.Token);
        await using var waitingTransaction = await waiting.BeginTransactionAsync(isolation, timeout.Token);

        // The delete runs synchronously until the existing writer forces its first asynchronous
        // wait, so no timing delay is needed to establish the race.
        var pending = waitingFiles.DeleteAsync("first", timeout.Token).AsTask();
        bool parked = !pending.IsCompleted;

        // Act
        if (disposeSession)
        {
            await waiting.DisposeAsync();
        }
        else
        {
            await waitingTransaction.RollbackAsync(timeout.Token);
        }
        var stateAfterEnd = waitingTransaction.State;
        await firstTransaction.CommitAsync(timeout.Token);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await pending.WaitAsync(timeout.Token));
        await Write(container, "after", timeout.Token);

        // Assert
        parked.ShouldBeTrue();
        stateAfterEnd.ShouldBe(TransactionState.RolledBack);
        error.ShouldBeAssignableTo<DatabaseTransactionAbortedException>();
        (await container.GetPropertiesAsync("after", timeout.Token)).ShouldNotBeNull();
        (await container.GetPropertiesAsync("first", timeout.Token)).ShouldNotBeNull();
    }

    /// <summary>
    /// A rollback or a session close while a large delete of the transaction is under way leaves the
    /// blob whole through a checkpoint, a reopen and a version-purge pass: the delete applies no
    /// physical bracket once its transaction's end has begun (#1225 review). Before, the delete went
    /// on tombstoning content chunks with the rolled-back sequence; once the checkpoint truncated the
    /// abort record, recovery read those tombstones as committed and the purge reclaimed the content
    /// of a blob that was never deleted.
    /// </summary>
    /// <param name="disposeSession">True to end the transaction by closing its session; false to roll it back.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Lifecycle: ending a transaction under a running delete keeps the blob through restart and purge")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_TransactionEndsWhileDeleteRuns_ShouldKeepBlobThroughRestartAndPurge(bool disposeSession)
    {
        // Arrange: a file-backed database holding a multi-chunk blob, with quiet background workers.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-Blob-lifecycle", Guid.NewGuid().ToString("N"));
        var options = new BlobDatabaseEngineOptions
        {
            RootPath = FileSystemPath.Parse(directory),
            MaintenanceInterval = TimeSpan.FromHours(1),
            CheckpointInterval = TimeSpan.FromHours(1),
        };
        byte[] content = new byte[1024 * 1024];
        new Random(1225).NextBytes(content);
        try
        {
            bool deleteParked;
            Exception deleteError;
            await using (var engine = BlobDatabaseEngine.Create(options))
            {
                var database = await engine.CreateDatabaseAsync("db", timeout.Token);
                var container = await database.CreateContainerAsync("files", timeout.Token);
                await using (var upload = await container.OpenWriteAsync("big", cancellationToken: timeout.Token))
                {
                    await upload.WriteAsync(content, timeout.Token);
                }
                var session = await database.CreateSessionAsync(timeout.Token);
                var files = await session.GetContainerAsync("files", timeout.Token);
                var transaction = await session.BeginTransactionAsync(timeout.Token);

                // Another transaction's statement holds the apply gate, so the delete takes the writer
                // lock and then stops before its first chunk bracket.
                var gateHolder = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
                using var gateHeld = new ManualResetEventSlim();
                using var releaseGate = new ManualResetEventSlim();
                var holding = Task.Factory.StartNew(() => database.Coordinator.ApplyStatementAsync(gateHolder, _ =>
                {
                    gateHeld.Set();
                    releaseGate.Wait(timeout.Token);
                    return 0;
                }).AsTask().GetAwaiter().GetResult(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                gateHeld.Wait(timeout.Token);
                var deleting = files.DeleteAsync("big", timeout.Token).AsTask();
                deleteParked = !deleting.IsCompleted;

                // Act
                if (disposeSession)
                {
                    await session.DisposeAsync();
                }
                else
                {
                    await transaction.RollbackAsync(timeout.Token);
                }
                releaseGate.Set();
                await holding.WaitAsync(timeout.Token);
                deleteError = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await deleting.WaitAsync(timeout.Token));
                await database.Coordinator.RollbackAsync(gateHolder, timeout.Token);
                await session.DisposeAsync();
                database.Coordinator.Checkpoint();
            }

            await using var reopened = BlobDatabaseEngine.Create(options);
            var loaded = await reopened.OpenDatabaseAsync("db", timeout.Token);
            loaded.Coordinator.RunVersionPurgePass(timeout.Token);
            var loadedFiles = await loaded.GetContainerAsync("files", timeout.Token);
            var properties = await loadedFiles.GetPropertiesAsync("big", timeout.Token);
            byte[] stored = await Read(loadedFiles, "big", timeout.Token);

            // Assert
            deleteParked.ShouldBeTrue();
            deleteError.InnerException.ShouldBeOfType<TransactionAbortedException>();
            properties.ShouldNotBeNull().Length.ShouldBe(content.Length);
            stored.AsSpan().SequenceEqual(content).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task Write(BlobContainer container, string name, CancellationToken cancellationToken)
    {
        await using var stream = await container.OpenWriteAsync(name, cancellationToken: cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(name), cancellationToken);
    }

    private static async Task<byte[]> Read(BlobContainer container, string name, CancellationToken cancellationToken)
    {
        await using var stream = await container.OpenReadAsync(name, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
