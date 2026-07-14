using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Tests for the isolation-level seam on the root session contract: the requested
/// level is carried on the transaction (the surface the MVCC session binding will
/// honor per-level), the default is Snapshot, and transactions begun at any level
/// resolve correctly today (the engine executes conservatively at page grain).
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
    [InlineData(IsolationLevel.Serializable)]
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

        // Assert: the level rode the transaction and the commit resolved (the
        // engine executes conservatively at page grain today — never weaker than
        // requested).
        transaction.IsolationLevel.ShouldBe(isolationLevel);
        transaction.State.ShouldBe(TransactionState.Committed);
    }
}
