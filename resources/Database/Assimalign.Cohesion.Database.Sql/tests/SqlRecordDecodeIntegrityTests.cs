using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Row records that pass their page's checksum and owner check and still do not decode (#1362).
/// Every read of a row (the index seek through <c>Storage.TryReadRecord</c> with the table's object
/// id, the scan through the table's owner-scoped iterator) finds the record in a live slot of the
/// table's own page chain, so a record that does not decode is damaged. Before the fix a record too
/// short for its stamps or whose object-id prefix named another object read as "not this table's"
/// and the row vanished from the result, a malformed component failed with a type error that named
/// no page, and a component of another type decoded as a value of that type. Each damage here is
/// written through the storage API in its own committed bracket, and the page is then evicted, so
/// the statement reads it back from the data file and its checksum verifies.
/// </summary>
public sealed class SqlRecordDecodeIntegrityTests
{
    private const string Seek = "SELECT id FROM t WHERE val = 20";

    /// <summary>How a test damages a row record.</summary>
    public enum RecordDamage
    {
        /// <summary>The first column's type tag is no type at all.</summary>
        InvalidTag,

        /// <summary>The first column's INT tag names a REAL, which has the same width.</summary>
        OtherType,

        /// <summary>The object-id prefix names another object.</summary>
        OtherObject,

        /// <summary>The record ends inside the first column's component.</summary>
        Truncated,

        /// <summary>The record is shorter than its version stamps.</summary>
        ShorterThanStamps,
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Record decode: an index seek over a row that does not decode fails with StorageCorruptionException (#1362)")]
    [InlineData(RecordDamage.InvalidTag)]
    [InlineData(RecordDamage.OtherType)]
    [InlineData(RecordDamage.OtherObject)]
    [InlineData(RecordDamage.Truncated)]
    [InlineData(RecordDamage.ShorterThanStamps)]
    public async Task Select_IndexSeekOverRowThatDoesNotDecode_ShouldFailWithStorageCorruption(RecordDamage damage)
    {
        // Arrange: three rows on one page behind an index; the middle one damaged.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("seek-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10), (2, 20), (3, 30)");
        var damaged = RowLocation(database, id: 2);
        Damage(database, damaged, damage);

        // Act
        var failure = await Should.ThrowAsync<StorageCorruptionException>(() => Ids(session, Seek));
        var neighbour = await Ids(session, "SELECT id FROM t WHERE val = 10");

        // Assert: the failure names the record; the page itself reads, so the row beside it does.
        failure.PageId.ShouldBe(damaged.PageId);
        failure.Message.ShouldStartWith(
            $"The row record in slot {damaged.SlotIndex} of page {(long)damaged.PageId} of table 'dbo.t' (object {Table(database).ObjectId}) does not decode: ",
            Case.Sensitive);
        neighbour.ShouldBe([1]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Record decode: a scan over a row that does not decode fails with StorageCorruptionException (#1362)")]
    [InlineData(RecordDamage.InvalidTag)]
    [InlineData(RecordDamage.OtherType)]
    [InlineData(RecordDamage.OtherObject)]
    [InlineData(RecordDamage.Truncated)]
    [InlineData(RecordDamage.ShorterThanStamps)]
    public async Task Select_ScanOverRowThatDoesNotDecode_ShouldFailWithStorageCorruption(RecordDamage damage)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("scan-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10), (2, 20), (3, 30)");
        (await Ids(session, "SELECT id FROM t")).Order().ShouldBe([1, 2, 3]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("scan");
        var damaged = RowLocation(database, id: 2);
        Damage(database, damaged, damage);

        // Act
        var failure = await Should.ThrowAsync<StorageCorruptionException>(() => Ids(session, "SELECT id FROM t"));

        // Assert
        failure.PageId.ShouldBe(damaged.PageId);
        failure.Message.ShouldContain($"slot {damaged.SlotIndex} of page {(long)damaged.PageId} of table 'dbo.t'", Case.Sensitive);
        failure.Message.ShouldContain("does not decode", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Record decode: a write's target scan and an index build over a row that does not decode fail without changing the table (#1362)")]
    public async Task UpdateAndCreateIndex_OverRowThatDoesNotDecode_ShouldFailWithStorageCorruption()
    {
        // Arrange: the damage that used to vanish from every read, a prefix naming another object.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("write-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10), (2, 20), (3, 30)");
        var damaged = RowLocation(database, id: 2);
        byte[] original = Damage(database, damaged, RecordDamage.OtherObject);

        // Act
        var update = await Should.ThrowAsync<StorageCorruptionException>(
            () => session.ExecuteAsync("UPDATE t SET val = val + 1").AsTask());
        var build = await Should.ThrowAsync<StorageCorruptionException>(
            () => session.ExecuteAsync("CREATE INDEX ix_val ON t (val)").AsTask());
        Rewrite(database, damaged, original);
        var values = await Ids(session, "SELECT val FROM t");

        // Assert: with the record repaired, the table reads as it did; neither statement left an
        // effect behind.
        update.PageId.ShouldBe(damaged.PageId);
        build.PageId.ShouldBe(damaged.PageId);
        values.Order().ShouldBe([10, 20, 30]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("scan");
        database.Catalog.TryGetIndex(Table(database).ObjectId, "ix_val", out _).ShouldBeFalse();
    }

    /// <summary>
    /// A stale index entry can name a slot whose page was freed and handed to another table: the
    /// slot holds that table's record, whose object-id prefix is not this table's. That record is
    /// not this table's to decode, so the owner check of the read skips it before any decode runs.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Record decode: an entry whose row page now belongs to another table is skipped, not reported as corruption (#1362)")]
    public async Task Select_IndexEntryOverPageReallocatedToAnotherTable_ShouldSkipTheEntry()
    {
        // Arrange: t's only row is deleted beneath its entry, which frees its page; u's first row
        // takes the page, in the slot t's entry names.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("foreign-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
        await session.ExecuteAsync("CREATE TABLE u (id INT NOT NULL, val INT NOT NULL)");
        var stale = RowLocation(database, id: 2);
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            database.DataStorage.DeleteRow(bracket, stale.PageId, stale.SlotIndex);
            bracket.Commit();
        }

        await session.ExecuteAsync("INSERT INTO u (id, val) VALUES (7, 20)");
        database.DataStorage.GetOwnerPages(Table(database, "u").ObjectId).ShouldBe([stale.PageId]);
        database.DataStorage.TryReadRecord(stale.PageId, stale.SlotIndex, out _).ShouldBeTrue("u's row sits in the slot t's entry names");

        // Act
        var ids = await Ids(session, Seek);

        // Assert
        ids.ShouldBeEmpty();
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Record decode: a scan skips a row whose slot was reclaimed, not reported as corruption (#1362)")]
    public async Task Select_ScanOverReclaimedSlot_ShouldSkipTheRow()
    {
        // Arrange: the middle row's slot deleted beneath the table, as the purge deletes it.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("reclaimed-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10), (2, 20), (3, 30)");
        var reclaimed = RowLocation(database, id: 2);
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            database.DataStorage.DeleteRow(bracket, reclaimed.PageId, reclaimed.SlotIndex);
            bracket.Commit();
        }

        // Act
        var ids = await Ids(session, "SELECT id FROM t");

        // Assert
        ids.Order().ShouldBe([1, 3]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("scan");
    }

    /// <summary>
    /// A scan pins its page but takes no latch, so it can copy a slot while a writer reclaims it:
    /// a bracket rollback restores the page without the slot, or the last delete frees the page and
    /// clears it. The copy does not decode, but the slot was reclaimed, not damaged. The decode is
    /// confirmed by reading the slot again before it is reported, and the re-read finds the slot
    /// gone. The writer here inserts well-formed rows, reverts an insert and deletes every row,
    /// freeing the pages, as statements, failed statements and the version purge do.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Record decode: a scan racing rollbacks and page frees never reports a reclaimed row as corrupt (#1362)")]
    public async Task Select_ScanRacingReclamation_ShouldNeverReportCorruption()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("race-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, label TEXT)");
        var table = Table(database);
        var storage = database.DataStorage;
        string label = new('x', 900);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(() =>
        {
            var live = new List<(PageId PageId, int SlotIndex)>();
            int id = 0;
            while (!stop.IsCancellationRequested)
            {
                using (var bracket = storage.BeginTransaction())
                {
                    for (int index = 0; index < 16; index++)
                    {
                        live.Add(storage.InsertRow(bracket, table.ObjectId, Row(table, id++, label)));
                    }

                    bracket.Commit();
                }

                using (var bracket = storage.BeginTransaction())
                {
                    storage.InsertRow(bracket, table.ObjectId, Row(table, id++, label));
                    bracket.Rollback();
                }

                foreach (var location in live)
                {
                    using var bracket = storage.BeginTransaction();
                    storage.DeleteRow(bracket, location.PageId, location.SlotIndex);
                    bracket.Commit();
                }

                live.Clear();
            }
        });

        // Act
        long scans = 0;
        Exception? failure = null;
        while (!stop.IsCancellationRequested && failure is null)
        {
            try
            {
                await Ids(session, "SELECT id FROM t");
                scans++;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        await writer;

        // Assert
        failure.ShouldBeNull();
        scans.ShouldBeGreaterThan(0);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });

    private static byte[] Row(SqlCatalogTable table, int id, string label)
        => SqlRowCodec.Encode(table, [id, label], new TransactionSequence(1));

    private static async Task<int[]> Ids(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token());
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();
        var ids = new List<int>();
        await foreach (var row in resultSet!.GetRowsAsync())
        {
            ids.Add(Convert.ToInt32(row.GetValue(0)));
        }

        return ids.ToArray();
    }

    private static SqlCatalogTable Table(SqlDatabase database, string name = "t")
    {
        database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
        return table;
    }

    private static (PageId PageId, int SlotIndex) RowLocation(SqlDatabase database, int id)
    {
        var table = Table(database);
        using var iterator = database.DataStorage.GetUnitIterator(table.ObjectId);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            if (Convert.ToInt32(SqlRowCodec.Decode(unit.Data.Span, table, out _, out _, out _)[0]) == id)
            {
                return (unit.PageId, unit.SlotIndex);
            }
        }

        throw new InvalidOperationException($"Row {id} was not found.");
    }

    /// <summary>
    /// Rewrites a row record damaged (<see cref="Rewrite"/>).
    /// </summary>
    /// <returns>The record as it was.</returns>
    private static byte[] Damage(SqlDatabase database, (PageId PageId, int SlotIndex) location, RecordDamage damage)
    {
        byte[] record = database.DataStorage.ReadRow(location.PageId, location.SlotIndex).ToArray();
        const int idTag = SqlRowCodec.StampHeaderSize + 9;
        record[idTag].ShouldBe((byte)DatabaseType.Int32);

        byte[] damaged = damage switch
        {
            RecordDamage.InvalidTag => Patched(record, idTag, 0xEE),
            RecordDamage.OtherType => Patched(record, idTag, (byte)DatabaseType.Float32),
            RecordDamage.OtherObject => Patched(record, idTag - 1, (byte)(record[idTag - 1] ^ 0x01)),
            RecordDamage.Truncated => record[..(idTag + 3)],
            RecordDamage.ShorterThanStamps => record[..8],
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };

        Rewrite(database, location, damaged);
        return record;
    }

    /// <summary>
    /// Rewrites a row record through the storage API in a committed bracket, then evicts its page,
    /// which writes it back with its checksum recomputed: the next read verifies the page and finds
    /// the record in a live slot.
    /// </summary>
    private static void Rewrite(SqlDatabase database, (PageId PageId, int SlotIndex) location, byte[] record)
    {
        var storage = database.DataStorage;
        using (var bracket = storage.BeginTransaction())
        {
            storage.UpdateRow(bracket, location.PageId, location.SlotIndex, record);
            bracket.Commit();
        }

        Evict(database, location.PageId);
    }

    private static byte[] Patched(byte[] record, int offset, byte value)
    {
        byte[] patched = record.ToArray();
        patched[offset] = value;
        return patched;
    }

    /// <summary>
    /// Evicts a page from the data storage's buffer pool: the pool shrinks to one frame, which
    /// another page then takes, and grows back. A dirty page is written back first.
    /// </summary>
    private static void Evict(SqlDatabase database, PageId pageId)
    {
        var storage = database.DataStorage;
        var other = Enumerable.Range(1, (int)storage.PageManager.PageCount - 1)
            .Select(page => (PageId)(long)page)
            .First(page => page != pageId && storage.FreeSpaceMap.IsAllocated(page));
        int capacity = storage.BufferPoolCapacity;
        storage.BufferPoolCapacity = 1;
        using (storage.PageManager.GetPage(other))
        {
        }

        storage.BufferPoolCapacity = capacity;
    }
}
