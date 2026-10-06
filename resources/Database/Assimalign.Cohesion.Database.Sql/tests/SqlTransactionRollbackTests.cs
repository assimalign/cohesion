using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The SQL transaction's rollback contract (#1226): the caller's token is observed only before the
/// rollback starts, and a started rollback always ends the transaction and releases its locks, even
/// when the journal rejects its abort record.
/// </summary>
public sealed class SqlTransactionRollbackTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Rollback: a token canceled before the rollback starts leaves the transaction and its writes intact")]
    public async Task RollbackAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionAndItsWritesIntact()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "rollback-canceled" });
        var database = await engine.CreateDatabaseAsync("canceled-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));

        // Assert: nothing of the rollback ran, so no purge pass may undo the still-active writer,
        // and its commit keeps the row.
        transaction.State.ShouldBe(TransactionState.Active);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await transaction.CommitAsync();
        (await Rows(session, "SELECT id FROM t")).Select(row => Convert.ToInt64(row[0])).OrderBy(id => id).ShouldBe([1L, 2L]);
    }

    /// <summary>
    /// A started rollback ends the transaction and releases its locks even when the journal
    /// rejects its abort record (#1226). Since #1252 the rollback appends the record to the
    /// journal's append buffer, and it reaches the file with the next drain: here the commit of
    /// another session's insert. That drain fails, which takes the database offline (#1243's
    /// rule): the insert is reported unconfirmed, the next statement is refused as offline, and the
    /// reopen keeps what committed before and nothing of either transaction.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Rollback: a journal that rejects the abort record still ends the transaction and releases its locks, and the database goes offline")]
    public async Task RollbackAsync_AbortRecordCannotBeWritten_ShouldEndTransactionAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy();
        var engine = SqlDatabaseEngine.Create(QuietOptions("rollback-journal", strategy));
        var database = await engine.CreateDatabaseAsync("journal-db");
        var session = await database.CreateSessionAsync();
        var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var transaction = await session.BeginTransactionAsync();

        // An update that matches no row takes the table's intent-exclusive lock and writes no
        // version, so the abort record is the rollback's only journal append.
        await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 999");

        // Act: the rollback buffers the abort record; the other session's insert commits, and its
        // drain carries the record, which fails.
        int unspentAfterTheRollback;
        int unspent;
        bool tableLockFree;
        bool openContextsAfterTheRollback;
        DatabaseTransactionCommitUnconfirmedException lost;
        using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalWritesContaining(JournalRecordType.RollbackTransaction))
        {
            await transaction.RollbackAsync();
            unspentAfterTheRollback = failures.Remaining;
            openContextsAfterTheRollback = database.Coordinator.GetOpenContexts().Count > 0;

            // The table's exclusive lock, which the writer's intent-exclusive lock would block, is
            // granted at once to an owner no transaction uses, and given back.
            var probe = new TransactionSequence(ulong.MaxValue);
            tableLockFree = database.Coordinator.LockManager.TryAcquire(probe, LockResource.Object(table.ObjectId), LockMode.Exclusive);
            database.Coordinator.LockManager.ReleaseAll(probe);

            lost = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await other.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)"));
            unspent = failures.Remaining;
        }

        await Should.ThrowAsync<DatabaseOfflineException>(async () => await other.ExecuteAsync("INSERT INTO t (id, val) VALUES (3, 30)"));
        await other.DisposeAsync();
        await session.DisposeAsync();
        await engine.DisposeAsync();
        await using var reopened = SqlDatabaseEngine.Create(QuietOptions("rollback-journal", strategy));
        var recovered = await reopened.OpenDatabaseAsync("journal-db");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the rollback wrote nothing, ended the transaction and released its locks; the
        // drain that carried its record failed.
        unspentAfterTheRollback.ShouldBe(1);
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        openContextsAfterTheRollback.ShouldBeFalse();
        tableLockFree.ShouldBeTrue();
        StorageOfflineException.Find(lost).ShouldNotBeNull();
        (await Rows(observer, "SELECT id, val FROM t")).Select(row => (Convert.ToInt64(row[0]), Convert.ToInt64(row[1])))
            .ShouldBe([(1L, 10L)]);
    }

    /// <summary>
    /// A rollback whose undo cannot run still ends the transaction; its locks wait for the
    /// version-purge pass to complete the undo, and the failed storage bracket leaves nothing that
    /// refuses a checkpoint or the retry (#1226). Another storage bracket holds every data page
    /// while the rollback runs; until #1252 a failed journal write was the fault, and a journal
    /// write failure now takes the database offline.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Rollback: a rollback whose undo is deferred holds its locks until the purge pass, and checkpoints keep running")]
    public async Task RollbackAsync_UndoDeferred_ShouldHoldLocksUntilThePurgePassAndKeepCheckpointsRunning()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(QuietOptions("rollback-undo-journal", new FaultInjectingJournalSqlStorageStrategy()));
        var database = await engine.CreateDatabaseAsync("undo-db");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");

        // Act: the undo's storage bracket cannot touch the first page it undoes.
        int locked;
        using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await transaction.RollbackAsync();
            locked = holder.Pages;
        }

        int deferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count;

        // DROP TABLE needs the table's exclusive lock, which the writer's intent lock blocks.
        var drop = other.ExecuteAsync("DROP TABLE t").AsTask();
        await Task.WhenAny(drop, Task.Delay(TimeSpan.FromMilliseconds(250)));
        bool droppedBeforeTheUndo = drop.IsCompleted;
        var rowsBeforeTheUndo = await Rows(session, "SELECT id FROM t");
        database.Coordinator.Checkpoint();
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await drop.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the transaction ended at once, its row stayed invisible, and its locks waited for
        // the undo; the failed bracket left nothing that refuses a checkpoint.
        locked.ShouldBeGreaterThan(0);
        deferred.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        droppedBeforeTheUndo.ShouldBeFalse();
        rowsBeforeTheUndo.Select(row => Convert.ToInt64(row[0])).ShouldBe([1L]);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        database.Coordinator.Checkpoint();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Rollback: an undo that still fails at close is scrubbed at the next open, and the close still closes the storages")]
    public async Task DisposeAsync_UndoStillFailsAtClose_ShouldLeaveNothingOfTheRolledBackTransactionAfterReopen()
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy();
        var engine = SqlDatabaseEngine.Create(QuietOptions("rollback-close", strategy));
        var database = await engine.CreateDatabaseAsync("close-db");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
            await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
            var transaction = await session.BeginTransactionAsync();
            await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
            await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");

            // Another storage bracket holds every data page, through the close: the undo's bracket
            // cannot touch the first page it undoes, so it rolls itself back and the undo is
            // deferred. (Until #1252 a failed journal write was the fault; a journal write
            // failure now takes the database offline.)
            _ = PageWriteLockHolder.LockEveryPage(database.DataStorage); // abandoned with the storage
            await transaction.RollbackAsync();
            transaction.State.ShouldBe(TransactionState.RolledBack);
        }

        // Act: the close retries the undo, which fails the same way.
        var closeFailure = await Should.ThrowAsync<AggregateException>(async () => await engine.DisposeAsync());

        await using var reopened = SqlDatabaseEngine.Create(QuietOptions("rollback-close", strategy));
        var recovered = await reopened.OpenDatabaseAsync("close-db");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the insert is gone and the update is undone.
        closeFailure.Flatten().InnerExceptions.ShouldContain(error => error is Assimalign.Cohesion.Database.Storage.StorageTransactionException);
        (await Rows(observer, "SELECT id, val FROM t")).Select(row => (Convert.ToInt64(row[0]), Convert.ToInt64(row[1])))
            .ShouldBe([(1L, 10L)]);
    }

    // The engine's own maintenance workers stay out of the way, the deferred-undo retry included:
    // these tests drive the purge pass and the checkpoint themselves.
    private static SqlDatabaseEngineOptions QuietOptions(string name, FaultInjectingJournalSqlStorageStrategy strategy) => new()
    {
        EngineName = name,
        StorageStrategy = strategy,
        MaintenanceInterval = TimeSpan.FromHours(1),
        CheckpointInterval = TimeSpan.FromHours(1),
        DeferredUndoRetryDelay = TimeSpan.FromHours(1),
    };

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql);
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();

        var rows = new List<object?[]>();
        await foreach (var row in resultSet!.GetRowsAsync())
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }

            rows.Add(values);
        }

        return rows;
    }
}
