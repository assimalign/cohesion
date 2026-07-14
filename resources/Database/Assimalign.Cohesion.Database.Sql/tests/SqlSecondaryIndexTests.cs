using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Secondary indexes end-to-end in the SQL engine (#912): CREATE/DROP INDEX DDL,
/// MVCC-correct maintenance on the write path (entries mirror row-version stamps),
/// unique enforcement through the key-lock discipline, logical rollback undoing
/// index stamps, crash-recovery scrubbing, and registration persistence across
/// restart including root-page drift.
/// </summary>
public sealed class SqlSecondaryIndexTests : IDisposable
{
    private readonly string _rootPath;

    public SqlSecondaryIndexTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-indexes", Guid.NewGuid().ToString("N"));
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

    private static ulong ObjectIdOf(IDatabase database, string table)
    {
        var instance = (SqlDatabaseInstance)database;
        instance.Catalog.TryGetTable("dbo", table, out var catalogTable).ShouldBeTrue();
        return catalogTable.ObjectId;
    }

    /// <summary>
    /// Materializes the entries a fresh snapshot sees in the named index.
    /// </summary>
    private static async Task<List<(byte[] Key, ulong EntryReference)>> VisibleEntriesAsync(IDatabase database, string table, string indexName)
    {
        var instance = (SqlDatabaseInstance)database;
        ulong objectId = ObjectIdOf(database, table);
        instance.IndexManager.TryGetIndex(objectId, indexName, out var index).ShouldBeTrue($"index '{indexName}' should be attached");

        await using var session = await database.CreateSessionAsync();
        var transaction = (SqlDatabaseTransaction)await session.BeginTransactionAsync();

        try
        {
            var entries = new List<(byte[] Key, ulong EntryReference)>();
            await using var cursor = index.OpenCursor(transaction.Context, IndexKeyRange.All);

            while (await cursor.MoveNextAsync())
            {
                entries.Add((cursor.CurrentKey.Encoded.ToArray(), cursor.CurrentEntryReference));
            }

            return entries;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static byte[] Int32Key(int value)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(value);
        return writer.ToArray();
    }

    // ── DDL + build ────────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: CREATE INDEX builds over existing rows and persists across restart")]
    public async Task CreateIndex_OverExistingRows_ShouldBuildAndPersist()
    {
        // Arrange: rows first, index second (the DDL-blocking build path).
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-build", RootPath = _rootPath });
        var database = await engine.CreateDatabaseAsync("build-db");

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, label VARCHAR(50))");
            await session.ExecuteAsync("INSERT INTO t (id, label) VALUES (1, 'a'), (2, 'b'), (3, 'c')");
            await session.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");
        }

        // Assert: catalog metadata + a live tree with one entry per row.
        var instance = (SqlDatabaseInstance)database;
        instance.Catalog.TryGetIndex(ObjectIdOf(database, "t"), "ix_t_id", out var metadata).ShouldBeTrue();
        metadata.ColumnNames.ShouldBe(new[] { "id" });
        (await VisibleEntriesAsync(database, "t", "ix_t_id")).Count.ShouldBe(3);

        await engine.DisposeAsync();

        // Act: restart — registrations re-attach the tree.
        var reopenedEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-build", RootPath = _rootPath });
        await using var _ = reopenedEngine;
        var reopened = await reopenedEngine.OpenDatabaseAsync("build-db");

        var entries = await VisibleEntriesAsync(reopened, "t", "ix_t_id");
        entries.Count.ShouldBe(3);
        entries[0].Key.ShouldBe(Int32Key(1));
        entries[2].Key.ShouldBe(Int32Key(3));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: CREATE UNIQUE INDEX over duplicate rows fails and leaves nothing behind")]
    public async Task CreateUniqueIndex_WithDuplicateRows_ShouldFailCompletely()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-dup" });
        var database = await engine.CreateDatabaseAsync("dup-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1), (1)");

        // Act
        var exception = await Should.ThrowAsync<DatabaseException>(
            async () => await session.ExecuteAsync("CREATE UNIQUE INDEX ix_t_id ON t (id)"));

        // Assert: precise error; neither the catalog nor the live directory keeps
        // any trace, and a non-unique index over the same data still works.
        exception.Message.ShouldContain("duplicate");
        var instance = (SqlDatabaseInstance)database;
        instance.Catalog.TryGetIndex(ObjectIdOf(database, "t"), "ix_t_id", out _).ShouldBeFalse();
        instance.IndexManager.TryGetIndex(ObjectIdOf(database, "t"), "ix_t_id", out _).ShouldBeFalse();

        await session.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");
        (await VisibleEntriesAsync(database, "t", "ix_t_id")).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: DROP INDEX removes the index now and after restart; DROP COLUMN on an indexed column is rejected")]
    public async Task DropIndex_ShouldRemoveIndex_AndIndexedColumnsAreGuarded()
    {
        // Arrange
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-drop", RootPath = _rootPath });
        var database = await engine.CreateDatabaseAsync("drop-db");

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, label VARCHAR(50))");
            await session.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");

            // The dependent-column guard.
            var guarded = await Should.ThrowAsync<DatabaseException>(
                async () => await session.ExecuteAsync("ALTER TABLE t DROP COLUMN id"));
            guarded.Message.ShouldContain("ix_t_id");

            // Act
            await session.ExecuteAsync("DROP INDEX ix_t_id ON t");

            // Assert: gone from both surfaces; maintenance no longer runs.
            var instance = (SqlDatabaseInstance)database;
            instance.Catalog.GetIndexes(ObjectIdOf(database, "t")).ShouldBeEmpty();
            instance.IndexManager.TryGetIndex(ObjectIdOf(database, "t"), "ix_t_id", out _).ShouldBeFalse();
            await session.ExecuteAsync("INSERT INTO t (id, label) VALUES (1, 'a')");

            // Dropping an unknown index honors IF EXISTS and rejects otherwise.
            await session.ExecuteAsync("DROP INDEX IF EXISTS ix_t_id ON t");
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("DROP INDEX ix_t_id ON t"));
        }

        await engine.DisposeAsync();

        // Restart: still gone.
        await using var reopenedEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-drop", RootPath = _rootPath });
        var reopened = await reopenedEngine.OpenDatabaseAsync("drop-db");
        ((SqlDatabaseInstance)reopened).Catalog.GetIndexes(ObjectIdOf(reopened, "t")).ShouldBeEmpty();
    }

    // ── Write-path maintenance ─────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: UPDATE moves the entry — old key tombstoned, new key visible")]
    public async Task Update_IndexedColumn_ShouldMoveEntry()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-update" });
        var database = await engine.CreateDatabaseAsync("update-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, label VARCHAR(50))");
        await session.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");
        await session.ExecuteAsync("INSERT INTO t (id, label) VALUES (1, 'row')");

        // Act
        await session.ExecuteAsync("UPDATE t SET id = 2 WHERE id = 1");

        // Assert: exactly one visible entry, keyed by the new value.
        var entries = await VisibleEntriesAsync(database, "t", "ix_t_id");
        entries.Count.ShouldBe(1);
        entries[0].Key.ShouldBe(Int32Key(2));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: DELETE tombstones the entry — old snapshots keep it, new ones lose it")]
    public async Task Delete_IndexedRow_ShouldTombstoneEntryUnderSnapshots()
    {
        // Arrange: a reader pins a snapshot before the delete commits.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-delete" });
        var database = await engine.CreateDatabaseAsync("delete-db");
        await using var writerSession = await database.CreateSessionAsync();
        await using var readerSession = await database.CreateSessionAsync();

        await writerSession.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await writerSession.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");
        await writerSession.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        var instance = (SqlDatabaseInstance)database;
        instance.IndexManager.TryGetIndex(ObjectIdOf(database, "t"), "ix_t_id", out var index).ShouldBeTrue();

        var pinned = (SqlDatabaseTransaction)await readerSession.BeginTransactionAsync(IsolationLevel.Snapshot);
        var pinnedSnapshot = pinned.Context.Snapshot;

        // Act
        await writerSession.ExecuteAsync("DELETE FROM t WHERE id = 1");

        // Assert: the pinned snapshot still sees the entry; a fresh one does not.
        await using (var pinnedCursor = index.OpenCursor(pinnedSnapshot, IndexKeyRange.All))
        {
            (await pinnedCursor.MoveNextAsync()).ShouldBeTrue();
        }

        await pinned.RollbackAsync();
        (await VisibleEntriesAsync(database, "t", "ix_t_id")).ShouldBeEmpty();
    }

    // ── Uniqueness ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: a UNIQUE violation fails the statement and keeps the session usable")]
    public async Task Insert_UniqueViolation_ShouldFailStatementOnly()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-unique" });
        var database = await engine.CreateDatabaseAsync("unique-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, email VARCHAR(100))");
        await session.ExecuteAsync("CREATE UNIQUE INDEX ix_t_email ON t (email)");
        await session.ExecuteAsync("INSERT INTO t (id, email) VALUES (1, 'a@x')");

        // Act
        var exception = await Should.ThrowAsync<DatabaseException>(
            async () => await session.ExecuteAsync("INSERT INTO t (id, email) VALUES (2, 'a@x')"));

        // Assert: the statement failed atomically (no half-inserted row), and the
        // session keeps working with a distinct key.
        exception.Message.ShouldContain("UNIQUE", Case.Sensitive);
        (await Rows(session, "SELECT id FROM t")).Count.ShouldBe(1);
        await session.ExecuteAsync("INSERT INTO t (id, email) VALUES (2, 'b@x')");
        (await Rows(session, "SELECT id FROM t")).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: concurrent same-key inserts serialize — the loser violates after commit, succeeds after rollback")]
    public async Task ConcurrentInserts_SameUniqueKey_ShouldSerializeThroughKeyLock()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-race" });
        var database = await engine.CreateDatabaseAsync("race-db");
        await using var first = await database.CreateSessionAsync();
        await using var second = await database.CreateSessionAsync();

        await first.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await first.ExecuteAsync("CREATE UNIQUE INDEX ix_t_id ON t (id)");

        // Act 1: the first writer holds the key uncommitted; the second blocks on
        // the key lock (deterministic — a lock wait, not a sleep), then loses
        // when the first commits.
        var transaction = await first.BeginTransactionAsync();
        await first.ExecuteAsync("INSERT INTO t (id) VALUES (7)");

        var contender = second.ExecuteAsync("INSERT INTO t (id) VALUES (7)").AsTask();
        contender.IsCompleted.ShouldBeFalse();

        await transaction.CommitAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await contender);

        // Act 2: same shape, but the first writer rolls back — its logical undo
        // erases the index entry before the key lock releases, so the waiting
        // insert now succeeds.
        var second7 = await first.BeginTransactionAsync();
        await first.ExecuteAsync("INSERT INTO t (id) VALUES (8)");

        var succeeds = second.ExecuteAsync("INSERT INTO t (id) VALUES (8)").AsTask();
        succeeds.IsCompleted.ShouldBeFalse();

        await second7.RollbackAsync();
        await succeeds;

        // Assert
        var rows = await Rows(first, "SELECT id FROM t");
        rows.Select(row => (int)row[0]!).OrderBy(id => id).ShouldBe(new[] { 7, 8 });
    }

    // ── Rollback and recovery ──────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: rollback erases inserted entries and restores tombstoned ones")]
    public async Task Rollback_ShouldUndoIndexStamps()
    {
        // Arrange: one committed row; a transaction then inserts one and deletes
        // the other.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-rollback" });
        var database = await engine.CreateDatabaseAsync("rollback-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        // Act
        var transaction = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)");
        await session.ExecuteAsync("DELETE FROM t WHERE id = 1");
        await transaction.RollbackAsync();

        // Assert: exactly the pre-transaction entry is visible again.
        var entries = await VisibleEntriesAsync(database, "t", "ix_t_id");
        entries.Count.ShouldBe(1);
        entries[0].Key.ShouldBe(Int32Key(1));
        (await Rows(session, "SELECT id FROM t")).Single()[0].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: crash recovery scrubs an unproven writer's entries out of the index")]
    public async Task Recovery_UnprovenWriter_ShouldScrubIndexEntries()
    {
        // Arrange: a committed row, then an uncommitted insert whose bracket
        // records become durable through a later committed statement (journal
        // ordering) — the classic unproven-writer crash window.
        var strategy = new CrashCaptureSqlStorageStrategy();
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-crash", StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("crash-db");

        var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)");

        var uncommitted = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)");

        // A second session's committed write flushes the journal durably —
        // covering the uncommitted writer's records without committing it.
        var flusherSession = await database.CreateSessionAsync();
        await flusherSession.ExecuteAsync("INSERT INTO t (id) VALUES (3)");

        // Act: crash (capture durable images without a clean shutdown), reopen.
        var crashed = strategy.CaptureDurableImages();
        var reopenedEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-crash-reopen", StorageStrategy = crashed });
        await using var _ = reopenedEngine;
        var reopened = await reopenedEngine.OpenDatabaseAsync("crash-db");

        // Assert: rows 1 and 3 stand, the unproven writer's row AND its index
        // entry are gone.
        await using var verifySession = await reopened.CreateSessionAsync();
        var recoveredRows = await Rows(verifySession, "SELECT id FROM t");
        recoveredRows.Select(row => (int)row[0]!).OrderBy(id => id).ShouldBe(new[] { 1, 3 });

        var entries = await VisibleEntriesAsync(reopened, "t", "ix_t_id");
        entries.Count.ShouldBe(2);
        entries[0].Key.ShouldBe(Int32Key(1));
        entries[1].Key.ShouldBe(Int32Key(3));

        // The crashed engine's disposal is best-effort (its streams are gated).
        await engine.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: root splits drift the root page id and re-export keeps restart consistent")]
    public async Task Restart_AfterRootSplits_ShouldReattachConsistently()
    {
        // Arrange: enough entries to split the root at least once (a leaf holds
        // ~250 int keys), inserted AFTER the index exists so the write path — not
        // the build — drives the splits.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-split", RootPath = _rootPath });
        var database = await engine.CreateDatabaseAsync("split-db");

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
            await session.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");

            for (int start = 0; start < 600; start += 100)
            {
                var values = string.Join(", ", Enumerable.Range(start, 100).Select(i => $"({i})"));
                await session.ExecuteAsync($"INSERT INTO t (id) VALUES {values}");
            }
        }

        await engine.DisposeAsync(); // re-exports drifted registrations

        // Act
        var reopenedEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-split", RootPath = _rootPath });
        await using var _ = reopenedEngine;
        var reopened = await reopenedEngine.OpenDatabaseAsync("split-db");

        // Assert: the re-attached tree serves every entry in order.
        var entries = await VisibleEntriesAsync(reopened, "t", "ix_t_id");
        entries.Count.ShouldBe(600);
        entries[0].Key.ShouldBe(Int32Key(0));
        entries[599].Key.ShouldBe(Int32Key(599));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: DROP TABLE drops the table's indexes with it")]
    public async Task DropTable_WithIndexes_ShouldDropIndexes()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ix-droptable" });
        var database = await engine.CreateDatabaseAsync("droptable-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");
        ulong objectId = ObjectIdOf(database, "t");

        // Act
        await session.ExecuteAsync("DROP TABLE t");

        // Assert
        var instance = (SqlDatabaseInstance)database;
        instance.Catalog.GetIndexes(objectId).ShouldBeEmpty();
        instance.IndexManager.TryGetIndex(objectId, "ix_t_id", out _).ShouldBeFalse();
        instance.Catalog.GetIndexRegistrations().ShouldBeEmpty();
    }
}
