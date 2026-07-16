using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Per-object record chains at the SQL surface (#911): each table's rows live on
/// its own pages, table scans stop decoding the whole database, DROP TABLE releases
/// its chain, and a pre-chain (format-version-2) database upgrades in place at open.
/// </summary>
public sealed class SqlPerObjectChainTests : IDisposable
{
    private readonly string _rootPath;

    public SqlPerObjectChainTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-chains", Guid.NewGuid().ToString("N"));
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

    private static async Task BulkInsertAsync(IDatabaseSession session, string table, int count)
    {
        // Multi-row VALUES in batches keeps the test fast while spanning many pages.
        const int batchSize = 50;
        string filler = new('x', 180);

        for (int start = 0; start < count; start += batchSize)
        {
            int size = Math.Min(batchSize, count - start);
            var values = string.Join(", ", Enumerable.Range(start, size).Select(i => $"({i}, '{filler}')"));
            await session.ExecuteAsync($"INSERT INTO {table} (id, payload) VALUES {values}");
        }
    }

    private static ulong ObjectIdOf(IDatabase database, string table)
    {
        var instance = (SqlDatabaseInstance)database;
        instance.Catalog.TryGetTable("dbo", table, out var catalogTable).ShouldBeTrue();
        return catalogTable.ObjectId;
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Per-object chains: tables occupy disjoint page sets and small-table scans stay small")]
    public async Task Scan_TwoTables_ShouldUseDisjointOwnerChains()
    {
        // Arrange: one large table, one one-row table.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "chains" });
        var database = await engine.CreateDatabaseAsync("chains-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE big (id INT NOT NULL, payload VARCHAR(200))");
        await session.ExecuteAsync("CREATE TABLE small (id INT NOT NULL, payload VARCHAR(200))");

        await BulkInsertAsync(session, "big", 500);
        await session.ExecuteAsync("INSERT INTO small (id, payload) VALUES (1, 'tiny')");

        var instance = (SqlDatabaseInstance)database;
        var bigPages = instance.DataStorage.GetOwnerPages(ObjectIdOf(database, "big")).Select(p => (long)p).ToHashSet();
        var smallPages = instance.DataStorage.GetOwnerPages(ObjectIdOf(database, "small")).Select(p => (long)p).ToHashSet();

        // Assert: disjoint chains; the small table's chain is O(its own rows) even
        // though the database holds hundreds of pages of the big table.
        bigPages.Count.ShouldBeGreaterThan(10);
        smallPages.Count.ShouldBe(1);
        bigPages.Overlaps(smallPages).ShouldBeFalse();

        // And both scans return exactly their own rows.
        (await Rows(session, "SELECT id FROM small")).Count.ShouldBe(1);
        (await Rows(session, "SELECT COUNT(*) FROM big")).Single()[0].ShouldBe(500L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Per-object chains: DROP TABLE releases the table's pages for reuse")]
    public async Task DropTable_ShouldReleaseChainPages()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "chains-drop" });
        var database = await engine.CreateDatabaseAsync("drop-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE victim (id INT NOT NULL, payload VARCHAR(200))");
        await BulkInsertAsync(session, "victim", 300);

        var instance = (SqlDatabaseInstance)database;
        ulong victimId = ObjectIdOf(database, "victim");
        instance.DataStorage.GetOwnerPages(victimId).Count.ShouldBeGreaterThan(5);
        long totalPagesBefore = instance.DataStorage.PageManager.PageCount;

        // Act
        await session.ExecuteAsync("DROP TABLE victim");

        // Assert: the chain is released...
        instance.DataStorage.GetOwnerPages(victimId).ShouldBeEmpty();
        instance.DataStorage.FreeSpaceMap.FreePageCount.ShouldBeGreaterThan(0);

        // ...and a successor table reuses the freed pages instead of growing the file.
        await session.ExecuteAsync("CREATE TABLE successor (id INT NOT NULL, payload VARCHAR(200))");
        await BulkInsertAsync(session, "successor", 300);
        instance.DataStorage.PageManager.PageCount.ShouldBe(totalPagesBefore);
        (await Rows(session, "SELECT COUNT(*) FROM successor")).Single()[0].ShouldBe(300L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Per-object chains: a restart rebuilds the chains from page headers")]
    public async Task Restart_FileBacked_ShouldRebuildChains()
    {
        // Arrange: committed rows across two tables, clean engine shutdown.
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "chains-restart", RootPath = _rootPath });
        var database = await engine.CreateDatabaseAsync("restart-db");

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE a (id INT NOT NULL, payload VARCHAR(200))");
            await session.ExecuteAsync("CREATE TABLE b (id INT NOT NULL, payload VARCHAR(200))");
            await BulkInsertAsync(session, "a", 120);
            await BulkInsertAsync(session, "b", 3);
        }

        await engine.DisposeAsync();

        // Act
        var reopenedEngine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "chains-restart", RootPath = _rootPath });
        await using var _ = reopenedEngine;
        var reopened = await reopenedEngine.OpenDatabaseAsync("restart-db");
        await using var verifySession = await reopened.CreateSessionAsync();

        // Assert: the directory is rebuilt (disjoint, correctly sized chains) and
        // scans return the recovered rows.
        var instance = (SqlDatabaseInstance)reopened;
        var pagesOfA = instance.DataStorage.GetOwnerPages(ObjectIdOf(reopened, "a")).Select(p => (long)p).ToHashSet();
        var pagesOfB = instance.DataStorage.GetOwnerPages(ObjectIdOf(reopened, "b")).Select(p => (long)p).ToHashSet();

        pagesOfA.Count.ShouldBeGreaterThan(3);
        pagesOfB.Count.ShouldBe(1);
        pagesOfA.Overlaps(pagesOfB).ShouldBeFalse();

        (await Rows(verifySession, "SELECT COUNT(*) FROM a")).Single()[0].ShouldBe(120L);
        (await Rows(verifySession, "SELECT COUNT(*) FROM b")).Single()[0].ShouldBe(3L);
    }

    // ── The format-version-2 → 3 in-place upgrade ──────────────────────

    /// <summary>
    /// Builds a database in the pre-chain (format-version-2) layout by hand: rows
    /// stamped but written into the shared owner-zero page stream, catalog marker 2.
    /// Returns the captured file images (data + journal per file set).
    /// </summary>
    private static (byte[] Data, byte[] DataJournal, byte[] CatalogData, byte[] CatalogJournal, ulong ObjectId) BuildVersion2Database(int rowCount)
    {
        var data = new MemoryStream();
        var dataJournal = new MemoryStream();
        var dataBackup = new MemoryStream();
        var catalogData = new MemoryStream();
        var catalogJournal = new MemoryStream();
        var catalogBackup = new MemoryStream();

        ulong objectId;

        using (var storage = SqlStorage.Create(data, dataJournal, dataBackup, "legacy"))
        using (var catalogStorage = SqlStorage.Create(catalogData, catalogJournal, catalogBackup, "legacy.catalog"))
        {
            var catalog = SqlCatalog.Open(catalogStorage);
            var table = catalog.CreateTableAsync(
                "dbo",
                "legacy",
                new[]
                {
                    new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), isNullable: false),
                    new SqlCatalogColumn("label", new DatabaseTypeInfo(DatabaseType.String, 100), isNullable: true),
                }).AsTask().GetAwaiter().GetResult();
            objectId = table.ObjectId;

            // Rows in the SHARED page stream (owner zero) — the version-2 layout.
            using (var transaction = storage.BeginTransaction())
            {
                for (int i = 0; i < rowCount; i++)
                {
                    byte[] record = SqlRowCodec.Encode(
                        objectId, table.Columns, new object?[] { i, $"row-{i}" }, writer: default);
                    storage.InsertRow(transaction, record);
                }

                transaction.Commit();
            }

            catalog.SetRecordSpaceFormatVersionAsync(2).AsTask().GetAwaiter().GetResult();
        }

        return (data.ToArray(), dataJournal.ToArray(), catalogData.ToArray(), catalogJournal.ToArray(), objectId);
    }

    private static SqlStorage OpenFromImages(byte[] dataImage, byte[] journalImage, bool checkpointOnOpen)
    {
        // MemoryStream(byte[]) is non-expandable — copy into expandable streams.
        var data = new MemoryStream();
        data.Write(dataImage);
        data.Position = 0;

        var journal = new MemoryStream();
        journal.Write(journalImage);
        journal.Position = 0;

        return SqlStorage.Open(data, journal, new MemoryStream(), checkpointOnOpen);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Per-object chains: a format-version-2 database relocates into chains at open")]
    public async Task Upgrade_Version2Database_ShouldRelocateRowsToChains()
    {
        // Arrange: a hand-built version-2 database (rows in the shared page stream).
        var (dataImage, journalImage, catalogImage, catalogJournalImage, objectId) = BuildVersion2Database(rowCount: 40);

        // Act: opening the instance runs the in-place upgrade.
        var storage = OpenFromImages(dataImage, journalImage, checkpointOnOpen: false);
        var catalogStorage = OpenFromImages(catalogImage, catalogJournalImage, checkpointOnOpen: true);
        var instance = new SqlDatabaseInstance("legacy", engine: null!, storage, catalogStorage, recover: true);
        await using var _ = instance;

        // Assert: the marker moved, rows live in the table's chain, the shared
        // space is empty, and everything is still visible with stamps preserved.
        instance.Catalog.RecordSpaceFormatVersion.ShouldBe(3);
        instance.DataStorage.GetOwnerPages(objectId).ShouldNotBeEmpty();
        instance.DataStorage.GetOwnerPages(0).ShouldBeEmpty();

        await using var session = await instance.CreateSessionAsync();
        (await Rows(session, "SELECT COUNT(*) FROM legacy")).Single()[0].ShouldBe(40L);
        (await Rows(session, "SELECT label FROM legacy WHERE id = 7")).Single()[0].ShouldBe("row-7");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Per-object chains: the chain upgrade is idempotent across the crash window")]
    public async Task Upgrade_RerunAfterCrashWindow_ShouldBeIdempotent()
    {
        // Arrange: run the upgrade once, then simulate the crash window — the
        // relocation committed but the catalog marker write was lost (rolled back
        // to 2) — and reopen so the upgrade runs again over moved records.
        var (dataImage, journalImage, catalogImage, catalogJournalImage, objectId) = BuildVersion2Database(rowCount: 25);

        byte[] upgradedData;
        byte[] upgradedJournal;
        byte[] upgradedCatalog;
        byte[] upgradedCatalogJournal;

        var dataStream = new MemoryStream();
        dataStream.Write(dataImage);
        dataStream.Position = 0;
        var journalStream = new MemoryStream();
        journalStream.Write(journalImage);
        journalStream.Position = 0;
        var catalogStream = new MemoryStream();
        catalogStream.Write(catalogImage);
        catalogStream.Position = 0;
        var catalogJournalStream = new MemoryStream();
        catalogJournalStream.Write(catalogJournalImage);
        catalogJournalStream.Position = 0;

        var storage = SqlStorage.Open(dataStream, journalStream, new MemoryStream(), checkpointOnOpen: false);
        var catalogStorage = SqlStorage.Open(catalogStream, catalogJournalStream, new MemoryStream());
        var instance = new SqlDatabaseInstance("legacy", engine: null!, storage, catalogStorage, recover: true);

        // The relocation is durable; regress only the marker (the crash window).
        await instance.Catalog.SetRecordSpaceFormatVersionAsync(2);
        await instance.DisposeAsync();

        upgradedData = dataStream.ToArray();
        upgradedJournal = journalStream.ToArray();
        upgradedCatalog = catalogStream.ToArray();
        upgradedCatalogJournal = catalogJournalStream.ToArray();

        // Act: reopen — the upgrade re-runs over already-relocated records.
        var reopenedStorage = OpenFromImages(upgradedData, upgradedJournal, checkpointOnOpen: false);
        var reopenedCatalogStorage = OpenFromImages(upgradedCatalog, upgradedCatalogJournal, checkpointOnOpen: true);
        var reopened = new SqlDatabaseInstance("legacy", engine: null!, reopenedStorage, reopenedCatalogStorage, recover: true);
        await using var _ = reopened;

        // Assert: no duplication, marker restored to 3.
        reopened.Catalog.RecordSpaceFormatVersion.ShouldBe(3);
        await using var session = await reopened.CreateSessionAsync();
        (await Rows(session, "SELECT COUNT(*) FROM legacy")).Single()[0].ShouldBe(25L);
    }
}
