using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Tests for the MVCC session binding (#907): explicit and auto-commit SQL
/// statements run under an <c>ITransactionContext</c> from the database's
/// transaction manager, paired one-to-one with a storage bracket under a single
/// shared sequence, and transaction-kernel aborts surface wrapped in the area
/// root's <see cref="DatabaseTransactionAbortedException"/>.
/// </summary>
public sealed class SqlMvccBindingTests
{
    private static async Task<(SqlDatabaseInstance Database, IDatabaseSession Session)> CreateSessionAsync(
        SqlDatabaseEngine engine, string name)
    {
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync(name);
        var session = await database.CreateSessionAsync();
        return (database, session);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC binding: An explicit transaction pairs a manager context with a storage bracket under one sequence")]
    public async Task BeginTransactionAsync_Explicit_ShouldPairContextAndBracketUnderOneSequence()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "mvcc-pairing" });
        var (database, session) = await CreateSessionAsync(engine, "pairing-db");
        await using var _ = session;

        // Act
        var transaction = (SqlDatabaseTransaction)await session.BeginTransactionAsync();

        // Assert: one paired transaction, and the physical bracket adopted the
        // logical context's sequence (the single-namespace invariant recovery
        // classification depends on).
        database.Coordinator.PairedTransactionCount.ShouldBe(1);
        transaction.StorageTransaction.Sequence.ShouldBe((long)transaction.Context.Sequence.Value);

        await transaction.CommitAsync();
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC binding: Auto-commit statements ride a one-statement manager transaction")]
    public async Task ExecuteAsync_AutoCommit_ShouldRideManagerTransaction()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "mvcc-autocommit" });
        var (database, session) = await CreateSessionAsync(engine, "autocommit-db");
        await using var _ = session;
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

        var before = database.Coordinator.Manager.OldestActive;

        // Act: an auto-commit statement.
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Assert: the statement consumed a manager transaction — the manager's
        // idle oldest-active bound (last assigned + 1) advanced past it — and
        // nothing stayed paired behind it.
        database.Coordinator.Manager.OldestActive.Value.ShouldBeGreaterThan(before.Value);
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC binding: Rollback undoes the transaction's writes through the manager")]
    public async Task RollbackAsync_AfterWrites_ShouldUndoThroughManager()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "mvcc-rollback" });
        var (database, session) = await CreateSessionAsync(engine, "rollback-db");
        await using var _ = session;
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

        // Act: write inside an explicit transaction, then roll back.
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (42)");
        await transaction.RollbackAsync();

        // Assert: the write is gone (physical bracket revert), the pairing is
        // released, and the session is immediately usable.
        var result = await session.ExecuteAsync("SELECT COUNT(*) AS n FROM t");
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();
        await foreach (var row in resultSet!.GetRowsAsync())
        {
            row.GetValue(0).ShouldBe(0L);
        }

        database.Coordinator.PairedTransactionCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC binding: A transaction-kernel abort surfaces wrapped in DatabaseTransactionAbortedException")]
    public async Task CommitAsync_KernelAbort_ShouldSurfaceWrappedAbort()
    {
        // Arrange: a transaction object bound to database A's coordinator but
        // carrying a context begun on database B's manager — the manager rejects
        // the foreign context with the kernel's TransactionAbortedException, which
        // must cross the model boundary wrapped in the area root's typed abort.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "mvcc-abort" });
        var (databaseA, sessionA) = await CreateSessionAsync(engine, "abort-a");
        var (databaseB, sessionB) = await CreateSessionAsync(engine, "abort-b");
        await using var _ = sessionA;
        await using var __ = sessionB;

        var foreignContext = await databaseB.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var crossBound = new SqlDatabaseTransaction(databaseA.Coordinator, foreignContext);

        // Act + Assert
        var exception = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await crossBound.CommitAsync());

        exception.InnerException.ShouldBeOfType<TransactionAbortedException>();

        await databaseB.Coordinator.RollbackAsync(foreignContext);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC binding: Disposing the engine aborts still-active transactions before storage closes")]
    public async Task DisposeAsync_WithActiveTransaction_ShouldAbortCleanly()
    {
        // Arrange
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "mvcc-dispose" });
        var (database, session) = await CreateSessionAsync(engine, "dispose-db");
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Act + Assert: disposal aborts the in-flight transaction through the
        // manager (bracket rolled back while storage is still open) — no throw.
        await engine.DisposeAsync();
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
    }
}
