using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// Storage operations of the document engine: a failed journal fsync takes the database offline
/// until it is reopened (#1243), the buffer pool and the checkpoint triggers are options (#1254),
/// and a deferred undo is retried on its own backoff (#1226). Documents has no wire server, so
/// every refusal is checked in process.
/// </summary>
public sealed class DocumentStorageOperationsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The commit's journal fsync fails: the caller gets the unconfirmed commit, and from then on
    /// every operation — a new session, a read, a write, a query, BEGIN, COMMIT and ROLLBACK of an
    /// open transaction — is refused with COHDBD002, and nothing reaches the file set, through the
    /// workers' passes and the sessions' close included. Reopening the database runs recovery,
    /// which keeps the commit when its record's bytes survived and drops it when they were lost
    /// with the failed fsync.
    /// </summary>
    /// <param name="recordSurvives">False to reopen with only the journal bytes a durable flush confirmed.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Offline: a failed journal fsync refuses every operation until the reopen, whose recovery decides")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commit_JournalFsyncFails_ShouldRefuseEveryOperationUntilReopened(bool recordSurvives)
    {
        // Arrange: quiet workers, so none of them makes the commit record durable before the
        // committer's own fsync; the test runs their passes itself after the failure.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = !recordSurvives };
        await using var engine = DocumentDatabaseEngine.Create(new()
        {
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        var session = await database.CreateSessionAsync();
        var other = await database.CreateSessionAsync();
        await collection.PutAsync(session, "kept", Doc("kept"));

        // A reader: the database has one writer at a time, so an open writer would block the
        // commit below.
        var open = await other.BeginTransactionAsync();
        (await collection.GetAsync(other, "kept")).ShouldNotBeNull();

        // Act: the commit record is appended and its fsync fails.
        DatabaseTransactionCommitUnconfirmedException unconfirmed;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalFlushes(1))
        {
            unconfirmed = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await collection.PutAsync(session, "unconfirmed", Doc("unconfirmed")));
            failures.Remaining.ShouldBe(0);
        }

        var atTheFailure = strategy.Capture("test");
        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateSessionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await collection.GetAsync(session, "kept")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await collection.PutAsync(session, "late", Doc("late"))),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync("SELECT id FROM items")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateCollectionAsync("late")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.CommitAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.RollbackAsync()),
        };

        // Every worker runs a pass, with a checkpoint due by size; then the sessions close.
        database.DataStorage.CheckpointJournalSize = 1;
        foreach (var worker in engine.Workers.OfType<DatabaseEngineWorker>())
        {
            worker.RunIteration(CancellationToken.None).ShouldBeTrue(worker.Fault?.ToString());
        }

        await other.DisposeAsync();
        await session.DisposeAsync();
        var beforeTheReopen = strategy.Capture("test");

        var reopened = (DocumentDatabaseInstance)await engine.OpenDatabaseAsync("test");
        await using var observer = await reopened.CreateSessionAsync();
        var ids = await Ids(observer);

        // Assert
        unconfirmed.InnerException.ShouldBeOfType<TransactionCommitUnconfirmedException>();
        StorageOfflineException.Find(unconfirmed).ShouldNotBeNull();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHDBD002" && refusal.Message.StartsWith("COHDBD002", StringComparison.Ordinal));
        engine.State.ShouldBe(EngineState.Running);
        beforeTheReopen.Data.ShouldBe(atTheFailure.Data);
        beforeTheReopen.Journal.ShouldBe(atTheFailure.Journal);
        reopened.ShouldNotBeSameAs(database);
        reopened.IsOffline.ShouldBeFalse();
        ids.ShouldBe(recordSurvives ? ["kept", "unconfirmed"] : ["kept"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Buffer pool: a database gets the 32 MiB default, and the options are validated")]
    public async Task BufferPoolCapacity_DefaultAndInvalidOptions_ShouldSizeThePoolAndRefuseBadValues()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("pool");

        // Act & Assert
        database.DataStorage.BufferPoolCapacity.ShouldBe(4096);
        database.DataStorage.CheckpointJournalSize.ShouldBe(256L * 1024 * 1024);
        new DocumentDatabaseEngineOptions().CheckpointInterval.ShouldBe(TimeSpan.FromMinutes(5));
        Should.Throw<ArgumentOutOfRangeException>(() => DocumentDatabaseEngine.Create(new() { BufferPoolCapacity = 512 * 1024 }))
            .ParamName.ShouldBe(nameof(DocumentDatabaseEngineOptions.BufferPoolCapacity));
        Should.Throw<ArgumentOutOfRangeException>(() => DocumentDatabaseEngine.Create(new() { BufferPoolCapacity = 1024 * 1024 + 1 }))
            .ParamName.ShouldBe(nameof(DocumentDatabaseEngineOptions.BufferPoolCapacity));
        Should.Throw<ArgumentOutOfRangeException>(() => DocumentDatabaseEngine.Create(new() { CheckpointJournalSize = -1 }))
            .ParamName.ShouldBe(nameof(DocumentDatabaseEngineOptions.CheckpointJournalSize));
        await using var sized = DocumentDatabaseEngine.Create(new() { BufferPoolCapacity = 2 * 1024 * 1024 });
        ((DocumentDatabaseInstance)await sized.CreateDatabaseAsync("sized")).DataStorage.BufferPoolCapacity.ShouldBe(256);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Builder: the engine builder carries the buffer pool and checkpoint size to the engine it builds")]
    public async Task CreateBuilder_StorageOptions_ShouldReachTheBuiltEngine()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder();
        long defaultPool = builder.BufferPoolCapacity;
        long defaultSize = builder.CheckpointJournalSize;
        builder.BufferPoolCapacity = 2 * 1024 * 1024;
        builder.CheckpointJournalSize = 8 * 1024 * 1024;

        // Act
        await using var engine = (DocumentDatabaseEngine)builder.Build();
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("built");

        // Assert
        defaultPool.ShouldBe(32L * 1024 * 1024);
        defaultSize.ShouldBe(256L * 1024 * 1024);
        database.DataStorage.BufferPoolCapacity.ShouldBe(256);
        database.DataStorage.CheckpointJournalSize.ShouldBe(8L * 1024 * 1024);
    }

    /// <summary>
    /// Under a sustained write load the journal-size trigger keeps the journal near its
    /// configured size (#1254). The bounds are ratios to the configured size and to what was
    /// written, never an absolute time: the test runs until forty sizes of journal were written,
    /// under a hang guard of minutes. The documents carry a 5,000-character payload: since storage
    /// format 3 (#1253) a put journals the bytes it changed rather than two 8 KiB images of each
    /// page it touched, so with small documents forty sizes of journal took many times as many puts.
    /// </summary>
    /// <remarks>
    /// The test counts the checkpoints that truncated the journal and bounds the journal written
    /// per truncation: on average a checkpoint must truncate it before it holds four sizes. Without
    /// the trigger nothing truncates it, and the one length holds all forty. The largest length
    /// alone measured the scheduler as much as the trigger: on a loaded three-core machine one
    /// checkpoint could wait long enough for the four writers to append several sizes, and that
    /// one cycle took the peak past four sizes while the journal written per truncation stayed
    /// under two. The peak is reported, not bounded.
    /// </remarks>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Checkpoint trigger: the journal stays bounded under a sustained write load")]
    public async Task CheckpointJournalSize_SustainedWrites_ShouldKeepTheJournalBounded()
    {
        // Arrange: a time backstop far out of the way, so only the size can trigger.
        const long size = 4 * 1024 * 1024;
        await using var engine = DocumentDatabaseEngine.Create(new()
        {
            CheckpointJournalSize = size,
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("bounded");
        var collection = await database.CreateCollectionAsync("items");
        using var stop = new CancellationTokenSource();
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync();
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                await collection.PutAsync(session, $"{writer}-{i}", Doc($"{writer}-{i}", "\"payload\":\"" + new string('x', 5000) + "\""));
            }
        })).ToArray();

        // Act: sample the journal while the writers push well past the size many times over,
        // counting the checkpoints that truncated it.
        var journal = await JournalSamples.CollectAsync(() => database.DataStorage.JournalLength, 40 * size, Task.WhenAll(writers));
        stop.Cancel();
        await Task.WhenAll(writers).WaitAsync(Timeout);

        // Assert: tens of journal sizes were written, and checkpoints truncated the journal before
        // it held four sizes on average.
        string measured = journal.Describe(size);
        journal.Written.ShouldBeGreaterThanOrEqualTo(40 * size, measured);
        journal.WrittenPerTruncation(size).ShouldBeLessThan(4.0, measured);
        engine.State.ShouldBe(EngineState.Running, measured);
    }

    /// <summary>
    /// A rollback's undo fails once. The undo is retried on its own backoff, about 100 ms later,
    /// so the writer waiting for the rolled-back transaction's writer lock proceeds within about a
    /// second although the maintenance interval is an hour (#1226 owner decision of 2026-10-04).
    /// The engine's wiring is checked exactly (the first retry is due within 100 ms of the
    /// deferral), and the release end to end as a ratio to that first delay.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Deferred undo: a transient undo failure releases the writer within about a second")]
    public async Task RollbackAsync_TransientUndoFailure_ShouldReleaseTheWriterWithinAboutASecond()
    {
        // Arrange
        var maintenance = TimeSpan.FromHours(1);
        var options = new DocumentDatabaseEngineOptions
        {
            StorageStrategy = new FaultInjectingJournalStorageStrategy(),
            MaintenanceInterval = maintenance,
        };
        await using var engine = DocumentDatabaseEngine.Create(options);
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "rolled", Doc("rolled"));

        // Act: another storage bracket holds every page while the rollback runs, so the undo's
        // bracket cannot touch the first page it undoes and the undo is deferred with the database
        // writer lock held; the pages are released at once. (Until #1252 a failed journal write was
        // the transient fault; a journal write failure now takes the database offline.)
        var watch = Stopwatch.StartNew();
        using (PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
        }

        // The engine handed the coordinator its first retry delay: the retry is due within it.
        var firstRetry = database.Coordinator.NextDeferredUndoRetry;

        await collection.PutAsync(other, "other", Doc("other")).AsTask().WaitAsync(Timeout);
        watch.Stop();

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (await collection.GetAsync(other, "rolled")).ShouldBeNull();
        options.DeferredUndoRetryDelay.ShouldBe(TimeSpan.FromMilliseconds(100));
        firstRetry.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(options.DeferredUndoRetryDelay);
        // The regression this guards against waits a full MaintenanceInterval (an hour here) per retry.
        // 100 retry delays (10 s) still catches it by a factor of 360 and leaves room for a loaded CI
        // runner; the exact wiring is the firstRetry check above.
        (watch.Elapsed / options.DeferredUndoRetryDelay).ShouldBeLessThan(100);
        engine.State.ShouldBe(EngineState.Running);
    }

    /// <summary>
    /// Auto-commit puts of small documents share data pages. Every transaction used to own the page
    /// of its content chunk, so each put of a 180-byte document took a fresh 8 KiB page, 1.009 pages
    /// a put, and the worker pace test's in-memory data file passed its 2 GiB capacity within six
    /// seconds on a fast runner (ArgumentOutOfRangeException from the data file's SetLength). With
    /// one content owner, a put's chunk and catalog record take about 300 bytes.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Space: auto-commit puts of small documents share data pages")]
    public async Task PutAsync_SmallDocumentsAutoCommitted_ShouldShareDataPages()
    {
        // Arrange
        const int puts = 1000;
        await using var engine = DocumentDatabaseEngine.Create(new()
        {
            StorageStrategy = new FaultInjectingJournalStorageStrategy(),
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("space");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        long pagesBefore = database.DataStorage.PageManager.PageCount;

        // Act: each put is its own transaction.
        for (int id = 0; id < puts; id++)
        {
            await collection.PutAsync(session, $"k{id}", Doc($"k{id}", "\"payload\":\"" + new string('x', 150) + "\""));
        }

        // Assert: about 300 bytes a put is under 40 pages; a page per put was 1,009.
        long pages = database.DataStorage.PageManager.PageCount - pagesBefore;
        pages.ShouldBeLessThanOrEqualTo(puts / 10, $"{pages} data pages for {puts} puts of 180-byte documents");
        (await Ids(session)).Count.ShouldBe(puts);
    }

    private static ReadOnlyMemory<byte> Doc(string id, string? members = null)
        => Encoding.UTF8.GetBytes(members is null ? $"{{\"id\":\"{id}\"}}" : $"{{\"id\":\"{id}\",{members}}}");

    private static async Task<List<string>> Ids(IDatabaseSession session)
    {
        var ids = new List<string>();
        var result = await session.ExecuteAsync("SELECT id FROM items");
        if (result is not QueryResultSet set) { return ids; }
        await using (set)
        {
            await foreach (var row in set.GetRowsAsync()) { ids.Add(row.GetString(0) ?? "<null>"); }
        }
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }
}
