using System;
using System.Collections.Generic;
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
