using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// Storage operations of the key-value engine: a failed journal fsync takes the database offline
/// until it is reopened (#1243), the buffer pool and the checkpoint triggers are options (#1254),
/// and a deferred undo is retried on its own backoff (#1226).
/// </summary>
public sealed class KeyValueStorageOperationsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The commit's journal fsync fails: the caller gets the unconfirmed commit, and from then on
    /// every operation — a new session, a command, BEGIN, COMMIT and ROLLBACK of an open
    /// transaction, over the wire too — is refused with COHDBK002, and nothing reaches either file
    /// set, through the workers' passes and the sessions' close included. Reopening the database
    /// runs recovery, which keeps the commit when its record's bytes survived and drops it when
    /// they were lost with the failed fsync; the open transaction is aborted either way.
    /// </summary>
    /// <param name="recordSurvives">False to reopen with only the journal bytes a durable flush confirmed.</param>
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Offline: a failed journal fsync refuses every operation until the reopen, whose recovery decides")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commit_JournalFsyncFails_ShouldRefuseEveryOperationUntilReopened(bool recordSurvives)
    {
        // Arrange: quiet workers, so none of them makes the commit record durable before the
        // committer's own fsync; the test runs their passes itself after the failure.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = !recordSurvives };
        await using var harness = await KeyValueServerHarness.StartAsync(configureEngine: options =>
        {
            options.StorageStrategy = strategy;
            options.CheckpointInterval = TimeSpan.FromHours(1);
            options.PageWriteBackInterval = TimeSpan.FromHours(1);
            options.MaintenanceInterval = TimeSpan.FromHours(1);
        });
        const string name = KeyValueServerHarness.DatabaseName;
        var database = (KeyValueDatabaseInstance)await harness.Engine.OpenDatabaseAsync(name);
        await using var wire = await harness.DialAsync();
        await wire.HandshakeAsync();
        var session = await database.CreateSessionAsync();
        var other = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("kept"), Bytes("1"));
        var open = await other.BeginTransactionAsync();
        await database.PutAsync(other, Bytes("open"), Bytes("2"));

        // Act: the commit record is appended and its fsync fails.
        DatabaseTransactionCommitUnconfirmedException unconfirmed;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalFlushes(1))
        {
            unconfirmed = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await database.PutAsync(session, Bytes("unconfirmed"), Bytes("3")));
            failures.Remaining.ShouldBe(0);
        }

        var dataAtTheFailure = strategy.Capture(name);
        var catalogAtTheFailure = strategy.Capture(name + KeyValueDatabaseEngine.CatalogSuffix);

        // The catalog file set went offline with the data set, before anything read either
        // state: the storage's own flag, not the instance's. Every worker then runs a pass, with a
        // checkpoint due by size, before any session operation.
        bool catalogOfflineAtOnce = database.CatalogStorage.IsOffline;
        database.DataStorage.CheckpointJournalSize = 1;
        database.CatalogStorage.CheckpointJournalSize = 1;
        foreach (var worker in harness.Engine.Workers.OfType<DatabaseEngineWorker>())
        {
            worker.RunIteration(CancellationToken.None).ShouldBeTrue(worker.Fault?.ToString());
        }

        var dataAfterTheWorkers = strategy.Capture(name);
        var catalogAfterTheWorkers = strategy.Capture(name + KeyValueDatabaseEngine.CatalogSuffix);
        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateSessionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.GetAsync(session, Bytes("kept"))),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync("SCAN")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.PutAsync(other, Bytes("late"), Bytes("4"))),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.CommitAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.RollbackAsync()),
        };

        // Over the wire: a command on a session opened before the failure, and a new session.
        await wire.SendAsync(ProtocolMessageType.Execute, new ProtocolExecuteMessage("SCAN", new Dictionary<string, byte[]>()).Encode());
        var commandError = ProtocolErrorMessage.Decode((await wire.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        await using var late = await harness.DialAsync();
        await late.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, name, "late").Encode());
        await late.ExpectAsync(ProtocolMessageType.Authenticate);
        await late.SendAsync(ProtocolMessageType.AuthenticateResponse);
        var handshakeError = ProtocolErrorMessage.Decode((await late.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Then the sessions close.
        await other.DisposeAsync();
        await session.DisposeAsync();
        var dataBeforeTheReopen = strategy.Capture(name);
        var catalogBeforeTheReopen = strategy.Capture(name + KeyValueDatabaseEngine.CatalogSuffix);

        var reopened = (KeyValueDatabaseInstance)await harness.Engine.OpenDatabaseAsync(name);
        await using var observer = await reopened.CreateSessionAsync();
        var keys = new List<string>();
        await foreach (var entry in reopened.ScanAsync(observer))
        {
            keys.Add(Text(entry.Key));
        }

        // Assert: the coded refusals, everywhere.
        unconfirmed.InnerException.ShouldBeOfType<TransactionCommitUnconfirmedException>();
        StorageOfflineException.Find(unconfirmed).ShouldNotBeNull();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHDBK002" && refusal.Message.StartsWith("COHDBK002", StringComparison.Ordinal));
        commandError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        commandError.Message.ShouldStartWith("COHDBK002", Case.Sensitive);
        handshakeError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        handshakeError.Message.ShouldStartWith("COHDBK002", Case.Sensitive);
        harness.Engine.State.ShouldBe(EngineState.Running);

        // Assert: nothing reached either file set after the failure, from the workers or the close.
        catalogOfflineAtOnce.ShouldBeTrue();
        dataAfterTheWorkers.Data.ShouldBe(dataAtTheFailure.Data);
        dataAfterTheWorkers.Journal.ShouldBe(dataAtTheFailure.Journal);
        catalogAfterTheWorkers.Data.ShouldBe(catalogAtTheFailure.Data);
        catalogAfterTheWorkers.Journal.ShouldBe(catalogAtTheFailure.Journal);
        dataBeforeTheReopen.Data.ShouldBe(dataAtTheFailure.Data);
        dataBeforeTheReopen.Journal.ShouldBe(dataAtTheFailure.Journal);
        catalogBeforeTheReopen.Data.ShouldBe(catalogAtTheFailure.Data);
        catalogBeforeTheReopen.Journal.ShouldBe(catalogAtTheFailure.Journal);

        // Assert: the reopen is a new, online instance, and recovery decided.
        reopened.ShouldNotBeSameAs(database);
        reopened.IsOffline.ShouldBeFalse();
        keys.Order(StringComparer.Ordinal).ShouldBe(recordSurvives ? ["kept", "unconfirmed"] : ["kept"]);
    }

    /// <summary>
    /// The data set's journal fsync fails on a commit while the catalog set still holds the pages
    /// the database's creation wrote. With no engine call in between, the catalog set is already
    /// offline, and a pass of every worker changes neither of its files. A second database of the
    /// same engine shows the pass would have written such pages (#1243 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Offline: a failed data journal fsync stops the catalog file set at once, before any engine call")]
    public async Task Commit_DataJournalFsyncFails_ShouldStopTheCatalogFileSetAtOnce()
    {
        // Arrange: quiet workers, so only the passes the test runs write anything back.
        const string name = "data-fails";
        const string control = "control";
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = KeyValueDatabaseEngine.Create(QuietOptions(strategy));
        var database = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(name);
        var controlDatabase = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(control);
        await using var session = await database.CreateSessionAsync();

        // Act: the data journal's fsync fails on the commit.
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalFlushes(1, storageName: name))
        {
            await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await database.PutAsync(session, Bytes("key"), Bytes("value")));
            unspent = failures.Remaining;
        }

        bool catalogOfflineAtOnce = database.CatalogStorage.IsOffline;
        var catalogAtTheFailure = strategy.Capture(name + KeyValueDatabaseEngine.CatalogSuffix);
        var controlCatalogBefore = strategy.Capture(control + KeyValueDatabaseEngine.CatalogSuffix);
        database.CatalogStorage.CheckpointJournalSize = 1;
        foreach (var worker in engine.Workers.OfType<DatabaseEngineWorker>())
        {
            worker.RunIteration(CancellationToken.None).ShouldBeTrue(worker.Fault?.ToString());
        }

        var catalogAfterTheWorkers = strategy.Capture(name + KeyValueDatabaseEngine.CatalogSuffix);
        var controlCatalogAfter = strategy.Capture(control + KeyValueDatabaseEngine.CatalogSuffix);

        // Assert
        unspent.ShouldBe(0);
        catalogOfflineAtOnce.ShouldBeTrue();
        catalogAfterTheWorkers.Data.ShouldBe(catalogAtTheFailure.Data);
        catalogAfterTheWorkers.Journal.ShouldBe(catalogAtTheFailure.Journal);
        controlCatalogAfter.Data.ShouldNotBe(controlCatalogBefore.Data);
        controlDatabase.IsOffline.ShouldBeFalse();
    }

    /// <summary>
    /// The catalog set's journal fsync fails during its checkpoint. The data set goes offline in
    /// the same moment: with no engine call in between a pass of every worker changes neither of
    /// its files, and a transaction that wrote before the failure cannot commit (#1243 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Offline: a failed catalog journal fsync stops the data file set at once and refuses a pending commit")]
    public async Task Checkpoint_CatalogJournalFsyncFails_ShouldStopTheDataFileSetAtOnce()
    {
        // Arrange: dirty data pages, and a transaction with a pending write.
        const string name = "catalog-fails";
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = KeyValueDatabaseEngine.Create(QuietOptions(strategy));
        var database = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync(name);
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("a"), Bytes("1"));
        await database.PutAsync(session, Bytes("b"), Bytes("2"));
        var pending = await other.BeginTransactionAsync();
        await database.PutAsync(other, Bytes("c"), Bytes("3"));

        // Act: the catalog journal's fsync fails as its checkpoint flushes the checkpoint record.
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalFlushes(1, storageName: name + KeyValueDatabaseEngine.CatalogSuffix))
        {
            Should.Throw<StorageOfflineException>(() => database.CatalogStorage.Checkpoint());
            unspent = failures.Remaining;
        }

        bool dataOfflineAtOnce = database.DataStorage.IsOffline;
        var dataAtTheFailure = strategy.Capture(name);
        database.DataStorage.CheckpointJournalSize = 1;
        foreach (var worker in engine.Workers.OfType<DatabaseEngineWorker>())
        {
            worker.RunIteration(CancellationToken.None).ShouldBeTrue(worker.Fault?.ToString());
        }

        var dataAfterTheWorkers = strategy.Capture(name);
        var commit = await Should.ThrowAsync<DatabaseOfflineException>(async () => await pending.CommitAsync());

        // Assert
        unspent.ShouldBe(0);
        dataOfflineAtOnce.ShouldBeTrue();
        dataAfterTheWorkers.Data.ShouldBe(dataAtTheFailure.Data);
        dataAfterTheWorkers.Journal.ShouldBe(dataAtTheFailure.Journal);
        commit.Code.ShouldBe("COHDBK002");
        engine.OfflineDatabases.ShouldBe([(DatabaseName)name]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Buffer pool: the data file set gets the 32 MiB default and the catalog a 1 MiB pool")]
    public async Task BufferPoolCapacity_Default_ShouldSizeTheDataFileSetAndKeepTheCatalogSmall()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        var instance = (KeyValueDatabaseInstance)database;

        // Act & Assert
        new KeyValueDatabaseEngineOptions().BufferPoolCapacity.ShouldBe(32L * 1024 * 1024);
        instance.DataStorage.BufferPoolCapacity.ShouldBe(4096);
        instance.CatalogStorage.BufferPoolCapacity.ShouldBe(KeyValueDatabaseEngine.CatalogBufferPoolPages);
        instance.DataStorage.CheckpointJournalSize.ShouldBe(256L * 1024 * 1024);
        new KeyValueDatabaseEngineOptions().CheckpointInterval.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Buffer pool: a configured capacity reaches the data file set of created and reopened databases")]
    public async Task BufferPoolCapacity_Configured_ShouldReachCreatedAndReopenedDatabases()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var options = new KeyValueDatabaseEngineOptions { StorageStrategy = strategy, BufferPoolCapacity = 2 * 1024 * 1024 };
        var engine = KeyValueDatabaseEngine.Create(options);
        var created = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync("sized");
        int createdPages = created.DataStorage.BufferPoolCapacity;
        await engine.DisposeAsync();

        // Act
        await using var reopenedEngine = KeyValueDatabaseEngine.Create(options);
        var reopened = (KeyValueDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("sized");

        // Assert
        createdPages.ShouldBe(256);
        reopened.DataStorage.BufferPoolCapacity.ShouldBe(256);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Builder: the engine builder carries the buffer pool and checkpoint size to the engine it builds")]
    public async Task CreateBuilder_StorageOptions_ShouldReachTheBuiltEngine()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        long defaultPool = builder.BufferPoolCapacity;
        long defaultSize = builder.CheckpointJournalSize;
        builder.BufferPoolCapacity = 2 * 1024 * 1024;
        builder.CheckpointJournalSize = 8 * 1024 * 1024;

        // Act
        await using var engine = (KeyValueDatabaseEngine)builder.Build();
        var database = (KeyValueDatabaseInstance)await engine.CreateDatabaseAsync("built");

        // Assert
        defaultPool.ShouldBe(32L * 1024 * 1024);
        defaultSize.ShouldBe(256L * 1024 * 1024);
        database.DataStorage.BufferPoolCapacity.ShouldBe(256);
        database.DataStorage.CheckpointJournalSize.ShouldBe(8L * 1024 * 1024);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Options: an invalid buffer pool or checkpoint size is refused at creation")]
    [InlineData(0L, 0L)]
    [InlineData(512L * 1024, 0L)]
    [InlineData(1024L * 1024 + 1, 0L)]
    [InlineData(32L * 1024 * 1024, -1L)]
    public void Create_InvalidStorageOptions_ShouldBeRefused(long bufferPoolCapacity, long checkpointJournalSize)
    {
        // Arrange
        var options = new KeyValueDatabaseEngineOptions
        {
            BufferPoolCapacity = bufferPoolCapacity,
            CheckpointJournalSize = checkpointJournalSize,
        };

        // Act
        var error = Should.Throw<ArgumentOutOfRangeException>(() => KeyValueDatabaseEngine.Create(options));

        // Assert
        error.ParamName.ShouldBe(checkpointJournalSize < 0 ? nameof(KeyValueDatabaseEngineOptions.CheckpointJournalSize) : nameof(KeyValueDatabaseEngineOptions.BufferPoolCapacity));
    }

    /// <summary>
    /// Under a sustained write load the journal-size trigger keeps the data file set's journal
    /// near its configured size (#1254). The bound is a ratio to the configured size.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Checkpoint trigger: the journal stays bounded under a sustained write load")]
    public async Task CheckpointJournalSize_SustainedWrites_ShouldKeepTheJournalBounded()
    {
        // Arrange: a time backstop far out of the way, so only the size can trigger.
        const long size = 4 * 1024 * 1024;
        var (engine, database) = await CreateAsync(options =>
        {
            options.CheckpointJournalSize = size;
            options.CheckpointInterval = TimeSpan.FromHours(1);
        });
        await using var _ = engine;
        var instance = (KeyValueDatabaseInstance)database;
        using var stop = new CancellationTokenSource();
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync();
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                await database.PutAsync(session, Bytes($"{writer}-{i}"), Bytes(new string('x', 150)));
            }
        })).ToArray();

        // Act: sample the journal while the writers push well past the size many times over.
        long largest = 0;
        long written = 0;
        long previous = 0;
        var watch = Stopwatch.StartNew();
        while (written < 40 * size && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            long length = instance.DataStorage.JournalLength;
            largest = Math.Max(largest, length);
            written += length >= previous ? length - previous : length;
            previous = length;
            await Task.Delay(1);
        }

        stop.Cancel();
        await Task.WhenAll(writers).WaitAsync(Timeout);

        // Assert: tens of journal sizes were written, and the journal never held more than a few.
        // The measured values are in the messages, so a failure on a loaded machine says which.
        string measured = $"written {written} bytes in {watch.Elapsed}, largest journal {largest} bytes for a size of {size}, " +
            $"engine {engine.State}";
        written.ShouldBeGreaterThanOrEqualTo(40 * size, measured);
        ((double)largest / size).ShouldBeLessThan(4.0, measured);
        engine.State.ShouldBe(EngineState.Running, measured);
    }

    /// <summary>
    /// A rollback's undo fails once. The undo is retried on its own backoff, about 100 ms later,
    /// so the writer waiting for the rolled-back transaction's key lock proceeds within about a
    /// second although the maintenance interval is an hour (#1226 owner decision of 2026-10-04).
    /// The engine's wiring is checked exactly (the first retry is due within 100 ms of the
    /// deferral), and the release end to end as a ratio to that first delay.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Deferred undo: a transient undo failure releases the writer within about a second")]
    public async Task RollbackAsync_TransientUndoFailure_ShouldReleaseTheWriterWithinAboutASecond()
    {
        // Arrange
        var maintenance = TimeSpan.FromHours(1);
        KeyValueDatabaseEngineOptions? configured = null;
        var (engine, database) = await CreateAsync(options =>
        {
            options.StorageStrategy = new FaultInjectingJournalStorageStrategy();
            options.MaintenanceInterval = maintenance;
            configured = options;
        });
        await using var _ = engine;
        var instance = (KeyValueDatabaseInstance)database;
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();
        await database.PutAsync(session, Bytes("hot"), Bytes("rolled back"));

        // Act: the rollback's first journal write is its undo bracket's begin record, which fails
        // once, so the undo is deferred with the writer's key lock held.
        int unspent;
        var watch = Stopwatch.StartNew();
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }

        // The engine handed the coordinator its first retry delay: the retry is due within it.
        var firstRetry = instance.Coordinator.NextDeferredUndoRetry;

        var put = await database.PutAsync(other, Bytes("hot"), Bytes("next")).AsTask().WaitAsync(Timeout);
        watch.Stop();

        // Assert
        var retryDelay = configured.ShouldNotBeNull().DeferredUndoRetryDelay;
        unspent.ShouldBe(0);
        retryDelay.ShouldBe(TimeSpan.FromMilliseconds(100));
        firstRetry.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(retryDelay);
        put.Applied.ShouldBeTrue();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        instance.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (watch.Elapsed / retryDelay).ShouldBeLessThan(20);
        engine.State.ShouldBe(EngineState.Running);
    }

    // Options whose background workers stay out of the way: only the passes a test runs itself
    // write anything back or checkpoint.
    private static KeyValueDatabaseEngineOptions QuietOptions(FaultInjectingJournalStorageStrategy strategy) => new()
    {
        StorageStrategy = strategy,
        CheckpointInterval = TimeSpan.FromHours(1),
        PageWriteBackInterval = TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    };
}
