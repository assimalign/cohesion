using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Lifecycle tests for the root-contract engine start/stop seam (#902): state
/// transitions, idempotent start, stop-then-start, and the durable-flush-on-stop
/// guarantee.
/// </summary>
public sealed class SqlEngineLifecycleTests : IDisposable
{
    private readonly string _rootPath;

    public SqlEngineLifecycleTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-lifecycle", Guid.NewGuid().ToString("N"));
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

    private SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "lifecycle", RootPath = _rootPath });

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: Start transitions Idle to Running")]
    public async Task StartAsync_FromIdle_ShouldTransitionToRunning()
    {
        // Arrange
        await using var engine = CreateEngine();
        engine.State.ShouldBe(EngineState.Idle);

        // Act
        await engine.StartAsync(CancellationToken.None);

        // Assert
        engine.State.ShouldBe(EngineState.Running);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: Starting a running engine is a no-op")]
    public async Task StartAsync_WhenAlreadyRunning_ShouldBeNoOp()
    {
        // Arrange
        await using var engine = CreateEngine();
        await engine.StartAsync();
        var database = await engine.CreateDatabaseAsync("double-start");

        // Act: a second start must not reset the engine or drop open databases.
        await engine.StartAsync();

        // Assert
        engine.State.ShouldBe(EngineState.Running);
        engine.TryGetDatabase("double-start", out var found).ShouldBeTrue();
        found.ShouldBeSameAs(database);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: Stop transitions Running to Stopped and closes databases")]
    public async Task StopAsync_WhenRunning_ShouldTransitionToStoppedAndCloseDatabases()
    {
        // Arrange
        await using var engine = CreateEngine();
        await engine.StartAsync();
        await engine.CreateDatabaseAsync("stopping-db");

        // Act
        await engine.StopAsync(CancellationToken.None);

        // Assert
        engine.State.ShouldBe(EngineState.Stopped);
        engine.TryGetDatabase("stopping-db", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: Stopping a never-started engine is a no-op")]
    public async Task StopAsync_WhenIdle_ShouldBeNoOp()
    {
        // Arrange
        await using var engine = CreateEngine();

        // Act
        await engine.StopAsync();

        // Assert
        engine.State.ShouldBe(EngineState.Idle);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: A stopped engine starts again and reopens its databases")]
    public async Task StartAsync_AfterStop_ShouldServeExistingDatabases()
    {
        // Arrange: create + populate, then stop.
        await using var engine = CreateEngine();
        await engine.StartAsync();

        var database = await engine.CreateDatabaseAsync("restartable");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
            await session.ExecuteAsync("INSERT INTO t (id) VALUES (7)");
        }

        await engine.StopAsync();

        // Act: start the same engine instance again and reopen the database.
        await engine.StartAsync();
        var reopened = await engine.OpenDatabaseAsync("restartable");

        // Assert
        engine.State.ShouldBe(EngineState.Running);
        await using var verify = await reopened.CreateSessionAsync();
        var result = await verify.ExecuteAsync("SELECT id FROM t");
        var rows = result.ShouldBeAssignableTo<Assimalign.Cohesion.Database.Execution.QueryResultSet>();

        int count = 0;
        await foreach (var _ in rows!.GetRowsAsync())
        {
            count++;
        }

        count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: Stop flushes committed work durably before completing")]
    public async Task StopAsync_AfterCommit_ShouldFlushDurably()
    {
        // Arrange: commit work, stop the engine (not dispose), and read the files a
        // second engine sees — the stop must have made the committed rows durable.
        var engine = CreateEngine();
        await engine.StartAsync();

        var database = await engine.CreateDatabaseAsync("durable-stop");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
            var transaction = await session.BeginTransactionAsync();
            await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");
            await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)");
            await transaction.CommitAsync();
        }

        // Act
        await engine.StopAsync();

        // Assert: a fresh engine over the same root recovers both rows.
        await using var reopenedEngine = CreateEngine();
        await reopenedEngine.StartAsync();
        var reopened = await reopenedEngine.OpenDatabaseAsync("durable-stop");
        await using var verify = await reopened.CreateSessionAsync();

        var result = await verify.ExecuteAsync("SELECT id FROM t");
        var rows = result.ShouldBeAssignableTo<Assimalign.Cohesion.Database.Execution.QueryResultSet>();

        int count = 0;
        await foreach (var _ in rows!.GetRowsAsync())
        {
            count++;
        }

        count.ShouldBe(2);
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: Database operations before start are rejected")]
    public async Task CreateDatabaseAsync_BeforeStart_ShouldThrow()
    {
        // Arrange
        await using var engine = CreateEngine();

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("too-early"));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: Database operations after stop are rejected")]
    public async Task CreateDatabaseAsync_AfterStop_ShouldThrow()
    {
        // Arrange
        await using var engine = CreateEngine();
        await engine.StartAsync();
        await engine.StopAsync();

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("too-late"));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lifecycle: Lifecycle calls on a disposed engine are rejected")]
    public async Task StartAsync_AfterDispose_ShouldThrowObjectDisposed()
    {
        // Arrange
        var engine = CreateEngine();
        await engine.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.StartAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.StopAsync());
    }
}
