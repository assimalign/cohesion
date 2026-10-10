using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// An <c>ON DELETE CASCADE</c> that waited for a child row's writer (#1370). The cascade reads a
/// parent row's children in latest state once it holds the parent's exclusive lock, so it reaches
/// the child versions a writer committed while the statement waited for that lock. Under
/// <see cref="IsolationLevel.ReadCommitted"/> those versions are the statement's to delete. Under
/// <see cref="IsolationLevel.Snapshot"/> they are newer than the transaction's snapshot: the
/// cascade deleted them anyway and removed their index entries through that snapshot, which does
/// not see them, so the index delete matched nothing, the deleted child's primary-key entry stayed
/// live, and the child's key could never be inserted again. The cascade now fails the statement
/// first-updater-wins, as PostgreSQL fails a REPEATABLE READ or SERIALIZABLE cascade that meets a
/// row updated after its snapshot with a serialization failure.
/// </summary>
public sealed class SqlCascadeSnapshotConflictTests
{
    private const string Delete = "DELETE FROM p WHERE id = 1";

    /// <summary>
    /// The #1363 repro at <see cref="IsolationLevel.Snapshot"/>, in an explicit transaction and in
    /// auto-commit (which runs at <see cref="IsolationLevel.Snapshot"/>): a child writer that
    /// updates the child row, or inserts a second child, commits while the delete waits for the
    /// parent row. The delete fails with the retryable conflict, writes nothing, and a retry after
    /// the writer committed deletes the closure and frees its keys.
    /// </summary>
    /// <param name="explicitTransaction">Whether the delete runs in an explicit transaction rather than auto-commit.</param>
    /// <param name="childChange">The child writer's statement.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Cascade: A snapshot cascade that reaches a child version newer than its snapshot fails first-updater-wins and deletes nothing (#1370)")]
    [InlineData(true, "UPDATE c SET pid = pid WHERE id = 10")]
    [InlineData(true, "INSERT INTO c (id, pid) VALUES (11, 1)")]
    [InlineData(false, "UPDATE c SET pid = pid WHERE id = 10")]
    [InlineData(false, "INSERT INTO c (id, pid) VALUES (11, 1)")]
    public async Task Cascade_SnapshotReachesANewerChildVersion_ShouldFailFirstUpdaterWinsAndDeleteNothing(bool explicitTransaction, string childChange)
    {
        // Arrange: the child writer holds a shared lock on the parent row its child references, and
        // the deleter's snapshot is taken while it is in flight.
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var setup = await database.CreateSessionAsync();
        await using var writerSession = await database.CreateSessionAsync();
        await using var deleterSession = await database.CreateSessionAsync();
        var writer = await writerSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await writerSession.ExecuteAsync(childChange, cancellationToken: TestTimeout.Token());
        var expectedChildren = await RowsAsync(writerSession, "SELECT id, pid FROM c ORDER BY id");
        var deleter = explicitTransaction
            ? await deleterSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token())
            : null;

        // Act: the delete waits for the parent row while the child writer commits.
        var pending = deleterSession.ExecuteAsync(Delete, cancellationToken: TestTimeout.Token()).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await writer.CommitAsync(TestTimeout.Token());
        var conflict = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
        var stateAfter = deleter?.State;
        var deleterSees = deleter is null ? null : await RowsAsync(deleterSession, "SELECT id, pid FROM c ORDER BY id");
        if (deleter is not null)
        {
            await deleter.RollbackAsync(TestTimeout.Token());
        }

        var parents = await RowsAsync(setup, "SELECT id FROM p");
        var children = await RowsAsync(setup, "SELECT id, pid FROM c ORDER BY id");
        var seek = await RowsAsync(setup, "SELECT id, pid FROM c WHERE id = 10");
        var duplicate = await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
            await setup.ExecuteAsync("INSERT INTO c (id, pid) VALUES (10, 1)", cancellationToken: TestTimeout.Token()));
        var retried = await deleterSession.ExecuteAsync(Delete, cancellationToken: TestTimeout.Token());
        var childrenAfterRetry = await RowsAsync(setup, "SELECT id FROM c");
        await setup.ExecuteAsync("INSERT INTO p (id) VALUES (1)", cancellationToken: TestTimeout.Token());
        var reinserted = await setup.ExecuteAsync("INSERT INTO c (id, pid) VALUES (10, 1), (11, 1)", cancellationToken: TestTimeout.Token());

        // Assert: the retryable conflict, never a deadlock, naming the child table; the failed
        // statement left an explicit transaction active on its own snapshot.
        conflict.ShouldBeOfType<DatabaseTransactionAbortedException>();
        conflict.Message.ShouldContain("first-updater-wins", Case.Sensitive);
        conflict.Message.ShouldContain("'dbo.c'", Case.Sensitive);
        conflict.InnerException.ShouldBeOfType<TransactionAbortedException>();
        if (explicitTransaction)
        {
            stateAfter.ShouldBe(TransactionState.Active);
            deleterSees.ShouldBe(["10,1"]);
        }

        // Nothing was half-deleted: the parent, every child and the primary-key entry of the child
        // the writer replaced are still live, and the index seek agrees with the scan.
        parents.ShouldBe(["1"]);
        children.ShouldBe(expectedChildren);
        seek.ShouldBe(["10,1"]);
        duplicate.ConstraintKind.ShouldBe("UNIQUE");

        // The retry, after the writer committed, deletes the closure and frees its keys.
        retried.AffectedCount.ShouldBe(1);
        childrenAfterRetry.ShouldBeEmpty();
        reinserted.AffectedCount.ShouldBe(2);
        (await RowsAsync(setup, "SELECT id, pid FROM c ORDER BY id")).ShouldBe(["10,1", "11,1"]);
    }

    /// <summary>
    /// The same race under <see cref="IsolationLevel.ReadCommitted"/>: the statement's snapshot is
    /// not the transaction's, so the cascade deletes the version the writer committed, with its
    /// index entries, and the keys are free again.
    /// </summary>
    /// <param name="childChange">The child writer's statement.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Cascade: A read-committed cascade that reaches a child version newer than its snapshot deletes it and frees its keys (#1370)")]
    [InlineData("UPDATE c SET pid = pid WHERE id = 10")]
    [InlineData("INSERT INTO c (id, pid) VALUES (11, 1)")]
    public async Task Cascade_ReadCommittedReachesANewerChildVersion_ShouldDeleteItAndFreeItsKeys(string childChange)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var setup = await database.CreateSessionAsync();
        await using var writerSession = await database.CreateSessionAsync();
        await using var deleterSession = await database.CreateSessionAsync();
        var writer = await writerSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await writerSession.ExecuteAsync(childChange, cancellationToken: TestTimeout.Token());
        var deleter = await deleterSession.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());

        // Act
        var pending = deleterSession.ExecuteAsync(Delete, cancellationToken: TestTimeout.Token()).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await writer.CommitAsync(TestTimeout.Token());
        var deleted = await pending;
        await deleter.CommitAsync(TestTimeout.Token());
        var children = await RowsAsync(setup, "SELECT id FROM c");
        await setup.ExecuteAsync("INSERT INTO p (id) VALUES (1)", cancellationToken: TestTimeout.Token());
        var reinserted = await setup.ExecuteAsync("INSERT INTO c (id, pid) VALUES (10, 1), (11, 1)", cancellationToken: TestTimeout.Token());

        // Assert
        deleted.AffectedCount.ShouldBe(1);
        children.ShouldBeEmpty();
        reinserted.AffectedCount.ShouldBe(2);
        (await RowsAsync(setup, "SELECT id, pid FROM c ORDER BY id")).ShouldBe(["10,1", "11,1"]);
    }

    /// <summary>
    /// <see cref="IsolationLevel.Serializable"/> fixes the snapshot at begin as
    /// <see cref="IsolationLevel.Snapshot"/> does, so the cascade fails the same way. The session
    /// refuses a serializable BEGIN, so the statement runs through the executor under a context the
    /// coordinator began, as the kernel would hand it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: A serializable cascade that reaches a child version newer than its snapshot fails first-updater-wins (#1370)")]
    public async Task Cascade_SerializableReachesANewerChildVersion_ShouldFailFirstUpdaterWins()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        var coordinator = database.Coordinator;
        await using var setup = await database.CreateSessionAsync();
        await using var writerSession = await database.CreateSessionAsync();
        var writer = await writerSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await writerSession.ExecuteAsync("UPDATE c SET pid = pid WHERE id = 10", cancellationToken: TestTimeout.Token());
        var deleter = await coordinator.BeginAsync(IsolationLevel.Serializable, TestTimeout.Token());

        // Act
        var pending = ExecuteAsync(database, deleter, Delete);
        pending.IsCompleted.ShouldBeFalse();
        await writer.CommitAsync(TestTimeout.Token());
        var conflict = await Should.ThrowAsync<TransactionAbortedException>(async () => await pending);
        var stateAfter = deleter.State;
        await coordinator.RollbackAsync(deleter, TestTimeout.Token());
        var children = await RowsAsync(setup, "SELECT id, pid FROM c ORDER BY id");

        // Assert
        conflict.ShouldBeOfType<TransactionAbortedException>();
        conflict.Message.ShouldContain("first-updater-wins", Case.Sensitive);
        stateAfter.ShouldBe(TransactionState.Active);
        (await RowsAsync(setup, "SELECT id FROM p")).ShouldBe(["1"]);
        children.ShouldBe(["10,1"]);
    }

    /// <summary>
    /// With no concurrent writer the check changes nothing: a cascade in a transaction whose
    /// snapshot is fixed deletes the whole closure, the children this transaction inserted itself
    /// included, and every deleted key is free again once it commits.
    /// </summary>
    /// <param name="isolationLevel">The deleting transaction's isolation level.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Cascade: A cascade with no concurrent writer deletes the whole closure, its own inserts included (#1370)")]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Cascade_NoConcurrentWriter_ShouldDeleteTheWholeClosure(IsolationLevel isolationLevel)
    {
        // Arrange: a grandchild level, a sibling parent that keeps its children, and children the
        // deleting transaction inserts before it deletes.
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var setup = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE g (id INT PRIMARY KEY, cid INT, CONSTRAINT fk_g FOREIGN KEY(cid) REFERENCES c(id) ON DELETE CASCADE)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO p (id) VALUES (2)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO c (id, pid) VALUES (11, 1), (20, 2)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO g (id, cid) VALUES (100, 10), (101, 11), (200, 20)", cancellationToken: TestTimeout.Token());
        const string ownChild = "INSERT INTO c (id, pid) VALUES (12, 1)";
        const string ownGrandchild = "INSERT INTO g (id, cid) VALUES (102, 12)";

        // Act
        QueryResult deleted;
        List<string> childrenInside;
        List<string> grandchildrenInside;
        if (isolationLevel == IsolationLevel.Serializable)
        {
            var coordinator = database.Coordinator;
            var transaction = await coordinator.BeginAsync(isolationLevel, TestTimeout.Token());
            await ExecuteAsync(database, transaction, ownChild);
            await ExecuteAsync(database, transaction, ownGrandchild);
            deleted = await ExecuteAsync(database, transaction, Delete);
            childrenInside = await ReadRowsAsync(await ExecuteAsync(database, transaction, "SELECT id FROM c ORDER BY id"));
            grandchildrenInside = await ReadRowsAsync(await ExecuteAsync(database, transaction, "SELECT id FROM g ORDER BY id"));
            await coordinator.CommitAsync(transaction, TestTimeout.Token());
        }
        else
        {
            await using var session = await database.CreateSessionAsync();
            var transaction = await session.BeginTransactionAsync(isolationLevel, TestTimeout.Token());
            await session.ExecuteAsync(ownChild, cancellationToken: TestTimeout.Token());
            await session.ExecuteAsync(ownGrandchild, cancellationToken: TestTimeout.Token());
            deleted = await session.ExecuteAsync(Delete, cancellationToken: TestTimeout.Token());
            childrenInside = await RowsAsync(session, "SELECT id FROM c ORDER BY id");
            grandchildrenInside = await RowsAsync(session, "SELECT id FROM g ORDER BY id");
            await transaction.CommitAsync(TestTimeout.Token());
        }

        await setup.ExecuteAsync("INSERT INTO p (id) VALUES (1)", cancellationToken: TestTimeout.Token());
        var reinsertedChildren = await setup.ExecuteAsync("INSERT INTO c (id, pid) VALUES (10, 1), (11, 1), (12, 1)", cancellationToken: TestTimeout.Token());
        var reinsertedGrandchildren = await setup.ExecuteAsync("INSERT INTO g (id, cid) VALUES (100, 10), (101, 11), (102, 12)", cancellationToken: TestTimeout.Token());

        // Assert: the closure went in one statement and only the sibling's rows stayed; every
        // deleted key, its index entries included, is free again.
        deleted.AffectedCount.ShouldBe(1);
        childrenInside.ShouldBe(["20"]);
        grandchildrenInside.ShouldBe(["200"]);
        reinsertedChildren.AffectedCount.ShouldBe(3);
        reinsertedGrandchildren.AffectedCount.ShouldBe(3);
        (await RowsAsync(setup, "SELECT id FROM g ORDER BY id")).ShouldBe(["100", "101", "102", "200"]);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cascade-snapshot-conflict" });

    /// <summary>A parent <c>p(1)</c> with one cascading child <c>c(10, 1)</c>.</summary>
    private static async Task<SqlDatabase> CreateDatabaseAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("db");
        await using var setup = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_c FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO p (id) VALUES (1)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO c (id, pid) VALUES (10, 1)", cancellationToken: TestTimeout.Token());
        return database;
    }

    /// <summary>
    /// Runs one statement through the executor under a context the coordinator began, the path a
    /// session takes for an explicit transaction.
    /// </summary>
    private static Task<QueryResult> ExecuteAsync(SqlDatabase database, TransactionContext transaction, string sql)
        => new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
            .ExecuteAsync(SqlQueryRequest.FromSql(sql), new SqlStatementContext(transaction, database.Coordinator), TestTimeout.Token());

    private static async Task<List<string>> RowsAsync(SqlDatabaseSession session, string sql)
        => await ReadRowsAsync(await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

    /// <summary>Reads each row as its values joined with commas.</summary>
    private static async Task<List<string>> ReadRowsAsync(QueryResult result)
    {
        await using var set = result.ShouldBeAssignableTo<QueryResultSet>().ShouldNotBeNull();
        var rows = new List<string>();
        await foreach (var row in set.GetRowsAsync(TestTimeout.Token()))
        {
            var values = new long[row.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = Convert.ToInt64(row.GetValue(index));
            }

            rows.Add(string.Join(",", values));
        }

        return rows;
    }
}
