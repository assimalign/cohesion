using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The rows an index seek fetches (#1342). An entry whose row was reclaimed beneath it (its slot
/// deleted, its page freed, the page reallocated as an index node) is stale and skipped. A row page
/// that cannot be read is not reclaimed: before the fix the executor read every storage error as
/// reclamation, so a page that failed its checksum dropped its rows from the result instead of
/// failing the statement. PostgreSQL's heap fetch makes the same split: a line pointer past the
/// page or no longer in use is "not found", and an invalid page is <c>ERRCODE_DATA_CORRUPTED</c>.
/// </summary>
public sealed class SqlIndexReadIntegrityTests
{
    private const string Seek = "SELECT id FROM t WHERE val = 20";

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Index reads: a row page that fails its checksum fails the seek instead of dropping its rows (#1342)")]
    public async Task Select_IndexSeekReadsRottenRowPage_ShouldFailWithStorageCorruption()
    {
        // Arrange: three rows on one page behind an index, read once through the seek, then the
        // page evicted so the next seek reads it from the data file.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = CreateEngine(strategy);
        var database = await engine.CreateDatabaseAsync("rot-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 10), (2, 20), (3, 30)");
        (await Ids(session, Seek)).ShouldBe([2]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
        var rowPage = database.DataStorage.GetOwnerPages(Table(database, "t").ObjectId).ShouldHaveSingleItem();
        Evict(database, rowPage);

        // Act: the page decays on the device.
        var faults = strategy.Faults("rot-db");
        faults.RottenPage = rowPage;
        var failure = await Should.ThrowAsync<StorageCorruptionException>(() => session.ExecuteAsync(Seek).AsTask());
        long rottenReads = faults.RottenPageReads;
        faults.RottenPage = null;
        await using var next = await database.CreateSessionAsync();
        var healed = await Ids(next, Seek);

        // Assert: the statement failed with the page's checksum error. The failed read was not
        // cached, so once the device returns the page whole the same seek finds its row.
        failure.PageId.ShouldBe(rowPage);
        failure.Message.ShouldContain("failed checksum verification", Case.Sensitive);
        rottenReads.ShouldBeGreaterThan(0);
        healed.ShouldBe([2]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Index reads: an entry whose row slot was reclaimed beneath it is skipped (#1342)")]
    public async Task Select_IndexEntryOverDeletedSlot_ShouldSkipTheEntry()
    {
        // Arrange: three rows share a key on one page. The middle row's slot is deleted beneath its
        // entry: the state a purge or an undo leaves under an entry a reader already holds.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync("slot-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 20), (2, 20), (3, 20)");
        var reclaimed = RowLocation(database, "t", id: 2);
        DeleteBeneathIndex(database, reclaimed);

        // Act
        var ids = await Ids(session, Seek);

        // Assert: the stale entry was examined and skipped; its page still holds the other rows.
        ids.Order().ShouldBe([1, 3]);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(3);
        database.DataStorage.FreeSpaceMap.IsAllocated(reclaimed.PageId).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Index reads: an entry whose row page was freed beneath it is skipped (#1342)")]
    public async Task Select_IndexEntryOverFreedPage_ShouldSkipTheEntry()
    {
        // Arrange: the table's only row is deleted beneath its entry, which frees its page.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync("page-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
        var reclaimed = RowLocation(database, "t", id: 2);
        DeleteBeneathIndex(database, reclaimed);
        database.DataStorage.FreeSpaceMap.IsAllocated(reclaimed.PageId).ShouldBeFalse();

        // Act
        var ids = await Ids(session, Seek);

        // Assert
        ids.ShouldBeEmpty();
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Index reads: an entry whose row page now holds an index node is skipped, not reported as corruption (#1342)")]
    public async Task Select_IndexEntryOverPageReusedAsIndexNode_ShouldSkipTheEntry()
    {
        // Arrange: the freed row page is the next page the allocator hands out, and the next
        // CREATE INDEX takes it for its root.
        await using var engine = CreateEngine(strategy: null);
        var database = await engine.CreateDatabaseAsync("reuse-db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_val ON t (val)");
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (2, 20)");
        var reclaimed = RowLocation(database, "t", id: 2);
        DeleteBeneathIndex(database, reclaimed);
        await session.ExecuteAsync("CREATE TABLE u (id INT NOT NULL)");
        await session.ExecuteAsync("CREATE INDEX ix_u ON u (id)");
        using (var reused = database.DataStorage.PageManager.GetPage(reclaimed.PageId))
        {
            reused.Page.Type.ShouldBe(PageType.Index);
        }

        // Act
        var ids = await Ids(session, Seek);

        // Assert
        ids.ShouldBeEmpty();
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_val");
    }

    private static SqlDatabaseEngine CreateEngine(FaultInjectingJournalSqlStorageStrategy? strategy)
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });

    private static async Task<int[]> Ids(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql);
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();
        var ids = new List<int>();
        await foreach (var row in resultSet!.GetRowsAsync())
        {
            ids.Add(Convert.ToInt32(row.GetValue(0)));
        }

        return ids.ToArray();
    }

    private static SqlCatalogTable Table(SqlDatabase database, string name)
    {
        database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
        return table;
    }

    private static (PageId PageId, int SlotIndex) RowLocation(SqlDatabase database, string tableName, int id)
    {
        var table = Table(database, tableName);
        using var iterator = database.DataStorage.GetUnitIterator(table.ObjectId);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            if (Convert.ToInt32(SqlRowCodec.Decode(unit.Data.Span, table, out _, out _, out _)[0]) == id)
            {
                return (unit.PageId, unit.SlotIndex);
            }
        }

        throw new InvalidOperationException($"Row {id} of '{tableName}' was not found.");
    }

    /// <summary>
    /// Deletes a row version's slot in its own storage bracket, leaving its index entry in place.
    /// </summary>
    private static void DeleteBeneathIndex(SqlDatabase database, (PageId PageId, int SlotIndex) location)
    {
        using var bracket = database.DataStorage.BeginTransaction();
        database.DataStorage.DeleteRow(bracket, location.PageId, location.SlotIndex);
        bracket.Commit();
    }

    /// <summary>
    /// Evicts a page from the data storage's buffer pool: the pool shrinks to one frame, which
    /// another page then takes, and grows back.
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
