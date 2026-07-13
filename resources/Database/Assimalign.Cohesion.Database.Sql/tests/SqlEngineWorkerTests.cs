using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Tests for the engine-owned background workers: they spawn with the engine
/// (create → use → dispose; no claim handshake, no external scheduler), the
/// checkpointer truncates both file sets while foreground work runs, grouped
/// commits ride the write-ahead flush worker, and everything quiesces on dispose —
/// identical behavior embedded or hosted (R10), because nothing outside the engine
/// participates.
/// </summary>
public sealed class SqlEngineWorkerTests : IDisposable
{
    private readonly string _rootPath;

    public SqlEngineWorkerTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-workers", Guid.NewGuid().ToString("N"));
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

    private string DataJournalPath(string database)
        => Path.Combine(_rootPath, database, database + ".log");

    private string CatalogJournalPath(string database)
        => Path.Combine(_rootPath, database + ".catalog", database + ".catalog.log");

    private static long GetFileLength(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Length;
    }

    private static async Task<int> CountRowsAsync(IDatabaseSession session)
    {
        var result = await session.ExecuteAsync("SELECT id FROM t");
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();

        int count = 0;
        await foreach (var _ in resultSet!.GetRowsAsync())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Polls until both journals of the database are truncated (a checkpoint pass
    /// landed) or the deadline lapses.
    /// </summary>
    private async Task WaitForCheckpointTruncationAsync(string database, int timeoutMilliseconds = 15_000)
    {
        string dataJournal = DataJournalPath(database);
        string catalogJournal = CatalogJournalPath(database);
        long deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (Environment.TickCount64 < deadline
            && (GetFileLength(dataJournal) >= 512 || GetFileLength(catalogJournal) >= 512))
        {
            await Task.Delay(50);
        }

        GetFileLength(dataJournal).ShouldBeLessThan(512);
        GetFileLength(catalogJournal).ShouldBeLessThan(512);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: The engine exposes the five-worker inventory from creation")]
    public async Task Workers_OnCreation_ShouldExposeTheFullInventory()
    {
        // Arrange / Act
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "inventory" });

        // Assert: one worker per kind, observable (name/kind/cadence) from creation.
        engine.Workers.Count.ShouldBe(5);
        engine.Workers.Select(worker => worker.Kind).Distinct().Count().ShouldBe(5);

        foreach (var worker in engine.Workers)
        {
            worker.Name.ShouldStartWith("inventory/");
            worker.Interval.ShouldBeGreaterThan(TimeSpan.Zero);
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Checkpoint worker: The engine-owned checkpointer truncates both journals and keeps data recoverable")]
    public async Task CheckpointWorker_SelfScheduled_ShouldTruncateBothJournalsAndPreserveData()
    {
        // Arrange: a fast checkpoint cadence so the engine's own checkpoint loop —
        // spawned at creation, no host anywhere — lands passes during the test.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "ckpt",
            RootPath = _rootPath,
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
        });

        var database = await engine.CreateDatabaseAsync("wal");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

            for (int i = 0; i < 20; i++)
            {
                await session.ExecuteAsync($"INSERT INTO t (id) VALUES ({i})");
            }
        }

        // Act / Assert: within a bounded wait the checkpointer truncates both
        // journals (each fattened by full-page-image commits).
        await WaitForCheckpointTruncationAsync("wal");

        // And the data survives disposal + a fresh engine over the same files.
        await engine.DisposeAsync();

        await using var reopened = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ckpt", RootPath = _rootPath });
        var reopenedDatabase = await reopened.OpenDatabaseAsync("wal");
        await using var verify = await reopenedDatabase.CreateSessionAsync();

        (await CountRowsAsync(verify)).ShouldBe(20);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Checkpoint worker: An active transaction never faults the engine — busy storage is skipped and retried")]
    public async Task CheckpointWorker_WithActiveTransaction_ShouldSkipBusyStorageAndRetry()
    {
        // Arrange: fast cadence, so passes land while the transaction is open.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "busy",
            RootPath = _rootPath,
            CheckpointInterval = TimeSpan.FromMilliseconds(50),
        });

        var database = await engine.CreateDatabaseAsync("busy-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Act: hold the transaction across several checkpoint intervals — passes
        // must skip the busy storage without faulting the engine.
        await Task.Delay(300);
        engine.State.ShouldBe(EngineState.Running);

        await transaction.CommitAsync();

        // Assert: with the transaction resolved, a later pass truncates.
        long deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline && GetFileLength(DataJournalPath("busy-db")) >= 512)
        {
            await Task.Delay(50);
        }

        GetFileLength(DataJournalPath("busy-db")).ShouldBeLessThan(512);
        engine.State.ShouldBe(EngineState.Running);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Flush worker: Grouped commits complete promptly via the engine's flusher")]
    public async Task GroupedCommits_WithEngineFlusher_ShouldCompletePromptly()
    {
        // Arrange: a deliberately long self-help window, so prompt completion is
        // attributable to the engine's own flush worker alone.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "grouped",
            Durability = StorageCommitDurability.Grouped,
            GroupCommitWindow = TimeSpan.FromSeconds(10),
        });

        var database = await engine.CreateDatabaseAsync("grouped-db");
        await using var session = await database.CreateSessionAsync();

        // Act: several grouped commits (DDL self-commits + auto-commit inserts).
        long start = Stopwatch.GetTimestamp();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)");
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        // Assert: far below one 10-second self-help window (several would have been
        // needed without a flusher), and the rows are visible.
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await CountRowsAsync(session)).ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Embedded parity: A hostless engine checkpoints and flushes with no composition at all (R10)")]
    public async Task EmbeddedEngine_WithNoHost_ShouldCheckpointAndFlushViaEngineOwnedWorkers()
    {
        // Arrange: no host, no application, no server — the engine's own loops are
        // the only scheduler that exists. Grouped durability + a fast checkpoint
        // cadence make their effects observable from the outside.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "embedded",
            RootPath = _rootPath,
            Durability = StorageCommitDurability.Grouped,
            GroupCommitWindow = TimeSpan.FromMilliseconds(5),
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
            PageWriteBackInterval = TimeSpan.FromMilliseconds(50),
        });

        var database = await engine.CreateDatabaseAsync("embedded-db");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

            for (int i = 0; i < 10; i++)
            {
                await session.ExecuteAsync($"INSERT INTO t (id) VALUES ({i})");
            }
        }

        // Act / Assert: within a bounded wait the engine-owned checkpointer
        // truncates both journals — background durability with no host at all.
        await WaitForCheckpointTruncationAsync("embedded-db");

        // Dispose (quiesces the workers), then prove the data recovered by a fresh
        // engine.
        await engine.DisposeAsync();

        await using var reopened = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "embedded", RootPath = _rootPath });
        var reopenedDatabase = await reopened.OpenDatabaseAsync("embedded-db");
        await using var verify = await reopenedDatabase.CreateSessionAsync();

        (await CountRowsAsync(verify)).ShouldBe(10);
    }
}
