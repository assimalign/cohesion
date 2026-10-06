using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Per-object record chains at the SQL surface (#911): each table's rows live on
/// its own pages, table scans stop decoding the whole database, and DROP TABLE
/// releases its chain. A pre-chain (format-version-2) database is refused at open
/// like every database on another format (<see cref="SqlDataStorageFormatTests"/>).
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

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
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

    private static async Task BulkInsertAsync(SqlDatabaseSession session, string table, int count)
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

    private static ulong ObjectIdOf(SqlDatabase database, string table)
    {
        var instance = database;
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

        var instance = database;
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

        var instance = database;
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
        var instance = reopened;
        var pagesOfA = instance.DataStorage.GetOwnerPages(ObjectIdOf(reopened, "a")).Select(p => (long)p).ToHashSet();
        var pagesOfB = instance.DataStorage.GetOwnerPages(ObjectIdOf(reopened, "b")).Select(p => (long)p).ToHashSet();

        pagesOfA.Count.ShouldBeGreaterThan(3);
        pagesOfB.Count.ShouldBe(1);
        pagesOfA.Overlaps(pagesOfB).ShouldBeFalse();

        (await Rows(verifySession, "SELECT COUNT(*) FROM a")).Single()[0].ShouldBe(120L);
        (await Rows(verifySession, "SELECT COUNT(*) FROM b")).Single()[0].ShouldBe(3L);
    }
}
