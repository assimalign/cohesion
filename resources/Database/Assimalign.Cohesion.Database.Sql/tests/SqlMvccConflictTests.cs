using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Row-grain write-conflict tests (#909): concurrent writers to disjoint rows
/// both commit (page-grain conflicts are gone as a user-visible surface),
/// same-row conflicts resolve through the lock manager (wait, then
/// first-updater-wins), deadlock victims surface as the root's retryable
/// <see cref="DatabaseTransactionDeadlockException"/>, DDL interlocks with row
/// writers through table-grain intent locks, and lock waits honor cancellation.
/// </summary>
public sealed class SqlMvccConflictTests
{
    private static async Task<(SqlDatabaseInstance Database, IDatabaseSession Session)> CreateSessionAsync(
        SqlDatabaseEngine engine, string name)
    {
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(name);
        var session = await database.CreateSessionAsync();
        return (database, session);
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Conflicts: Two transactions updating disjoint rows of one table both commit")]
    public async Task Update_DisjointRowsConcurrently_ShouldBothCommit()
    {
        // Arrange: two tiny rows — they share the same data page, the exact case
        // the page-grain engine failed with StorageTransactionException.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "conf-disjoint" });
        var (database, sessionA) = await CreateSessionAsync(engine, "disjoint-db");
        await using var _ = sessionA;
        await using var sessionB = await database.CreateSessionAsync();

        await sessionA.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await sessionA.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        await sessionA.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");

        // Act: both transactions write while the other is still uncommitted.
        var transactionA = await sessionA.BeginTransactionAsync();
        var transactionB = await sessionB.BeginTransactionAsync();

        await sessionA.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
        await sessionB.ExecuteAsync("UPDATE t SET val = 21 WHERE id = 2");

        await transactionA.CommitAsync();
        await transactionB.CommitAsync();

        // Assert: no page-grain conflict surfaced; both effects are visible.
        var rows = await Rows(sessionA, "SELECT val FROM t ORDER BY id");
        rows[0][0].ShouldBe(11L);
        rows[1][0].ShouldBe(21L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Conflicts: Two transactions inserting into one table both commit")]
    public async Task Insert_Concurrently_ShouldBothCommit()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "conf-inserts" });
        var (database, sessionA) = await CreateSessionAsync(engine, "inserts-db");
        await using var _ = sessionA;
        await using var sessionB = await database.CreateSessionAsync();

        await sessionA.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

        // Act: interleaved inserts allocating into the same page.
        var transactionA = await sessionA.BeginTransactionAsync();
        var transactionB = await sessionB.BeginTransactionAsync();

        await sessionA.ExecuteAsync("INSERT INTO t (id) VALUES (1)");
        await sessionB.ExecuteAsync("INSERT INTO t (id) VALUES (2)");

        await transactionA.CommitAsync();
        await transactionB.CommitAsync();

        // Assert
        (await Rows(sessionA, "SELECT id FROM t")).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Conflicts: Same-row second writer conflicts after the first commits (first-updater-wins), then a retry succeeds")]
    public async Task Update_SameRowAfterConcurrentCommit_ShouldConflictAndBeRetryable()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "conf-samerow" });
        var (database, sessionA) = await CreateSessionAsync(engine, "samerow-db");
        await using var _ = sessionA;
        await using var sessionB = await database.CreateSessionAsync();

        await sessionA.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await sessionA.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");

        // Act: A updates and commits; B (whose snapshot predates A's commit)
        // then targets the same row.
        var transactionA = await sessionA.BeginTransactionAsync();
        var transactionB = await sessionB.BeginTransactionAsync();

        await sessionA.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
        await transactionA.CommitAsync();

        // Assert: B's write fails with the retryable abort (kind observable via
        // the root's typed exception), B's session stays usable, and the retry
        // on a fresh transaction succeeds against the committed state.
        var conflict = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await sessionB.ExecuteAsync("UPDATE t SET val = 12 WHERE id = 1"));
        conflict.Message.ShouldContain("conflict");

        await transactionB.RollbackAsync();

        await sessionB.ExecuteAsync("UPDATE t SET val = 12 WHERE id = 1");
        (await Rows(sessionB, "SELECT val FROM t WHERE id = 1"))[0][0].ShouldBe(12L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Conflicts: A same-row writer waits on the lock and proceeds when the holder rolls back")]
    public async Task Update_SameRowWhileLocked_ShouldWaitThenProceedAfterHolderRollback()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "conf-wait" });
        var (database, sessionA) = await CreateSessionAsync(engine, "wait-db");
        await using var _ = sessionA;
        await using var sessionB = await database.CreateSessionAsync();

        await sessionA.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await sessionA.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");

        // A's transaction tombstones the row and holds its exclusive lock.
        var transactionA = await sessionA.BeginTransactionAsync();
        await sessionA.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");

        // Act: B targets the same row and must park on the lock — provably: the
        // task cannot complete while A's transaction holds it, so the bounded
        // observation window can never produce a false failure.
        var waiting = sessionB.ExecuteAsync("UPDATE t SET val = 12 WHERE id = 1").AsTask();
        var first = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500)));
        first.ShouldNotBe(waiting);

        // The holder rolls back: its tombstone is undone BEFORE its locks
        // release, so B's latest-state re-validation passes and B applies.
        await transactionA.RollbackAsync();
        await waiting;

        // Assert
        (await Rows(sessionA, "SELECT val FROM t WHERE id = 1"))[0][0].ShouldBe(12L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Conflicts: A lock cycle aborts the requester as deadlock victim; the survivor completes")]
    public async Task Update_LockCycle_ShouldAbortVictimAndLetSurvivorComplete()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "conf-deadlock" });
        var (database, sessionA) = await CreateSessionAsync(engine, "deadlock-db");
        await using var _ = sessionA;
        await using var sessionB = await database.CreateSessionAsync();

        await sessionA.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await sessionA.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        await sessionA.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");

        var transactionA = await sessionA.BeginTransactionAsync();
        var transactionB = await sessionB.BeginTransactionAsync();

        // A locks row 1, B locks row 2.
        await sessionA.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
        await sessionB.ExecuteAsync("UPDATE t SET val = 21 WHERE id = 2");

        // Act: A reaches for row 2 (parks behind B — provably held), then B
        // reaches for row 1: a wait-for cycle. Whichever request closes the
        // cycle aborts as the victim; the other request is granted once the
        // victim's transaction rolls back.
        var crossA = sessionA.ExecuteAsync("UPDATE t SET val = val + 100 WHERE id = 2").AsTask();
        await Task.WhenAny(crossA, Task.Delay(TimeSpan.FromMilliseconds(500)));

        var crossB = sessionB.ExecuteAsync("UPDATE t SET val = val + 100 WHERE id = 1").AsTask();

        var firstCompleted = await Task.WhenAny(crossA, crossB);
        firstCompleted.IsFaulted.ShouldBeTrue();

        var deadlock = firstCompleted.Exception!.InnerException
            .ShouldBeOfType<DatabaseTransactionDeadlockException>();
        deadlock.Message.ShouldContain("deadlock");

        // The victim rolls back (retryable by construction); the survivor's
        // parked statement is then granted and completes, and its transaction
        // commits.
        if (firstCompleted == crossA)
        {
            await transactionA.RollbackAsync();
            await crossB;
            await transactionB.CommitAsync();
        }
        else
        {
            await transactionB.RollbackAsync();
            await crossA;
            await transactionA.CommitAsync();
        }

        // Assert: the victim's session is immediately usable for a retry.
        var victimSession = firstCompleted == crossA ? sessionA : sessionB;
        await victimSession.ExecuteAsync("UPDATE t SET val = 999 WHERE id = 1");
        (await Rows(victimSession, "SELECT val FROM t WHERE id = 1"))[0][0].ShouldBe(999L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Conflicts: DROP TABLE waits for in-flight row writers (intent-lock interlock)")]
    public async Task DropTable_WithActiveRowWriter_ShouldWaitForWriterToFinish()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "conf-ddl" });
        var (database, sessionA) = await CreateSessionAsync(engine, "ddl-db");
        await using var _ = sessionA;
        await using var sessionB = await database.CreateSessionAsync();

        await sessionA.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await sessionA.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // A's transaction holds an IntentExclusive lock on the table.
        var transactionA = await sessionA.BeginTransactionAsync();
        await sessionA.ExecuteAsync("UPDATE t SET id = 2 WHERE id = 1");

        // Act: DROP TABLE requests the Exclusive table lock and must park —
        // provably: it cannot be granted while A's transaction holds IX.
        var dropping = sessionB.ExecuteAsync("DROP TABLE t").AsTask();
        var first = await Task.WhenAny(dropping, Task.Delay(TimeSpan.FromMilliseconds(500)));
        first.ShouldNotBe(dropping);

        // The writer finishes; the drop proceeds.
        await transactionA.CommitAsync();
        await dropping;

        // Assert: the table is gone.
        await Should.ThrowAsync<DatabaseException>(async () =>
            await sessionA.ExecuteAsync("SELECT id FROM t"));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Conflicts: A lock wait honors the session's cancellation token")]
    public async Task Update_LockWaitCancelled_ShouldThrowOperationCanceled()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "conf-cancel" });
        var (database, sessionA) = await CreateSessionAsync(engine, "cancel-db");
        await using var _ = sessionA;
        await using var sessionB = await database.CreateSessionAsync();

        await sessionA.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await sessionA.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");

        var transactionA = await sessionA.BeginTransactionAsync();
        await sessionA.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");

        // Act: B parks on the row lock (provably held by A), then cancels.
        using var cancellation = new CancellationTokenSource();
        var waiting = sessionB.ExecuteAsync("UPDATE t SET val = 12 WHERE id = 1", null, cancellation.Token).AsTask();
        var first = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500)));
        first.ShouldNotBe(waiting);

        cancellation.Cancel();

        // Assert: the wait aborts with cancellation; the holder is unaffected
        // and B's session stays usable.
        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);

        await transactionA.CommitAsync();
        (await Rows(sessionB, "SELECT val FROM t WHERE id = 1"))[0][0].ShouldBe(11L);
    }
}
