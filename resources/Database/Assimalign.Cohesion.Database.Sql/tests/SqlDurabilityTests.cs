using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Durability tests for the SQL engine over the storage-transaction WAL: rollback
/// really undoes applied changes, and committed data survives an engine restart.
/// </summary>
public sealed class SqlDurabilityTests : IDisposable
{
    private readonly string _rootPath;

    public SqlDurabilityTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-durability", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }
        }
    }

    private static SqlQueryRequest InsertRequest(string label)
        => SqlQueryRequest.FromSql("INSERT INTO t (label) VALUES (@label);", new Dictionary<string, object?> { ["label"] = label });

    private static SqlQueryRequest SelectRequest() => SqlQueryRequest.FromSql("SELECT label FROM t;");

    private static SqlQueryRequest CreateTableRequest() => SqlQueryRequest.FromSql("CREATE TABLE t (label VARCHAR(100));");

    private static async Task<int> CountRowsAsync(IDatabaseSession session)
    {
        var result = await session.ExecuteAsync(SelectRequest());
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();

        int count = 0;
        await foreach (var _ in resultSet!.GetRowsAsync())
        {
            count++;
        }

        return count;
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Rollback: Should undo applied row mutations in memory")]
    public async Task Rollback_AfterInsert_ShouldUndoAppliedMutations()
    {
        // Arrange
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "durability", RootPath = _rootPath });
        await using var _ = engine;

        var database = await engine.CreateDatabaseAsync("rollback-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync(CreateTableRequest());

        // Act: insert inside an explicit transaction, then roll back.
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync(InsertRequest("discarded-row"));
        await transaction.RollbackAsync();

        // Assert: the rolled-back row is not visible to a subsequent scan.
        (await CountRowsAsync(session)).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Restart: Committed rows survive an engine restart")]
    public async Task Restart_AfterCommit_ShouldRecoverCommittedRows()
    {
        // Arrange: write two committed rows, then dispose the engine (clean shutdown).
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "durability", RootPath = _rootPath });

        var database = await engine.CreateDatabaseAsync("restart-db");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync(CreateTableRequest());
            var transaction = await session.BeginTransactionAsync();
            await session.ExecuteAsync(InsertRequest("row-1"));
            await session.ExecuteAsync(InsertRequest("row-2"));
            await transaction.CommitAsync();
        }

        await engine.DisposeAsync();

        // Act: a fresh engine over the same root reopens the database.
        var reopenedEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "durability", RootPath = _rootPath });
        await using var __ = reopenedEngine;

        var reopenedDatabase = await reopenedEngine.OpenDatabaseAsync("restart-db");
        await using var verifySession = await reopenedDatabase.CreateSessionAsync();

        // Assert
        (await CountRowsAsync(verifySession)).ShouldBe(2);
    }
}
