using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Session-level MVCC visibility tests (#908): rows carry writer/deleter version
/// stamps in the shared record space and every scan filters through the
/// statement's snapshot — committed-only visibility under Snapshot isolation,
/// per-statement refresh under ReadCommitted, tombstoned deletes, version-chain
/// updates, and stamp correctness across restart and crash recovery.
/// </summary>
public sealed class SqlMvccVisibilityTests
{
    private static async Task<(IDatabase Database, IDatabaseSession Session)> CreateDatabaseAsync(SqlDatabaseEngine engine, string name)
    {
        var database = await engine.CreateDatabaseAsync(name);
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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: An uncommitted writer is invisible to other sessions (dirty read closed)")]
    public async Task Scan_UncommittedWriter_ShouldBeInvisibleToOtherSessions()
    {
        // Arrange: two sessions over one database.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-dirty" });
        var (database, writerSession) = await CreateDatabaseAsync(engine, "dirty-db");
        await using var _ = writerSession;
        await using var readerSession = await database.CreateSessionAsync();

        await writerSession.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

        // Act: the writer inserts inside an open transaction.
        var transaction = await writerSession.BeginTransactionAsync();
        await writerSession.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Assert: the reader does not observe the in-flight row (the dirty-read
        // window the page-grain engine had is closed); after commit it does.
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(0);

        await transaction.CommitAsync();
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: Snapshot isolation holds the begin-time view for the whole transaction")]
    public async Task Scan_SnapshotIsolation_ShouldHoldBeginTimeView()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-snapshot" });
        var (database, readerSession) = await CreateDatabaseAsync(engine, "snapshot-db");
        await using var _ = readerSession;
        await using var writerSession = await database.CreateSessionAsync();

        await readerSession.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await readerSession.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Act: the reader begins a Snapshot transaction, then the writer commits.
        var transaction = await readerSession.BeginTransactionAsync(IsolationLevel.Snapshot);
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(1);

        await writerSession.ExecuteAsync("INSERT INTO t (id) VALUES (2)");

        // Assert: still the begin-time view inside the transaction; the commit
        // becomes visible only once the reader's transaction ends.
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(1);
        await transaction.RollbackAsync();
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: ReadCommitted refreshes visibility per statement")]
    public async Task Scan_ReadCommitted_ShouldRefreshPerStatement()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-rc" });
        var (database, readerSession) = await CreateDatabaseAsync(engine, "rc-db");
        await using var _ = readerSession;
        await using var writerSession = await database.CreateSessionAsync();

        await readerSession.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");

        // Act: the reader's ReadCommitted transaction spans a concurrent commit.
        var transaction = await readerSession.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(0);

        await writerSession.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Assert: the next statement re-captures its snapshot and sees the commit.
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(1);
        await transaction.CommitAsync();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: A delete tombstones — older snapshots keep the row, newer ones lose it")]
    public async Task Delete_Tombstone_ShouldRespectSnapshots()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-delete" });
        var (database, readerSession) = await CreateDatabaseAsync(engine, "delete-db");
        await using var _ = readerSession;
        await using var writerSession = await database.CreateSessionAsync();

        await readerSession.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await readerSession.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Act: the reader pins a snapshot, then the writer deletes and commits.
        var transaction = await readerSession.BeginTransactionAsync(IsolationLevel.Snapshot);
        await writerSession.ExecuteAsync("DELETE FROM t WHERE id = 1");

        // Assert: the pinned snapshot still sees the row (the tombstone's deleter
        // is not admitted); a fresh statement reads absence.
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(1);
        await transaction.RollbackAsync();
        (await Rows(readerSession, "SELECT id FROM t")).Count.ShouldBe(0);
        (await Rows(writerSession, "SELECT id FROM t")).Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: An update writes a version chain — one version visible per snapshot")]
    public async Task Update_VersionChain_ShouldExposeExactlyOneVersionPerSnapshot()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-update" });
        var (database, readerSession) = await CreateDatabaseAsync(engine, "update-db");
        await using var _ = readerSession;
        await using var writerSession = await database.CreateSessionAsync();

        await readerSession.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await readerSession.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");

        // Act: the reader pins a snapshot, then the writer updates and commits.
        var transaction = await readerSession.BeginTransactionAsync(IsolationLevel.Snapshot);
        await writerSession.ExecuteAsync("UPDATE t SET val = 20 WHERE id = 1");

        // Assert: the pinned snapshot sees exactly the old version; a fresh one
        // sees exactly the new version — never both, never neither.
        var pinned = await Rows(readerSession, "SELECT val FROM t WHERE id = 1");
        pinned.Count.ShouldBe(1);
        pinned[0][0].ShouldBe(10L);

        await transaction.RollbackAsync();

        var fresh = await Rows(readerSession, "SELECT val FROM t WHERE id = 1");
        fresh.Count.ShouldBe(1);
        fresh[0][0].ShouldBe(20L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: Own writes are always visible, once")]
    public async Task Scan_OwnWrites_ShouldBeVisibleExactlyOnce()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-own" });
        var (database, session) = await CreateDatabaseAsync(engine, "own-db");
        await using var _ = session;

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");

        // Act + Assert: inside one transaction — insert, read own row, update it
        // twice (each update chains a version), read exactly the newest.
        var transaction = await session.BeginTransactionAsync();

        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 1)");
        (await Rows(session, "SELECT val FROM t"))[0][0].ShouldBe(1L);

        await session.ExecuteAsync("UPDATE t SET val = 2 WHERE id = 1");
        await session.ExecuteAsync("UPDATE t SET val = 3 WHERE id = 1");

        var rows = await Rows(session, "SELECT val FROM t");
        rows.Count.ShouldBe(1);
        rows[0][0].ShouldBe(3L);

        await transaction.CommitAsync();
        (await Rows(session, "SELECT val FROM t"))[0][0].ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: Rolled-back stamps revert — updates and deletes leave the committed state")]
    public async Task Rollback_StampedMutations_ShouldRevertToCommittedState()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-revert" });
        var (database, session) = await CreateDatabaseAsync(engine, "revert-db");
        await using var _ = session;

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");

        // Act: update one row, delete the other, insert a third — then roll back.
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
        await session.ExecuteAsync("DELETE FROM t WHERE id = 2");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (3, 30)");
        await transaction.RollbackAsync();

        // Assert: the committed state is exactly what it was.
        var rows = await Rows(session, "SELECT id, val FROM t ORDER BY id");
        rows.Count.ShouldBe(2);
        rows[0][1].ShouldBe(10L);
        rows[1][1].ShouldBe(20L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: Version stamps survive a clean restart")]
    public async Task Restart_StampedRows_ShouldStayVisibleWithVersionsIntact()
    {
        // Arrange: a strategy whose durable images can travel to a second engine.
        var strategy = new CrashCaptureSqlStorageStrategy();

        await using (var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-restart", StorageStrategy = strategy }))
        {
            var (_, session) = await CreateDatabaseAsync(engine, "restart-db");
            await using var _ = session;

            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
            await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10)");
            await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
            await session.ExecuteAsync("UPDATE t SET val = 11 WHERE id = 1");
            await session.ExecuteAsync("DELETE FROM t WHERE id = 2");
        }

        // Act: reopen from the durable images (clean close flushed everything).
        var reopened = strategy.CaptureDurableImages();
        await using var restarted = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-restart-2", StorageStrategy = reopened });
        var database = await restarted.OpenDatabaseAsync("restart-db");
        await using var restartedSession = await database.CreateSessionAsync();

        // Assert: the committed final state — updated row visible, deleted row
        // absent, and exactly one visible version of the survivor.
        var rows = await Rows(restartedSession, "SELECT id, val FROM t");
        rows.Count.ShouldBe(1);
        rows[0][0].ShouldBe(1L);
        rows[0][1].ShouldBe(11L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: A crash never resurrects an uncommitted writer's rows")]
    public async Task CrashRecovery_UncommittedWriter_ShouldNeverResurrect()
    {
        // Arrange: commit one row, leave a second uncommitted, then "crash" by
        // capturing the durable images without disposing the engine.
        var strategy = new CrashCaptureSqlStorageStrategy();
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-crash", StorageStrategy = strategy });
        var (_, session) = await CreateDatabaseAsync(engine, "crash-db");

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)");

        // Act: crash — the durable images are what a dead process leaves behind.
        var crashImages = strategy.CaptureDurableImages();

        await using var recovered = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-crash-2", StorageStrategy = crashImages });
        var database = await recovered.OpenDatabaseAsync("crash-db");
        await using var recoveredSession = await database.CreateSessionAsync();

        // Assert: the committed row is there; the uncommitted one is not — its
        // stamps reverted physically with the page images and its sequence
        // classified aborted by recovery analysis.
        var rows = await Rows(recoveredSession, "SELECT id FROM t");
        rows.Count.ShouldBe(1);
        rows[0][0].ShouldBe(1L);

        // The crashed engine is still holding the live streams; shut it down last.
        await transaction.RollbackAsync();
        await session.DisposeAsync();
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - MVCC visibility: ADD COLUMN null-tail decode and DROP COLUMN rewrite hold on stamped records")]
    public async Task SchemaEvolution_OnStampedRecords_ShouldKeepWorking()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "vis-ddl" });
        var (database, session) = await CreateDatabaseAsync(engine, "ddl-db");
        await using var _ = session;

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, tag VARCHAR(20))");
        await session.ExecuteAsync("INSERT INTO t (id, tag) VALUES (1, 'a')");

        // Act 1: ADD COLUMN — the old stamped row decodes with a null tail.
        await session.ExecuteAsync("ALTER TABLE t ADD extra INT");
        var afterAdd = await Rows(session, "SELECT id, tag, extra FROM t");
        afterAdd.Count.ShouldBe(1);
        afterAdd[0][2].ShouldBeNull();

        // Act 2: create version chains, then DROP COLUMN — the rewrite walks
        // every version (visible or tombstoned) and preserves stamps.
        await session.ExecuteAsync("INSERT INTO t (id, tag, extra) VALUES (2, 'b', 5)");
        await session.ExecuteAsync("UPDATE t SET extra = 7 WHERE id = 2");
        await session.ExecuteAsync("ALTER TABLE t DROP COLUMN tag");

        // Assert: the new layout reads correctly and visibility is unchanged —
        // one version per row.
        var rows = await Rows(session, "SELECT id, extra FROM t ORDER BY id");
        rows.Count.ShouldBe(2);
        rows[0][0].ShouldBe(1L);
        rows[0][1].ShouldBeNull();
        rows[1][0].ShouldBe(2L);
        rows[1][1].ShouldBe(7L);
    }
}
