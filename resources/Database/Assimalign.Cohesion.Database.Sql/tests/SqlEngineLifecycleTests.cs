using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Creation/dispose semantics of the engine as a data machine: operational from
/// <c>Create</c> (no start ceremony), disposal quiesces the workers and durably
/// flushes and closes every open database, disposal is idempotent, and a disposed
/// engine rejects every operation.
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Creation: The engine is operational from Create — no start ceremony")]
    public async Task Create_ShouldReturnOperationalEngine()
    {
        // Arrange / Act
        await using var engine = CreateEngine();

        // Assert: running from creation, workers spawned, and immediately usable.
        engine.State.ShouldBe(EngineState.Running);
        engine.Workers.Count.ShouldBe(5);

        var database = await engine.CreateDatabaseAsync("immediate");
        engine.TryGetDatabase("immediate", out var found).ShouldBeTrue();
        found.ShouldBeSameAs(database);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Dispose: Disposal closes open databases and is terminal")]
    public async Task DisposeAsync_WithOpenDatabases_ShouldCloseThemAndRejectFurtherUse()
    {
        // Arrange
        var engine = CreateEngine();
        await engine.CreateDatabaseAsync("closing-db");

        // Act
        await engine.DisposeAsync();

        // Assert
        engine.State.ShouldBe(EngineState.Disposed);
        Should.Throw<ObjectDisposedException>(() => engine.TryGetDatabase("closing-db", out _));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.CreateDatabaseAsync("too-late"));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Dispose: Double dispose is safe on both dispose paths")]
    public async Task Dispose_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        var asyncDisposed = CreateEngine();
        var syncDisposed = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sync-dispose" });

        // Act / Assert: DisposeAsync twice, Dispose twice, and mixed — all no-throw.
        await asyncDisposed.DisposeAsync();
        await asyncDisposed.DisposeAsync();
        asyncDisposed.Dispose();

        syncDisposed.Dispose();
        syncDisposed.Dispose();
        await syncDisposed.DisposeAsync();

        asyncDisposed.State.ShouldBe(EngineState.Disposed);
        syncDisposed.State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Dispose: Disposal flushes committed work durably")]
    public async Task DisposeAsync_AfterCommit_ShouldFlushDurably()
    {
        // Arrange: commit work, then dispose — disposal absorbs the old stop
        // contract (quiesce workers → durable flush → close databases).
        var engine = CreateEngine();

        var database = await engine.CreateDatabaseAsync("durable-dispose");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
            var transaction = await session.BeginTransactionAsync();
            await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");
            await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)");
            await transaction.CommitAsync();
        }

        // Act
        await engine.DisposeAsync();

        // Assert: a fresh engine over the same root recovers both rows — and the
        // fact that it can open the files at all proves the disposed engine's
        // workers quiesced and its storages closed (file handles released).
        await using var reopenedEngine = CreateEngine();
        var reopened = await reopenedEngine.OpenDatabaseAsync("durable-dispose");
        await using var verify = await reopened.CreateSessionAsync();

        var result = await verify.ExecuteAsync("SELECT id FROM t");
        var rows = result.ShouldBeAssignableTo<Assimalign.Cohesion.Database.Execution.QueryResultSet>();

        int count = 0;
        await foreach (var _ in rows!.GetRowsAsync())
        {
            count++;
        }

        count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Restart: A fresh engine over the same root serves the previous engine's databases")]
    public async Task Create_OverExistingRoot_ShouldServeExistingDatabases()
    {
        // Arrange: an engine restart is now create-over-the-same-root — the data
        // machine itself is not restartable (dispose is terminal).
        await using (var engine = CreateEngine())
        {
            var database = await engine.CreateDatabaseAsync("restartable");
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
            await session.ExecuteAsync("INSERT INTO t (id) VALUES (7)");
        }

        // Act
        await using var reopenedEngine = CreateEngine();
        var reopened = await reopenedEngine.OpenDatabaseAsync("restartable");

        // Assert
        reopenedEngine.State.ShouldBe(EngineState.Running);
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
}
