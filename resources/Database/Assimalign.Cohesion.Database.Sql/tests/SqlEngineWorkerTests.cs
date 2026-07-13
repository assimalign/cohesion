using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Tests for the engine-owned background workers (#902): the checkpoint worker
/// truncates both file sets and preserves recoverability, grouped commits ride the
/// write-ahead flush worker (and self-help when nobody pumps it), and an embedded
/// engine — no host — gets identical worker behavior via self-scheduling (R10).
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: The engine exposes the five-worker inventory before start")]
    public async Task Workers_OnCreation_ShouldExposeTheFullInventory()
    {
        // Arrange / Act
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "inventory" });

        // Assert: one worker per kind, available for claiming before start.
        engine.Workers.Count.ShouldBe(5);
        engine.Workers.Select(worker => worker.Kind).Distinct().Count().ShouldBe(5);

        foreach (var worker in engine.Workers)
        {
            worker.Name.ShouldStartWith("inventory/");
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Checkpoint worker: One pass truncates both file sets' journals and keeps data recoverable")]
    public async Task CheckpointWorker_RunIteration_ShouldTruncateBothJournalsAndPreserveData()
    {
        // Arrange: claim the checkpoint worker (as a host slot would) so the test
        // drives its passes deterministically — no timers, no sleeps.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ckpt", RootPath = _rootPath });
        var checkpoint = engine.Workers.First(worker => worker.Kind == DatabaseEngineWorkerKind.Checkpoint);
        checkpoint.TryClaim().ShouldBeTrue();

        await engine.StartAsync();

        var database = await engine.CreateDatabaseAsync("wal");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

            for (int i = 0; i < 20; i++)
            {
                await session.ExecuteAsync($"INSERT INTO t (id) VALUES ({i})");
            }
        }

        // Every auto-committed insert journaled full page images: the journal is fat.
        GetFileLength(DataJournalPath("wal")).ShouldBeGreaterThan(8 * 1024);
        GetFileLength(CatalogJournalPath("wal")).ShouldBeGreaterThan(0);

        // Act: one checkpoint pass over every open storage (data + catalog sets).
        checkpoint.RunIteration(CancellationToken.None);

        // Assert: both journals truncated down to a single checkpoint record.
        GetFileLength(DataJournalPath("wal")).ShouldBeLessThan(512);
        GetFileLength(CatalogJournalPath("wal")).ShouldBeLessThan(512);

        // And the data survives a full engine restart over the same files.
        await engine.StopAsync();
        await engine.DisposeAsync();

        await using var reopened = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ckpt", RootPath = _rootPath });
        await reopened.StartAsync();
        var reopenedDatabase = await reopened.OpenDatabaseAsync("wal");
        await using var verify = await reopenedDatabase.CreateSessionAsync();

        (await CountRowsAsync(verify)).ShouldBe(20);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Checkpoint worker: A pass with an active transaction skips busy storage without failing")]
    public async Task CheckpointWorker_WithActiveTransaction_ShouldSkipBusyStorage()
    {
        // Arrange
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "busy", RootPath = _rootPath });
        var checkpoint = engine.Workers.First(worker => worker.Kind == DatabaseEngineWorkerKind.Checkpoint);
        checkpoint.TryClaim().ShouldBeTrue();

        await engine.StartAsync();
        await using var _ = engine;

        var database = await engine.CreateDatabaseAsync("busy-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Act / Assert: the pass must neither throw nor deadlock while a transaction
        // holds the data storage; it simply retries next tick.
        Should.NotThrow(() => checkpoint.RunIteration(CancellationToken.None));

        await transaction.CommitAsync();

        // With the transaction resolved, the next pass truncates.
        checkpoint.RunIteration(CancellationToken.None);
        GetFileLength(DataJournalPath("busy-db")).ShouldBeLessThan(512);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Flush worker: Grouped commits complete promptly via the self-scheduled flusher")]
    public async Task GroupedCommits_WithSelfScheduledFlusher_ShouldCompletePromptly()
    {
        // Arrange: a deliberately long self-help window, so prompt completion is
        // attributable to the engine's self-scheduled flush worker alone.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "grouped",
            Durability = StorageCommitDurability.Grouped,
            GroupCommitWindow = TimeSpan.FromSeconds(10),
        });

        await engine.StartAsync();
        await using var _ = engine;

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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Flush worker: A claimed-but-unpumped flusher never blocks commits (self-help)")]
    public async Task GroupedCommits_WithClaimedUnpumpedFlusher_ShouldSelfHelp()
    {
        // Arrange: a host claims the flush worker but (misconfigured) never pumps it.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "self-help",
            Durability = StorageCommitDurability.Grouped,
            GroupCommitWindow = TimeSpan.FromMilliseconds(50),
        });

        var flusher = engine.Workers.First(worker => worker.Kind == DatabaseEngineWorkerKind.WriteAheadFlush);
        flusher.TryClaim().ShouldBeTrue();

        await engine.StartAsync();
        await using var _ = engine;

        var database = await engine.CreateDatabaseAsync("self-help-db");
        await using var session = await database.CreateSessionAsync();

        // Act / Assert: commits complete via the bounded inline self-help flush —
        // durability is never hostage to a worker.
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");
        (await CountRowsAsync(session)).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Embedded parity: A hostless engine checkpoints and flushes identically (R10)")]
    public async Task EmbeddedEngine_WithNoHost_ShouldCheckpointAndFlushViaSelfScheduledWorkers()
    {
        // Arrange: nothing claims any worker — the engine self-schedules all five on
        // start. Grouped durability + a fast checkpoint cadence make their effects
        // observable from the outside.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "embedded",
            RootPath = _rootPath,
            Durability = StorageCommitDurability.Grouped,
            GroupCommitWindow = TimeSpan.FromMilliseconds(5),
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
            PageWriteBackInterval = TimeSpan.FromMilliseconds(50),
        });

        await engine.StartAsync();

        var database = await engine.CreateDatabaseAsync("embedded-db");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

            for (int i = 0; i < 10; i++)
            {
                await session.ExecuteAsync($"INSERT INTO t (id) VALUES ({i})");
            }
        }

        // Act / Assert: within a bounded wait the self-scheduled checkpointer
        // truncates both journals — background durability with no host at all.
        string dataJournal = DataJournalPath("embedded-db");
        string catalogJournal = CatalogJournalPath("embedded-db");
        long deadline = Environment.TickCount64 + 15_000;

        while (Environment.TickCount64 < deadline
            && (GetFileLength(dataJournal) >= 512 || GetFileLength(catalogJournal) >= 512))
        {
            await Task.Delay(50);
        }

        GetFileLength(dataJournal).ShouldBeLessThan(512);
        GetFileLength(catalogJournal).ShouldBeLessThan(512);

        // Clean stop, then prove the data recovered by a fresh engine.
        await engine.StopAsync();
        await engine.DisposeAsync();

        await using var reopened = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "embedded", RootPath = _rootPath });
        await reopened.StartAsync();
        var reopenedDatabase = await reopened.OpenDatabaseAsync("embedded-db");
        await using var verify = await reopenedDatabase.CreateSessionAsync();

        (await CountRowsAsync(verify)).ShouldBe(10);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Workers: Engine start claims unclaimed workers; stop releases them")]
    public async Task StartAsync_WithUnclaimedWorkers_ShouldClaimThemAndReleaseOnStop()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "claims" });

        // Act: start self-schedules (claims) every worker.
        await engine.StartAsync();

        foreach (var worker in engine.Workers)
        {
            worker.TryClaim().ShouldBeFalse();
        }

        // Stop releases the engine's claims so a later host (or restart) can claim.
        await engine.StopAsync();

        foreach (var worker in engine.Workers)
        {
            worker.TryClaim().ShouldBeTrue();
            worker.Release();
        }
    }
}
