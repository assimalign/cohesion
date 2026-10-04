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
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("canceled-db");
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Rollback: a journal that rejects the abort record still ends the transaction and releases its locks")]
    public async Task RollbackAsync_AbortRecordCannotBeWritten_ShouldEndTransactionAndReleaseItsLocks()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "rollback-journal",
            StorageStrategy = new FaultInjectingJournalSqlStorageStrategy(),
        });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("journal-db");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        var transaction = await session.BeginTransactionAsync();

        // An update that matches no row takes the table's intent-exclusive lock and writes no
        // version, so the abort record is the rollback's only journal write.
        await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 999");

        // Act
        int unspent;
        using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalWrites(1))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
        }

        // Assert: the record write failed, and the rollback ended the transaction anyway. DROP
        // TABLE needs the table's exclusive lock, which a held intent lock would block.
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        await other.ExecuteAsync("DROP TABLE t").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A rollback whose undo the journal rejects still ends the transaction; its locks wait for the
    /// version-purge pass to complete the undo, and the failed storage bracket leaves nothing that
    /// refuses a checkpoint or the retry (#1226).
    /// </summary>
    /// <param name="skip">
    /// The undo's journal writes to let through before the failing one: 0 fails its storage
    /// bracket's begin record, 1 the before image of the first page it changes.
    /// </param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Rollback: a rollback whose undo the journal rejects holds its locks until the purge pass, and checkpoints keep running")]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RollbackAsync_JournalRejectsTheUndo_ShouldHoldLocksUntilThePurgePassAndKeepCheckpointsRunning(int skip)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(QuietOptions("rollback-undo-journal", new FaultInjectingJournalSqlStorageStrategy()));
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("undo-db");
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");

        // Act: the undo's storage bracket makes the rollback's first journal writes.
        int unspent;
        using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalWrites(1, skip))
        {
            await transaction.RollbackAsync();
            unspent = failures.Remaining;
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
        unspent.ShouldBe(0);
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

            // The undo's storage bracket begins (the first write) and fails at its first page
            // image (the second), so the bracket rolls itself back and the undo is deferred.
            using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalWrites(1, skip: 1))
            {
                await transaction.RollbackAsync();
                failures.Remaining.ShouldBe(0);
            }

            transaction.State.ShouldBe(TransactionState.RolledBack);
        }

        // Act: the close's first journal writes are the undo's retry, which fails the same way.
        AggregateException closeFailure;
        using (var failures = FaultInjectingJournalSqlStorageStrategy.FailJournalWrites(1, skip: 1))
        {
            closeFailure = await Should.ThrowAsync<AggregateException>(async () => await engine.DisposeAsync());
            failures.Remaining.ShouldBe(0);
        }

        await using var reopened = SqlDatabaseEngine.Create(QuietOptions("rollback-close", strategy));
        var recovered = await reopened.OpenDatabaseAsync("close-db");
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the insert is gone and the update is undone.
        closeFailure.InnerExceptions.ShouldContain(error => error is IOException);
        (await Rows(observer, "SELECT id, val FROM t")).Select(row => (Convert.ToInt64(row[0]), Convert.ToInt64(row[1])))
            .ShouldBe([(1L, 10L)]);
    }

    // The engine's own maintenance workers stay out of the way: these tests drive the purge
    // pass and the checkpoint themselves.
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

    private static async Task<List<object?[]>> Rows(IDatabaseSession session, string sql)
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
