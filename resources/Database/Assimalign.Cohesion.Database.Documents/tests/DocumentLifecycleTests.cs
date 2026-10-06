using System;
using System.IO;
using System.Text;
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
        var database = await engine.CreateDatabaseAsync("lifecycle", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        var collection = await first.CreateCollectionAsync("items", timeout.Token);
        await using var waiting = await database.CreateSessionAsync(timeout.Token);
        await using var observer = await database.CreateSessionAsync(timeout.Token);
        var waitingItems = await waiting.GetCollectionAsync("items", timeout.Token);
        var observed = await observer.GetCollectionAsync("items", timeout.Token);
        await using var firstTransaction = await first.BeginTransactionAsync(timeout.Token);
        await collection.PutAsync(first, "first", "1"u8.ToArray(), cancellationToken: timeout.Token);
        await using var waitingTransaction = await waiting.BeginTransactionAsync(isolation, timeout.Token);

        // Put executes synchronously until the existing writer forces its first
        // asynchronous wait, so no timing delay is needed to establish the race.
        var pending = waitingItems.PutAsync(waiting, "waiting", "2"u8.ToArray(), cancellationToken: timeout.Token).AsTask();
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
        var saved = await observed.PutAsync(observer, "after", "3"u8.ToArray(), cancellationToken: timeout.Token);
        saved.Content.ToArray().ShouldBe("3"u8.ToArray());
        (await observed.GetAsync(observer, "waiting", timeout.Token)).ShouldBeNull();
        (await observed.GetAsync(observer, "first", timeout.Token)).ShouldNotBeNull();
    }

    /// <summary>
    /// A rollback or a session close while a large delete of the transaction is under way leaves the
    /// document whole through a checkpoint, a reopen and a version-purge pass: the delete applies no
    /// physical bracket once its transaction's end has begun (#1225 review). Before, the delete went
    /// on tombstoning content chunks with the rolled-back sequence; once the checkpoint truncated the
    /// abort record, recovery read those tombstones as committed and the purge reclaimed the content
    /// of a document that was never deleted.
    /// </summary>
    /// <param name="disposeSession">True to end the transaction by closing its session; false to roll it back.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Lifecycle: ending a transaction under a running delete keeps the document through restart and purge")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_TransactionEndsWhileDeleteRuns_ShouldKeepDocumentThroughRestartAndPurge(bool disposeSession)
    {
        // Arrange: a file-backed database holding a multi-chunk document, with quiet background workers.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-Document-lifecycle", Guid.NewGuid().ToString("N"));
        var options = new DocumentDatabaseEngineOptions
        {
            RootPath = FileSystemPath.Parse(directory),
            MaintenanceInterval = TimeSpan.FromHours(1),
            CheckpointInterval = TimeSpan.FromHours(1),
        };
        byte[] content = Encoding.UTF8.GetBytes("{\"id\":\"big\",\"v\":\"" + new string('q', 1024 * 1024) + "\"}");
        try
        {
            bool deleteParked;
            Exception deleteError;
            await using (var engine = DocumentDatabaseEngine.Create(options))
            {
                var database = await engine.CreateDatabaseAsync("db", timeout.Token);
                await using (var seed = await database.CreateSessionAsync(timeout.Token))
                {
                    var seeded = await seed.CreateCollectionAsync("items", timeout.Token);
                    await seeded.PutAsync(seed, "big", content, cancellationToken: timeout.Token);
                }
                var session = await database.CreateSessionAsync(timeout.Token);
                var collection = await session.GetCollectionAsync("items", timeout.Token);
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
                var deleting = collection.DeleteAsync(session, "big", cancellationToken: timeout.Token).AsTask();
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

            await using var reopened = DocumentDatabaseEngine.Create(options);
            var loaded = await reopened.OpenDatabaseAsync("db", timeout.Token);
            loaded.Coordinator.RunVersionPurgePass(timeout.Token);
            await using var observer = await loaded.CreateSessionAsync(timeout.Token);
            var items = await observer.GetCollectionAsync("items", timeout.Token);
            var stored = await items.GetAsync(observer, "big", timeout.Token);

            // Assert
            deleteParked.ShouldBeTrue();
            deleteError.InnerException.ShouldBeOfType<TransactionAbortedException>();
            stored.ShouldNotBeNull().Content.Span.SequenceEqual(content).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
