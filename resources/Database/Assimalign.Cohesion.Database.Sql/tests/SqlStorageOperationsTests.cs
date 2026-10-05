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
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
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
        var catalogAfterTheWorkers = strategy.Capture(name + SqlDatabaseEngine.CatalogSuffix);
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

        // Then the sessions close.
        await other.DisposeAsync();
        await session.DisposeAsync();
        bool offlineAfterTheClose = database.IsOffline;
        var dataBeforeTheReopen = strategy.Capture(name);
        var catalogBeforeTheReopen = strategy.Capture(name + SqlDatabaseEngine.CatalogSuffix);

        var reopened = (SqlDatabaseInstance)await harness.Engine.OpenDatabaseAsync(name);
        await using var observer = await reopened.CreateSessionAsync();
        var ids = await Ids(observer);

        // A handle of the closed instance still gets the coded refusal, not the state its close
        // left the transaction in.
        var staleCommit = await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.CommitAsync());

        // Assert: the coded refusals, everywhere.
        unconfirmed.InnerException.ShouldBeOfType<TransactionCommitUnconfirmedException>();
        StorageOfflineException.Find(unconfirmed).ShouldNotBeNull();
        refusals.ShouldAllBe(refusal => refusal.Code == "COHSQLT004" && refusal.Message.StartsWith("COHSQLT004", StringComparison.Ordinal));
        statementError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        statementError.Message.ShouldStartWith("COHSQLT004", Case.Sensitive);
        handshakeError.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        handshakeError.Message.ShouldStartWith("COHSQLT004", Case.Sensitive);
        harness.Engine.State.ShouldBe(EngineState.Running);

        staleCommit.Code.ShouldBe("COHSQLT004");

        // Assert: nothing reached either file set after the failure, from the workers or the close.
        catalogOfflineAtOnce.ShouldBeTrue();
        dataAfterTheWorkers.Data.ShouldBe(dataAtTheFailure.Data);
        dataAfterTheWorkers.Journal.ShouldBe(dataAtTheFailure.Journal);
        catalogAfterTheWorkers.Data.ShouldBe(catalogAtTheFailure.Data);
        catalogAfterTheWorkers.Journal.ShouldBe(catalogAtTheFailure.Journal);
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

    /// <summary>
    /// The data set's journal fsync fails on a commit while the catalog set holds pages the
    /// write-back worker would write. With no engine call in between, the catalog set is already
    /// offline, and a pass of every worker changes neither of its files. A second database in the
    /// same engine, whose DDL left the same kind of dirty catalog pages, shows the pass would have
    /// written them (#1243 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Offline: a failed data journal fsync stops the catalog file set at once, before any engine call")]
    public async Task Commit_DataJournalFsyncFails_ShouldStopTheCatalogFileSetAtOnce()
    {
        // Arrange: quiet workers, so only the passes the test runs write anything back.
        const string name = "data-fails";
        const string control = "control";
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create(QuietOptions(strategy));
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(name);
        var controlDatabase = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(control);
        await using var session = await database.CreateSessionAsync();
        await using var controlSession = await controlDatabase.CreateSessionAsync();
        foreach (var target in new[] { session, controlSession })
        {
            await target.ExecuteAsync("CREATE TABLE t1 (id INT NOT NULL, val INT)");
            await target.ExecuteAsync("CREATE TABLE t2 (id INT NOT NULL, val INT)");
            await target.ExecuteAsync("CREATE INDEX ix_t1_val ON t1 (val)");
        }

        // Act: the data journal's fsync fails on the commit.
        int unspent;
        using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalFlushes(1, storageName: name))
        {
            await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await session.ExecuteAsync("INSERT INTO t1 (id, val) VALUES (1, 1)"));
            unspent = failures.Remaining;
        }

        bool catalogOfflineAtOnce = database.CatalogStorage.IsOffline;
        var catalogAtTheFailure = strategy.Capture(name + SqlDatabaseEngine.CatalogSuffix);
        var controlCatalogBefore = strategy.Capture(control + SqlDatabaseEngine.CatalogSuffix);
        database.CatalogStorage.CheckpointJournalSize = 1;
        foreach (var worker in engine.Workers.OfType<DatabaseEngineWorker>())
        {
            worker.RunIteration(CancellationToken.None).ShouldBeTrue(worker.Fault?.ToString());
        }

        var catalogAfterTheWorkers = strategy.Capture(name + SqlDatabaseEngine.CatalogSuffix);
        var controlCatalogAfter = strategy.Capture(control + SqlDatabaseEngine.CatalogSuffix);

        // Assert
        unspent.ShouldBe(0);
        catalogOfflineAtOnce.ShouldBeTrue();
        catalogAfterTheWorkers.Data.ShouldBe(catalogAtTheFailure.Data);
        catalogAfterTheWorkers.Journal.ShouldBe(catalogAtTheFailure.Journal);
        controlCatalogAfter.Data.ShouldNotBe(controlCatalogBefore.Data);
        controlDatabase.IsOffline.ShouldBeFalse();
    }

    /// <summary>
    /// The catalog set's journal fsync fails during a DDL statement. The data set goes offline in
    /// the same moment: with no engine call in between a pass of every worker changes neither of
    /// its files, and a transaction that wrote before the failure cannot commit. The DDL itself is
    /// unconfirmed, not refused: its catalog commit record was written (#1243 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Offline: a failed catalog journal fsync stops the data file set at once and refuses a pending commit")]
    public async Task Ddl_CatalogJournalFsyncFails_ShouldStopTheDataFileSetAtOnce()
    {
        // Arrange: dirty data pages, and a transaction with a pending write.
        const string name = "catalog-fails";
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create(QuietOptions(strategy));
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(name);
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 1), (2, 2), (3, 3)");
        var pending = await other.BeginTransactionAsync();
        await other.ExecuteAsync("INSERT INTO t (id, val) VALUES (4, 4)");

        // Act: the catalog journal's fsync fails while CREATE TABLE commits its catalog bracket.
        DatabaseTransactionCommitUnconfirmedException ddl;
        int unspent;
        using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalFlushes(1, storageName: name + SqlDatabaseEngine.CatalogSuffix))
        {
            ddl = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await session.ExecuteAsync("CREATE TABLE u (id INT NOT NULL)"));
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
        ddl.Message.ShouldStartWith("COHSQLT004", Case.Sensitive);
        StorageOfflineException.Find(ddl).ShouldNotBeNull().CommitRecordWritten.ShouldBeTrue();
        dataOfflineAtOnce.ShouldBeTrue();
        dataAfterTheWorkers.Data.ShouldBe(dataAtTheFailure.Data);
        dataAfterTheWorkers.Journal.ShouldBe(dataAtTheFailure.Journal);
        commit.Code.ShouldBe("COHSQLT004");
        engine.OfflineDatabases.ShouldBe([(DatabaseName)name]);
    }

    /// <summary>
    /// A DDL statement commits several durable brackets in the catalog and data file sets. Each
    /// of its journal fsyncs fails in turn, with the record bytes surviving, and the database is
    /// reopened. Whatever part of the statement the reopen keeps, the caller was told the outcome
    /// is unconfirmed, never that the statement was refused: a caller told "refused" would not
    /// look for the effect, and here it can survive (#1243 review, the CREATE TABLE, CREATE INDEX,
    /// DROP TABLE and ALTER TABLE cases of probe P2c).
    /// </summary>
    /// <param name="ddl">The statement.</param>
    /// <param name="count">A count query over the system views that reads the statement's effect.</param>
    /// <param name="countWhenApplied">The count when the statement's effect survived the reopen.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Offline: a DDL statement whose fsync fails is unconfirmed, whichever bracket failed")]
    [InlineData("CREATE TABLE t2 (id INT NOT NULL)", "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 't2'", 1L)]
    [InlineData("CREATE INDEX ix_name ON t (name)", "SELECT COUNT(*) FROM COHESION_SCHEMA.INDEXES WHERE INDEX_NAME = 'ix_name'", 1L)]
    [InlineData("DROP TABLE t", "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 't'", 0L)]
    [InlineData("ALTER TABLE t ADD COLUMN extra INT", "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE COLUMN_NAME = 'extra'", 1L)]
    public async Task Ddl_EveryFsyncFails_ShouldBeUnconfirmed(string ddl, string count, long countWhenApplied)
    {
        var outcomes = new List<bool>();
        for (int skip = 0; skip < 16; skip++)
        {
            // Arrange: a fresh database for each fsync to fail.
            const string name = "ddl";
            var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
            await using var engine = SqlDatabaseEngine.Create(QuietOptions(strategy));
            var database = await engine.CreateDatabaseAsync(name);
            await using (var setup = await database.CreateSessionAsync())
            {
                await setup.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, name VARCHAR(50))");
                await setup.ExecuteAsync("INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b')");
            }

            // Act: fail the statement's fsync after letting `skip` through.
            Exception? error;
            int unspent;
            await using (var session = await database.CreateSessionAsync())
            {
                using var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalFlushes(1, skip);
                error = await Record.ExceptionAsync(async () => await session.ExecuteAsync(ddl));
                unspent = failures.Remaining;
            }

            if (unspent == 1)
            {
                // The statement made fewer fsyncs than `skip`: every one of them has failed once.
                error.ShouldBeNull();
                break;
            }

            var reopened = await engine.OpenDatabaseAsync(name);
            await using var observer = await reopened.CreateSessionAsync();
            outcomes.Add(await Scalar(observer, count) == countWhenApplied);

            // Assert: the caller learned the outcome is unknown, whichever fsync failed: a bracket
            // the statement committed by itself, or its transaction's commit record.
            StorageOfflineException.Find(error.ShouldBeOfType<DatabaseTransactionCommitUnconfirmedException>($"fsync {skip + 1} of '{ddl}'"))
                .ShouldNotBeNull();
        }

        // Assert: the statement made several fsyncs, and in at least one case its effect survived,
        // the case a refusal would misreport.
        outcomes.Count.ShouldBeGreaterThan(0);
        outcomes.ShouldContain(true);
    }

    /// <summary>
    /// One database's statement holds its apply gate while another database of the engine is
    /// written well past its checkpoint size. The checkpoint worker does not wait for the busy
    /// gate: it defers that database's checkpoint to the statement's end and keeps checkpointing
    /// the other, whose journal stays near its size. When the statement ends, its database is
    /// checkpointed at once (#1254 review, probe P7). The bounds are ratios to the size.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Checkpoint trigger: a long statement in one database does not hold up another database's checkpoints")]
    public async Task CheckpointJournalSize_LongStatementInAnotherDatabase_ShouldKeepTheJournalBounded()
    {
        // Arrange: a time backstop far out of the way, so only the size can trigger.
        const long size = 4 * 1024 * 1024;
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            CheckpointJournalSize = size,
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var busy = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("busy");
        var written = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("written");
        await using (var setup = await written.CreateSessionAsync())
        {
            await setup.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, payload VARCHAR(200))");
        }

        await using (var setup = await busy.CreateSessionAsync())
        {
            await setup.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, payload VARCHAR(200))");
            for (int i = 0; i < 20; i++)
            {
                await setup.ExecuteAsync($"INSERT INTO t (id, payload) VALUES ({i}, '{new string('x', 150)}')");
            }
        }

        // One statement holds the busy database's gate, and only then does the database become due
        // by size, so the checkpoint worker cannot checkpoint it before the gate is held (a race
        // that left the journal near-empty on a fast Linux runner).
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = await busy.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var statement = busy.Coordinator.ApplyStatementAsync<bool>(context, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, durable: false).AsTask();
        await entered.Task.WaitAsync(Timeout);
        busy.DataStorage.CheckpointJournalSize = 1;
        long busyJournalWhileHeld = busy.DataStorage.JournalLength;
        busyJournalWhileHeld.ShouldBeGreaterThan(1024, "the busy database's journal must hold its inserts before the deferred checkpoint");

        using var stop = new CancellationTokenSource();
        var writers = Enumerable.Range(0, 2).Select(writer => Task.Run(async () =>
        {
            await using var session = await written.CreateSessionAsync();
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                await session.ExecuteAsync($"INSERT INTO t (id, payload) VALUES ({writer * 10_000_000 + i}, '{new string('x', 150)}')");
            }
        })).ToArray();

        // Act: sample the written database's journal until many sizes went through it.
        long largest = 0;
        long total = 0;
        long previous = 0;
        var watch = Stopwatch.StartNew();
        while (total < 10 * size && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            long length = written.DataStorage.JournalLength;
            largest = Math.Max(largest, length);
            total += length >= previous ? length - previous : length;
            previous = length;
            await Task.Delay(1);
        }

        stop.Cancel();
        await Task.WhenAll(writers).WaitAsync(Timeout);
        bool heldThroughout = !statement.IsCompleted;

        release.SetResult();
        await statement.WaitAsync(Timeout);
        long busyJournalAfterTheStatement = busy.DataStorage.JournalLength;
        await busy.Coordinator.CommitAsync(context);

        // Assert: tens of megabytes went through the written database while the busy one held
        // its gate, and its journal never held more than a few sizes.
        heldThroughout.ShouldBeTrue();
        total.ShouldBeGreaterThanOrEqualTo(10 * size, $"journal bytes written while the gate was held; largest {largest}");
        ((double)largest / size).ShouldBeLessThan(4.0, $"largest journal {largest} bytes for a size of {size}");

        // Assert: the busy database's statement ran the deferred checkpoint as it ended: its
        // journal holds the checkpoint record alone.
        busyJournalAfterTheStatement.ShouldBeLessThan(busyJournalWhileHeld / 10, $"journal of {busyJournalWhileHeld} bytes while the gate was held");
        engine.State.ShouldBe(EngineState.Running);
    }

    /// <summary>
    /// The rollback's undo cannot run for a while, so it is deferred and the version-purge worker's
    /// retries fail too: the engine reports Faulted. Once the fault clears the next retry completes
    /// the undo, the waiting writer proceeds, and the engine reports Running again instead of
    /// staying Faulted for good (#1226 review, probe P8). The fault is a data file that refuses
    /// reads while the buffer pool holds none of its pages; until #1252 it was a device refusing
    /// journal writes, which now takes the database offline instead.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Deferred undo: a fault that clears leaves the engine Running once the retry completes")]
    public async Task RollbackAsync_UndoFailsUntilTheFaultClears_ShouldReturnTheEngineToRunning()
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            StorageStrategy = strategy,
            MaintenanceInterval = TimeSpan.FromSeconds(1),
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
        });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("undo-fault");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");

        // Act: the pool keeps one page, the shared space's first, not the table's; the data file
        // refuses reads until the worker's retry has failed.
        int capacity = database.DataStorage.BufferPoolCapacity;
        database.DataStorage.BufferPoolCapacity = 1;
        using (database.DataStorage.PageManager.GetPage((Assimalign.Cohesion.Database.Storage.PageId)1L))
        {
        }

        strategy.FailEveryDataRead = true;
        await transaction.RollbackAsync();
        bool deferred = database.Coordinator.NextDeferredUndoRetry is not null;
        bool faulted = await Eventually(() => engine.State == EngineState.Faulted);
        strategy.FailEveryDataRead = false;
        database.DataStorage.BufferPoolCapacity = capacity;
        await other.ExecuteAsync("DROP TABLE t").AsTask().WaitAsync(Timeout);
        bool recovered = await Eventually(() => engine.State == EngineState.Running);

        // Assert
        deferred.ShouldBeTrue();
        faulted.ShouldBeTrue();
        recovered.ShouldBeTrue();
        database.Coordinator.NextDeferredUndoRetry.ShouldBeNull();
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
    /// The bound is a ratio to the configured size, never an absolute time. The rows carry a
    /// 6,000-character payload: since storage format 3 (#1253) an insert journals the bytes it
    /// changed rather than two 8 KiB images of each page it touched, so with small rows forty
    /// sizes of journal took several times as many statements as before.
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
            await setup.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, payload VARCHAR(6000))");
        }

        using var stop = new CancellationTokenSource();
        string payload = new('x', 6000);
        long inserted = 0;
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync();
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                await session.ExecuteAsync($"INSERT INTO t (id, payload) VALUES ({writer * 10_000_000 + i}, '{payload}')");
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
    /// engine's wiring is checked exactly (the first retry is due within 100 ms of the deferral),
    /// and the release end to end as a ratio to that first delay.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Deferred undo: a transient undo failure releases the writer within about a second")]
    public async Task RollbackAsync_TransientUndoFailure_ShouldReleaseTheWriterWithinAboutASecond()
    {
        // Arrange
        var maintenance = TimeSpan.FromHours(1);
        var options = new SqlDatabaseEngineOptions
        {
            StorageStrategy = new FaultInjectingJournalSqlStorageStrategy(),
            MaintenanceInterval = maintenance,
        };
        await using var engine = SqlDatabaseEngine.Create(options);
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("undo-retry");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");

        // Act: another storage bracket holds every page while the rollback runs, so the undo's
        // bracket cannot touch the first page it undoes and the undo is deferred with
        // the writer's locks held; the pages are released at once. (Until #1252 a failed journal
        // write was the transient fault; a journal write failure now takes the database offline.)
        var watch = Stopwatch.StartNew();
        using (PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
        }

        // The engine handed the coordinator its first retry delay: the retry is due within it.
        var firstRetry = database.Coordinator.NextDeferredUndoRetry;

        // DROP TABLE needs the table's exclusive lock, which the writer's intent lock holds.
        await other.ExecuteAsync("DROP TABLE t").AsTask().WaitAsync(Timeout);
        watch.Stop();

        // Assert
        options.DeferredUndoRetryDelay.ShouldBe(TimeSpan.FromMilliseconds(100));
        firstRetry.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(options.DeferredUndoRetryDelay);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        (watch.Elapsed / options.DeferredUndoRetryDelay).ShouldBeLessThan(20);
        engine.State.ShouldBe(EngineState.Running);
    }

    // Options whose background workers stay out of the way: only the passes a test runs itself
    // write anything back or checkpoint.
    private static SqlDatabaseEngineOptions QuietOptions(FaultInjectingJournalSqlStorageStrategy strategy) => new()
    {
        StorageStrategy = strategy,
        CheckpointInterval = TimeSpan.FromHours(1),
        PageWriteBackInterval = TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    };

    // Polls a condition the engine's workers make true, for at most the test timeout.
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Timeout)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    private static async Task<long> Scalar(IDatabaseSession session, string query)
    {
        var result = await session.ExecuteAsync(query);
        var set = result.ShouldBeAssignableTo<QueryResultSet>().ShouldNotBeNull();
        await using (set)
        {
            await foreach (var row in set.GetRowsAsync())
            {
                return Convert.ToInt64(row.GetValue(0));
            }
        }

        throw new InvalidOperationException($"'{query}' returned no row.");
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
