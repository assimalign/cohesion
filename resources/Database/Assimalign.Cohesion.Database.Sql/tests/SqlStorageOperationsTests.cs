using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Storage operations of the SQL engine: a failed journal fsync takes the database offline until
/// it is reopened (#1243), the buffer pool and the checkpoint triggers are options (#1254), and a
/// deferred undo is retried on its own backoff (#1226).
/// </summary>
public sealed class SqlStorageOperationsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The commit's journal fsync fails: the caller gets the unconfirmed commit, and from then on
    /// every operation — a new session, a statement, BEGIN, COMMIT and ROLLBACK of an open
    /// transaction, over the wire too — is refused with COHSQLT004, and nothing reaches either file
    /// set, while the background workers run and when the sessions close. Reopening the database
    /// runs recovery, which keeps the commit when its record's bytes survived and drops it when
    /// they were lost with the failed fsync; the open transaction is aborted either way.
    /// </summary>
    /// <param name="recordSurvives">False to reopen with only the journal bytes a durable flush confirmed.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Offline: a failed journal fsync refuses every operation until the reopen, whose recovery decides")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commit_JournalFsyncFails_ShouldRefuseEveryOperationUntilReopened(bool recordSurvives)
    {
        // Arrange: quiet workers, so none of them makes the commit record durable before the
        // committer's own fsync; the test runs their passes itself after the failure.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = !recordSurvives };
        await using var harness = await ServerTestHarness.StartAsync(configureEngine: options =>
        {
            options.StorageStrategy = strategy;
            options.CheckpointInterval = TimeSpan.FromHours(1);
            options.PageWriteBackInterval = TimeSpan.FromHours(1);
            options.MaintenanceInterval = TimeSpan.FromHours(1);
        });
        const string name = ServerTestHarness.DatabaseName;
        var database = (SqlDatabaseInstance)await harness.Engine.OpenDatabaseAsync(name);
        await using var wire = await harness.DialAsync();
        await wire.HandshakeAsync();
        var session = await database.CreateSessionAsync();
        var other = await database.CreateSessionAsync();
        var open = await other.BeginTransactionAsync();
        await other.ExecuteAsync("INSERT INTO users (id, name) VALUES (30, 'open')");

        // Act: the commit record is appended and its fsync fails.
        DatabaseTransactionCommitUnconfirmedException unconfirmed;
        using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalFlushes(1))
        {
            unconfirmed = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await session.ExecuteAsync("INSERT INTO users (id, name) VALUES (20, 'unconfirmed')"));
            failures.Remaining.ShouldBe(0);
        }

        var dataAtTheFailure = strategy.Capture(name);
        var catalogAtTheFailure = strategy.Capture(name + SqlDatabaseEngine.CatalogSuffix);
        var refusals = new List<DatabaseOfflineException>
        {
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.CreateSessionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync("SELECT id FROM users")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.BeginTransactionAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await other.ExecuteAsync("SELECT id FROM users")),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.CommitAsync()),
            await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.RollbackAsync()),
        };

        // Over the wire: a statement on a session opened before the failure, and a new session.
        await wire.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT id FROM users").Encode());
        var statementError = ProtocolErrorMessage.Decode((await wire.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        await using var late = await harness.DialAsync();
        await late.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, name, "late").Encode());
        await late.ExpectAsync(ProtocolMessageType.Authenticate);
        await late.SendAsync(ProtocolMessageType.AuthenticateResponse);
        var handshakeError = ProtocolErrorMessage.Decode((await late.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Every worker runs a pass, with a checkpoint due by size; then the sessions close.
        database.DataStorage.CheckpointJournalSize = 1;
        database.CatalogStorage.CheckpointJournalSize = 1;
        foreach (var worker in harness.Engine.Workers.OfType<DatabaseEngineWorker>())
        {
            worker.RunIteration(CancellationToken.None);
        }

        await other.DisposeAsync();
        await session.DisposeAsync();
        bool offlineAfterTheClose = database.IsOffline;
        var dataBeforeTheReopen = strategy.Capture(name);
        var catalogBeforeTheReopen = strategy.Capture(name + SqlDatabaseEngine.CatalogSuffix);

        var reopened = (SqlDatabaseInstance)await harness.Engine.OpenDatabaseAsync(name);
        await using var observer = await reopened.CreateSessionAsync();
        var ids = await Ids(observer);

        // Assert: the coded refusals, everywhere.
        unconfirmed.InnerException.ShouldBeOfType<TransactionCommitUnconfirmedException>();
        StorageOfflineException.Find(unconfirmed).ShouldNotBeNull();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHSQLT004" && refusal.Message.StartsWith("COHSQLT004", StringComparison.Ordinal));
        statementError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        statementError.Message.ShouldStartWith("COHSQLT004", Case.Sensitive);
        handshakeError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        handshakeError.Message.ShouldStartWith("COHSQLT004", Case.Sensitive);
        harness.Engine.State.ShouldBe(EngineState.Running);

        // Assert: nothing reached either file set after the failure.
        offlineAfterTheClose.ShouldBeTrue();
        dataBeforeTheReopen.Data.ShouldBe(dataAtTheFailure.Data);
        dataBeforeTheReopen.Journal.ShouldBe(dataAtTheFailure.Journal);
        catalogBeforeTheReopen.Data.ShouldBe(catalogAtTheFailure.Data);
        catalogBeforeTheReopen.Journal.ShouldBe(catalogAtTheFailure.Journal);

        // Assert: the reopen is a new, online instance, and recovery decided.
        reopened.ShouldNotBeSameAs(database);
        reopened.IsOffline.ShouldBeFalse();
        ids.ShouldBe(recordSurvives ? [1L, 2L, 20L] : [1L, 2L]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Buffer pool: the data file set gets the 32 MiB default and the catalog a 1 MiB pool")]
    public async Task BufferPoolCapacity_Default_ShouldSizeTheDataFileSetAndKeepTheCatalogSmall()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions());
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("pool");

        // Act & Assert
        new SqlDatabaseEngineOptions().BufferPoolCapacity.ShouldBe(32L * 1024 * 1024);
        database.DataStorage.BufferPoolCapacity.ShouldBe(4096);
        database.CatalogStorage.BufferPoolCapacity.ShouldBe(SqlDatabaseEngine.CatalogBufferPoolPages);
        database.DataStorage.CheckpointJournalSize.ShouldBe(256L * 1024 * 1024);
        new SqlDatabaseEngineOptions().CheckpointInterval.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Buffer pool: a configured capacity reaches the data file set of created and reopened databases")]
    public async Task BufferPoolCapacity_Configured_ShouldReachCreatedAndReopenedDatabases()
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy();
        var options = new SqlDatabaseEngineOptions { StorageStrategy = strategy, BufferPoolCapacity = 2 * 1024 * 1024 };
        var engine = SqlDatabaseEngine.Create(options);
        var created = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("sized");
        int createdPages = created.DataStorage.BufferPoolCapacity;
        await engine.DisposeAsync();

        // Act
        await using var reopenedEngine = SqlDatabaseEngine.Create(options);
        var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("sized");

        // Assert
        createdPages.ShouldBe(256);
        reopened.DataStorage.BufferPoolCapacity.ShouldBe(256);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Builder: the engine builder carries the buffer pool and checkpoint size to the engine it builds")]
    public async Task CreateBuilder_StorageOptions_ShouldReachTheBuiltEngine()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder();
        long defaultPool = builder.BufferPoolCapacity;
        long defaultSize = builder.CheckpointJournalSize;
        builder.BufferPoolCapacity = 2 * 1024 * 1024;
        builder.CheckpointJournalSize = 8 * 1024 * 1024;

        // Act
        await using var engine = (SqlDatabaseEngine)builder.Build();
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("built");

        // Assert
        defaultPool.ShouldBe(32L * 1024 * 1024);
        defaultSize.ShouldBe(256L * 1024 * 1024);
        database.DataStorage.BufferPoolCapacity.ShouldBe(256);
        database.DataStorage.CheckpointJournalSize.ShouldBe(8L * 1024 * 1024);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Options: an invalid buffer pool or checkpoint size is refused at creation")]
    [InlineData(0L, 0L)]
    [InlineData(512L * 1024, 0L)]
    [InlineData(1024L * 1024 + 1, 0L)]
    [InlineData(32L * 1024 * 1024, -1L)]
    public void Create_InvalidStorageOptions_ShouldBeRefused(long bufferPoolCapacity, long checkpointJournalSize)
    {
        // Arrange
        var options = new SqlDatabaseEngineOptions
        {
            BufferPoolCapacity = bufferPoolCapacity,
            CheckpointJournalSize = checkpointJournalSize,
        };

        // Act
        var error = Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create(options));

        // Assert
        error.ParamName.ShouldBe(checkpointJournalSize < 0 ? nameof(SqlDatabaseEngineOptions.CheckpointJournalSize) : nameof(SqlDatabaseEngineOptions.BufferPoolCapacity));
    }

    /// <summary>
    /// Under a sustained write load the journal-size trigger keeps the data file set's journal
    /// near its configured size: the checkpoint worker wakes on the size and the coordinator's
    /// checkpoint waits for the statement apply gate, so the load cannot keep it out (#1254).
    /// The bound is a ratio to the configured size, never an absolute time.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Checkpoint trigger: the journal stays bounded under a sustained write load")]
    public async Task CheckpointJournalSize_SustainedWrites_ShouldKeepTheJournalBounded()
    {
        // Arrange: a time backstop far out of the way, so only the size can trigger.
        const long size = 4 * 1024 * 1024;
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            CheckpointJournalSize = size,
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("bounded");
        await using (var setup = await database.CreateSessionAsync())
        {
            await setup.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, payload VARCHAR(200))");
        }

        using var stop = new CancellationTokenSource();
        long inserted = 0;
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync();
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                await session.ExecuteAsync($"INSERT INTO t (id, payload) VALUES ({writer * 10_000_000 + i}, '{new string('x', 150)}')");
                Interlocked.Increment(ref inserted);
            }
        })).ToArray();

        // Act: sample the journal while the writers push well past the size many times over.
        long largest = 0;
        long written = 0;
        long previous = 0;
        var watch = Stopwatch.StartNew();
        while (written < 40 * size && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            long length = database.DataStorage.JournalLength;
            largest = Math.Max(largest, length);
            written += length >= previous ? length - previous : length;
            previous = length;
            await Task.Delay(1);
        }

        stop.Cancel();
        await Task.WhenAll(writers).WaitAsync(Timeout);

        // Assert: tens of journal sizes were written, and the journal never held more than a few.
        written.ShouldBeGreaterThanOrEqualTo(40 * size);
        ((double)largest / size).ShouldBeLessThan(4.0);
        Interlocked.Read(ref inserted).ShouldBeGreaterThan(0);
        engine.State.ShouldBe(EngineState.Running);
    }

    /// <summary>
    /// A rollback's undo fails once. The undo is retried on its own backoff, about 100 ms later,
    /// so the writer waiting for the rolled-back transaction's lock proceeds within about a second,
    /// although the maintenance interval is an hour (#1226 owner decision of 2026-10-04). The
    /// bound is a ratio to the maintenance interval.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Deferred undo: a transient undo failure releases the writer within about a second")]
    public async Task RollbackAsync_TransientUndoFailure_ShouldReleaseTheWriterWithinAboutASecond()
    {
        // Arrange
        var maintenance = TimeSpan.FromHours(1);
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            StorageStrategy = new FaultInjectingJournalSqlStorageStrategy(),
            MaintenanceInterval = maintenance,
        });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("undo-retry");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");

        // Act: the rollback's first journal write is its undo bracket's begin record, which fails
        // once, so the undo is deferred with the writer's locks held.
        int unspent;
        var watch = Stopwatch.StartNew();
        using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }

        // DROP TABLE needs the table's exclusive lock, which the writer's intent lock holds.
        await other.ExecuteAsync("DROP TABLE t").AsTask().WaitAsync(Timeout);
        watch.Stop();

        // Assert
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (watch.Elapsed / maintenance).ShouldBeLessThan(0.01);
        engine.State.ShouldBe(EngineState.Running);
    }

    private static async Task<List<long>> Ids(IDatabaseSession session)
    {
        var ids = new List<long>();
        var result = await session.ExecuteAsync("SELECT id FROM users ORDER BY id");
        if (result is QueryResultSet set)
        {
            await using (set)
            {
                await foreach (var row in set.GetRowsAsync())
                {
                    ids.Add(Convert.ToInt64(row.GetValue(0)));
                }
            }
        }

        return ids;
    }
}
