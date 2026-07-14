using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>
/// Version-purge worker tests (#910): the worker's pass physically reclaims
/// versions below the safe prune bound through the version store, a
/// long-running snapshot pins its view (prune never removes a version a live
/// snapshot can still read), aborted writers leave the store at baseline, and
/// the engine-owned timer drives the same behavior with no host anywhere (R10).
/// </summary>
public sealed class SqlVersionPurgeTests
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

    private static int CountPhysicalRecords(SqlDatabaseInstance database)
    {
        int count = 0;
        using var iterator = database.DataStorage.GetUnitIterator();

        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Version purge: A pass reclaims superseded and deleted versions below the bound")]
    public async Task RunVersionPurgePass_DeadVersions_ShouldReclaimPhysically()
    {
        // Arrange: an update chains a version (old tombstoned + new) and a
        // delete tombstones another row — three physical records, two dead.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "purge-reclaim" });
        var (database, session) = await CreateSessionAsync(engine, "reclaim-db");
        await using var _ = session;

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
        await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
        await session.ExecuteAsync("DELETE FROM t WHERE id = 2");

        CountPhysicalRecords(database).ShouldBe(3);
        database.Coordinator.VersionStore.TrackedVersionCount.ShouldBe(2);

        // Act: one worker pass with no snapshots in flight.
        long reclaimed = database.Coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert: the superseded version and the deleted row are gone
        // physically; the visible state is unchanged; the store is at baseline.
        reclaimed.ShouldBe(2);
        CountPhysicalRecords(database).ShouldBe(1);
        database.Coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);

        var rows = await Rows(session, "SELECT id, val FROM t");
        rows.Count.ShouldBe(1);
        rows[0][1].ShouldBe(11L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Version purge: A long-running snapshot pins its view; reclamation happens after it closes")]
    public async Task RunVersionPurgePass_PinnedSnapshot_ShouldNotReclaimUntilClosed()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "purge-pinned" });
        var (database, readerSession) = await CreateSessionAsync(engine, "pinned-db");
        await using var _ = readerSession;
        await using var writerSession = await database.CreateSessionAsync();

        await readerSession.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await readerSession.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");

        // The long-running snapshot begins BEFORE the update commits.
        var pinned = await readerSession.BeginTransactionAsync();
        (await Rows(readerSession, "SELECT val FROM t"))[0][0].ShouldBe(10L);

        await writerSession.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
        CountPhysicalRecords(database).ShouldBe(2);

        // Act 1: a pass while the snapshot is live must not reclaim the old
        // version — the pinned reader can still see it.
        database.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        CountPhysicalRecords(database).ShouldBe(2);
        (await Rows(readerSession, "SELECT val FROM t"))[0][0].ShouldBe(10L);

        // Act 2: the snapshot closes; the next pass reclaims.
        await pinned.RollbackAsync();
        database.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(1);

        // Assert
        CountPhysicalRecords(database).ShouldBe(1);
        (await Rows(readerSession, "SELECT val FROM t"))[0][0].ShouldBe(11L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Version purge: Aborted writers leave the version store at baseline")]
    public async Task Rollback_AbortedWriter_ShouldReturnStoreToBaseline()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "purge-abort" });
        var (database, session) = await CreateSessionAsync(engine, "abort-db");
        await using var _ = session;

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        int baselineRecords = CountPhysicalRecords(database);
        int baselineTracked = database.Coordinator.VersionStore.TrackedVersionCount;

        // Act: a transaction writes broadly, then aborts. The unlink is the
        // store's PurgeWriterAsync (driven inline at rollback, before locks
        // release; the worker retries it only when the inline undo fails).
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
        await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
        await session.ExecuteAsync("DELETE FROM t WHERE id = 1");
        await transaction.RollbackAsync();

        // Assert: no active snapshots, and the store — and the record space —
        // are back at baseline; a worker pass finds nothing to do.
        database.Coordinator.VersionStore.TrackedVersionCount.ShouldBe(baselineTracked);
        database.Coordinator.VersionStore.PendingAbortedPurges.Count.ShouldBe(0);
        CountPhysicalRecords(database).ShouldBe(baselineRecords);
        database.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        (await Rows(session, "SELECT val FROM t"))[0][0].ShouldBe(10L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Version purge: Pre-restart tombstones are seeded at open and reclaimed")]
    public async Task Restart_PreRestartTombstones_ShouldBeSeededAndReclaimed()
    {
        // Arrange: dead versions created before a clean restart.
        var strategy = new CrashCaptureSqlStorageStrategy();

        await using (var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "purge-restart", StorageStrategy = strategy }))
        {
            var (_, session) = await CreateSessionAsync(engine, "restart-db");
            await using var _ = session;

            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
            await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
            await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
        }

        // Act: reopen (the open-time scan seeds the prunable set) and purge.
        var reopened = strategy.CaptureDurableImages();
        await using var restarted = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "purge-restart-2", StorageStrategy = reopened });
        var database = (SqlDatabaseInstance)await restarted.OpenDatabaseAsync("restart-db");
        await using var restartedSession = await database.CreateSessionAsync();

        CountPhysicalRecords(database).ShouldBe(2);
        long reclaimed = database.Coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        reclaimed.ShouldBe(1);
        CountPhysicalRecords(database).ShouldBe(1);
        (await Rows(restartedSession, "SELECT val FROM t"))[0][0].ShouldBe(11L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Version purge: The engine-owned timer drives reclamation with no host anywhere (R10)")]
    public async Task Worker_HostlessEngine_ShouldReclaimOnItsOwnTimer()
    {
        // Arrange: no host, no application, no server — the engine's own
        // maintenance loop is the only scheduler that exists.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "purge-hostless",
            MaintenanceInterval = TimeSpan.FromMilliseconds(25),
        });
        var (database, session) = await CreateSessionAsync(engine, "hostless-db");
        await using var _ = session;

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");

        // Act: wait (bounded) for the worker's own pass to reclaim the dead
        // version — no external scheduling of any kind.
        long deadline = Stopwatch.GetTimestamp();
        while (CountPhysicalRecords(database) > 1
            && Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        // Assert
        CountPhysicalRecords(database).ShouldBe(1);
        engine.State.ShouldBe(EngineState.Running);
        (await Rows(session, "SELECT val FROM t"))[0][0].ShouldBe(11L);
    }
}
