using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Tests for the isolation-level seam on the root session contract: the requested
/// level rides an MVCC transaction context on the database's transaction manager,
/// the default is Snapshot, and Serializable is rejected (no serialization-conflict
/// detection yet — the engine must never run weaker than requested).
/// </summary>
public sealed class SqlTransactionIsolationTests
{
    private static async Task<IDatabaseSession> CreateSessionAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("iso-db");
        return await database.CreateSessionAsync();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Isolation: The default transaction begins at Snapshot")]
    public async Task BeginTransactionAsync_WithoutLevel_ShouldDefaultToSnapshot()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "iso-default" });
        await using var session = await CreateSessionAsync(engine);

        // Act
        var transaction = await session.BeginTransactionAsync();

        // Assert
        transaction.IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
        await transaction.RollbackAsync();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Isolation: A requested level is carried on the transaction and commits resolve")]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.Snapshot)]
    public async Task BeginTransactionAsync_WithLevel_ShouldCarryLevelAndResolve(IsolationLevel isolationLevel)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "iso-levels" });
        await using var session = await CreateSessionAsync(engine);
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

        // Act: begin at the requested level, write, commit.
        var transaction = await session.BeginTransactionAsync(isolationLevel);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");
        await transaction.CommitAsync();

        // Assert: the level rode the transaction and the commit resolved through
        // the MVCC transaction manager.
        transaction.IsolationLevel.ShouldBe(isolationLevel);
        transaction.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Isolation: Serializable is rejected until conflict detection exists")]
    public async Task BeginTransactionAsync_Serializable_ShouldBeRejected()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "iso-serializable" });
        await using var session = await CreateSessionAsync(engine);

        // Act + Assert: the engine has no serialization-conflict detection, and
        // the root contract forbids running a transaction weaker than requested —
        // so the request is rejected rather than silently downgraded.
        var exception = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.BeginTransactionAsync(IsolationLevel.Serializable));

        exception.Message.ShouldContain("Serializable");
    }
}
