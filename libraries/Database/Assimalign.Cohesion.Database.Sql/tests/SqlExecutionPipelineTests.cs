using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Language.Sql;

public class SqlExecutionPipelineTests : IDisposable
{
    private readonly string _rootPath;

    public SqlExecutionPipelineTests()
    {
        // Each test instance gets its own isolated temp directory
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        // Clean up the temp directory after each test
        if (Directory.Exists(_rootPath))
        {
            try
            {
               Directory.Delete(_rootPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    /// <summary>
    /// Helper to create a file-backed SQL engine that is already started.
    /// </summary>
    private async Task<SqlDatabaseEngine> CreateEngine()
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "test-engine",
            RootPath = _rootPath
        });
        await engine.StartAsync();
        return engine;
    }

    /// <summary>
    /// Helper to create a SqlQueryRequest for an INSERT command.
    /// </summary>
    private static SqlQueryRequest CreateInsertRequest(byte[] row)
    {
        var expression = new SqlQueryExpression(SqlQueryCommandType.Insert, "INSERT INTO test", null);
        var statement = new SqlQueryStatement(expression);
        return new SqlQueryRequest(statement, new Dictionary<string, object?>
        {
            ["row"] = row
        });
    }

    /// <summary>
    /// Helper to create a SqlQueryRequest for a SELECT command.
    /// </summary>
    private static SqlQueryRequest CreateSelectRequest()
    {
        var expression = new SqlQueryExpression(SqlQueryCommandType.Select, "SELECT * FROM test", null);
        var statement = new SqlQueryStatement(expression);
        return new SqlQueryRequest(statement);
    }

    /// <summary>
    /// Helper to create a fixed-width row with an int id and a string name.
    /// </summary>
    private static byte[] MakeRow(int id, string name)
    {
        var row = new byte[68];
        BitConverter.TryWriteBytes(row, id);
        Encoding.UTF8.GetBytes(name).CopyTo(row.AsSpan(4));
        return row;
    }

    // ── Session Lifecycle ──────────────────────────────────────────────

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - CreateSession: Should return open session")]
    public async Task CreateSession_ShouldReturnOpenSession()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");

        await using var session = await db.CreateSessionAsync();

        Assert.Equal(SessionState.Open, session.State);
        Assert.Equal(db, session.Database);
        Assert.Null(session.CurrentTransaction);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - DisposeSession: Should transition to closed state")]
    public async Task DisposeSession_ShouldTransitionToClosedState()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await db.CreateSessionAsync();

        await session.DisposeAsync();

        Assert.Equal(SessionState.Closed, session.State);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - DisposeSession: Should be idempotent")]
    public async Task DisposeSession_ShouldBeIdempotent()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await db.CreateSessionAsync();

        await session.DisposeAsync();
        await session.DisposeAsync(); // Should not throw
    }

    // ── Transaction Lifecycle ──────────────────────────────────────────

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - BeginTransaction: Should return active transaction")]
    public async Task BeginTransaction_ShouldReturnActiveTransaction()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var tx = await session.BeginTransactionAsync();

        Assert.NotNull(tx);
        Assert.Equal(TransactionState.Active, tx.State);
        Assert.NotEqual(default, tx.Id);
        Assert.Equal(tx, session.CurrentTransaction);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - BeginTransaction: Should throw when transaction already active")]
    public async Task BeginTransaction_ShouldThrowWhenTransactionAlreadyActive()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        await session.BeginTransactionAsync();

        await Assert.ThrowsAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - CommitTransaction: Should transition to committed state")]
    public async Task CommitTransaction_ShouldTransitionToCommittedState()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var tx = await session.BeginTransactionAsync();
        await tx.CommitAsync();

        Assert.Equal(TransactionState.Committed, tx.State);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - RollbackTransaction: Should transition to rolledback state")]
    public async Task RollbackTransaction_ShouldTransitionToRolledBackState()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var tx = await session.BeginTransactionAsync();
        await tx.RollbackAsync();

        Assert.Equal(TransactionState.RolledBack, tx.State);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - DisposeTransaction: Should auto-rollback if active")]
    public async Task DisposeTransaction_ShouldAutoRollbackIfActive()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var tx = await session.BeginTransactionAsync();
        await tx.DisposeAsync();

        Assert.Equal(TransactionState.RolledBack, tx.State);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - DisposeSession: Should auto-rollback active transaction")]
    public async Task DisposeSession_ShouldAutoRollbackActiveTransaction()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");

        IDatabaseTransaction tx;
        {
            var session = await db.CreateSessionAsync();
            tx = await session.BeginTransactionAsync();
            await session.DisposeAsync();
        }

        Assert.Equal(TransactionState.RolledBack, tx.State);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - CommitTransaction: Should throw when not active")]
    public async Task CommitTransaction_ShouldThrowWhenNotActive()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var tx = await session.BeginTransactionAsync();
        await tx.CommitAsync();

        await Assert.ThrowsAsync<DatabaseException>(async () => await tx.CommitAsync());
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - RollbackTransaction: Should throw when not active")]
    public async Task RollbackTransaction_ShouldThrowWhenNotActive()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var tx = await session.BeginTransactionAsync();
        await tx.RollbackAsync();

        await Assert.ThrowsAsync<DatabaseException>(async () => await tx.RollbackAsync());
    }

    // ── DML Execution ──────────────────────────────────────────────────

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - ExecuteInsert: Should return success with affected count 1")]
    public async Task ExecuteInsert_ShouldReturnSuccessWithAffectedCount1()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var row = MakeRow(1, "Alice");
        var request = CreateInsertRequest(row);

        var result = await session.ExecuteAsync(request);

        Assert.Equal(QueryResultStatus.Success, result.Status);
        Assert.Equal(1, result.AffectedCount);
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - ExecuteInsert: Auto-commit should persist data")]
    public async Task ExecuteInsert_AutoCommit_ShouldPersistData()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        // Insert via auto-commit
        var row = MakeRow(1, "Alice");
        await session.ExecuteAsync(CreateInsertRequest(row));

        // Verify via SELECT
        var selectResult = await session.ExecuteAsync(CreateSelectRequest());
        Assert.True(selectResult is QueryResultSet);

        var resultSet = (QueryResultSet)selectResult;
        await using (resultSet)
        {
            var rows = new List<byte[]>();
            await foreach (var r in resultSet.GetRowsAsync())
            {
                rows.Add(r.GetBytes(0).ToArray());
            }

            Assert.Single(rows);
            Assert.Equal(row, rows[0]);
        }
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - ExecuteInsert: Explicit transaction commit should persist data")]
    public async Task ExecuteInsert_ExplicitTransactionCommit_ShouldPersistData()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var tx = await session.BeginTransactionAsync();

        var row1 = MakeRow(1, "Alice");
        var row2 = MakeRow(2, "Bob");
        await session.ExecuteAsync(CreateInsertRequest(row1));
        await session.ExecuteAsync(CreateInsertRequest(row2));

        await tx.CommitAsync();

        // Verify both rows are visible
        var selectResult = await session.ExecuteAsync(CreateSelectRequest());
        var resultSet = (QueryResultSet)selectResult;
        await using (resultSet)
        {
            var rows = new List<byte[]>();
            await foreach (var r in resultSet.GetRowsAsync())
            {
                rows.Add(r.GetBytes(0).ToArray());
            }

            Assert.Equal(2, rows.Count);
            Assert.Equal(row1, rows[0]);
            Assert.Equal(row2, rows[1]);
        }
    }

    // ── SELECT Execution ───────────────────────────────────────────────

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - ExecuteSelect: Should return result set")]
    public async Task ExecuteSelect_ShouldReturnResultSet()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var result = await session.ExecuteAsync(CreateSelectRequest());

        Assert.True(result is QueryResultSet);
        Assert.Equal(QueryResultStatus.Success, result.Status);
        Assert.Equal(-1, result.AffectedCount);

        var resultSet = (QueryResultSet)result;
        await using (resultSet)
        {
            Assert.Single(resultSet.Columns);
            Assert.Equal("data", resultSet.Columns[0].Name);
        }
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - ExecuteSelect: Empty table should return no rows")]
    public async Task ExecuteSelect_EmptyTable_ShouldReturnNoRows()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var result = await session.ExecuteAsync(CreateSelectRequest());
        var resultSet = (QueryResultSet)result;
        await using (resultSet)
        {
            var count = 0;
            await foreach (var _ in resultSet.GetRowsAsync())
            {
                count++;
            }

            Assert.Equal(0, count);
        }
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - ExecuteSelect: Multiple inserts should return all rows")]
    public async Task ExecuteSelect_MultipleInserts_ShouldReturnAllRows()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        await session.ExecuteAsync(CreateInsertRequest(MakeRow(1, "Alice")));
        await session.ExecuteAsync(CreateInsertRequest(MakeRow(2, "Bob")));
        await session.ExecuteAsync(CreateInsertRequest(MakeRow(3, "Charlie")));

        var result = await session.ExecuteAsync(CreateSelectRequest());
        var resultSet = (QueryResultSet)result;
        await using (resultSet)
        {
            var ids = new List<int>();
            await foreach (var row in resultSet.GetRowsAsync())
            {
                var bytes = row.GetBytes(0);
                ids.Add(BitConverter.ToInt32(bytes.Span));
            }

            Assert.Equal(3, ids.Count);
            Assert.Equal(1, ids[0]);
            Assert.Equal(2, ids[1]);
            Assert.Equal(3, ids[2]);
        }
    }

    // ── Engine Strategy ────────────────────────────────────────────────

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - InMemoryEngine: Should create and use database without RootPath")]
    public async Task InMemoryEngine_ShouldCreateAndUseDatabaseWithoutRootPath()
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "in-memory-test"
        });
        await engine.StartAsync();

        await using (engine)
        {
            var db = await engine.CreateDatabaseAsync("test-db");
            await using var session = await db.CreateSessionAsync();

            var row = MakeRow(42, "InMemory");
            await session.ExecuteAsync(CreateInsertRequest(row));

            var result = await session.ExecuteAsync(CreateSelectRequest());
            var resultSet = (QueryResultSet)result;
            await using (resultSet)
            {
                var count = 0;
                await foreach (var _ in resultSet.GetRowsAsync())
                {
                    count++;
                }

                Assert.Equal(1, count);
            }
        }
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - CreateDatabase: Should throw for duplicate name")]
    public async Task CreateDatabase_ShouldThrowForDuplicateName()
    {
        await using var engine = await CreateEngine();
        await engine.CreateDatabaseAsync("test-db");

        await Assert.ThrowsAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("test-db"));
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - DropDatabase: Should remove database")]
    public async Task DropDatabase_ShouldRemoveDatabase()
    {
        await using var engine = await CreateEngine();
        await engine.CreateDatabaseAsync("test-db");

        await engine.DropDatabaseAsync("test-db");

        Assert.False(engine.TryGetDatabase("test-db", out _));
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - TryGetDatabase: Should find existing database")]
    public async Task TryGetDatabase_ShouldFindExistingDatabase()
    {
        await using var engine = await CreateEngine();
        await engine.CreateDatabaseAsync("test-db");

        var found = engine.TryGetDatabase("test-db", out var db);

        Assert.True(found);
        Assert.NotNull(db);
    }

    // ── QueryRow Typed Accessors ───────────────────────────────────────

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - QueryRow: GetInt32 should decode first 4 bytes")]
    public async Task QueryRow_GetInt32_ShouldDecodeFirst4Bytes()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var row = MakeRow(42, "Test");
        await session.ExecuteAsync(CreateInsertRequest(row));

        var result = await session.ExecuteAsync(CreateSelectRequest());
        var resultSet = (QueryResultSet)result;
        await using (resultSet)
        {
            await foreach (var r in resultSet.GetRowsAsync())
            {
                Assert.Equal(42, r.GetInt32(0));
                Assert.Equal(1, r.FieldCount);
                Assert.False(r.IsNull(0));
            }
        }
    }

    // ── Error Handling ─────────────────────────────────────────────────

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - ExecuteInsert: Should throw without parameters")]
    public async Task ExecuteInsert_ShouldThrowWithoutParameters()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var expression = new SqlQueryExpression(SqlQueryCommandType.Insert, "INSERT INTO test", null);
        var statement = new SqlQueryStatement(expression);
        var request = new SqlQueryRequest(statement); // No parameters

        await Assert.ThrowsAsync<DatabaseException>(async () => await session.ExecuteAsync(request));
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - Session: Should throw when executing on closed session")]
    public async Task Session_ShouldThrowWhenExecutingOnClosedSession()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await db.CreateSessionAsync();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<DatabaseException>(async () => await session.ExecuteAsync(CreateSelectRequest()));
    }

    [Fact]
    [DisplayName("Cohesion Test [SqlEngine] - Session: Should throw BeginTransaction on closed session")]
    public async Task Session_ShouldThrowBeginTransactionOnClosedSession()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await db.CreateSessionAsync();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
    }
}
