using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// A <c>CREATE INDEX</c> runs in one durable storage bracket, and a bracket keeps the pre-image of
/// every page it touches until it completes. Storage format 2 kept each as a full 8 KiB copy, so an
/// index build held a copy of every page it built, whatever the buffer pool's size. Format 3
/// (#1253) keeps each pre-image as its encoded byte runs, a few dozen bytes for a page the bracket
/// allocated, and spills past a budget (Storage DESIGN.md, "The memory bound of pre-images").
/// </summary>
/// <remarks>
/// Storage counts encoded pre-image bytes plus a fixed bookkeeping allowance, independently of
/// GC heap layout, page reservation granularity and unrelated runtime/worker objects. The
/// checkpoint-size hook runs inside commit while all pre-images remain held. Fresh index pages
/// have only header runs in their pre-images: the existing storage regression bounds these at
/// 2% of the fixed 8 KiB page size. Together with a pool at most one tenth of the index, these
/// page buffers and pre-images account for at most 12% of the index bytes. This is an accounting
/// bound on those structures, not a measurement of the process heap.
/// </remarks>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlCreateIndexMemoryTests : IDisposable
{
    private const int Rows = 9_000;
    private const int KeyLength = 900;
    private const long PoolBytes = 1024 * 1024; // the smallest pool an engine accepts (Storage.MinimumBufferPoolBytes)

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-index-memory", Guid.NewGuid().ToString("N"));

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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CREATE INDEX: an index ten times the buffer pool, built in one bracket, keeps a small fraction of its pages on the heap (#1253)")]
    public async Task CreateIndex_IndexTenTimesThePool_ShouldKeepItsPreImagesSmall()
    {
        // Arrange: a table of wide, randomly ordered keys over the smallest pool.
        await using var engine = SqlDatabaseEngine.Create("create-index-memory", new SqlDatabaseEngineOptions
        {
            RootPath = _rootPath,
            BufferPoolCapacity = PoolBytes,
            CheckpointInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE t (id INT PRIMARY KEY, k VARCHAR({KeyLength}))", cancellationToken: TestTimeout.Token(60));
        var sql = new StringBuilder();
        for (int first = 0; first < Rows; first += 100)
        {
            sql.Clear().Append("INSERT INTO t (id, k) VALUES ");
            for (int id = first; id < first + 100; id++)
            {
                string key = ((id * 7_919L) % Rows).ToString("D6", CultureInfo.InvariantCulture).PadRight(KeyLength, (char)('a' + (id % 26)));
                sql.Append(id == first ? "" : ", ").Append(CultureInfo.InvariantCulture, $"({id}, '{key}')");
            }

            await session.ExecuteAsync(sql.ToString(), cancellationToken: TestTimeout.Token(120));
        }

        var storage = database.DataStorage;
        long pagesBefore = storage.PageManager.PageCount;
        long spilledBefore = SqlStorageMemoryProbe.SpilledPreImages(storage);

        // The hook fires once the commit has journaled a megabyte of the index's pages: the build
        // itself journals only the images of freshly allocated pages, a few dozen bytes each.
        long atCommit = 0;
        var engineHook = storage.OnCheckpointNeeded;
        storage.OnCheckpointNeeded = () =>
        {
            atCommit = SqlStorageMemoryProbe.PreImageBytes(storage);
            engineHook?.Invoke();
        };
        storage.CheckpointJournalSize = storage.JournalLength + (1024 * 1024);

        // Act
        try
        {
            await session.ExecuteAsync("CREATE INDEX t_k ON t(k)", cancellationToken: TestTimeout.Token(300));
        }
        finally { storage.OnCheckpointNeeded = engineHook; }

        // Assert: every page is built in one bracket, with compact pre-images and no spilling.
        // The 8 KiB storage page is format-defined; the OS/GC page size does not enter this bound.
        long indexPages = storage.PageManager.PageCount - pagesBefore;
        long poolPages = PoolBytes / Page.Size;
        storage.BufferPoolCapacity.ShouldBe((int)poolPages);
        indexPages.ShouldBeGreaterThanOrEqualTo(10 * poolPages, "the index must be built over at least ten times the pool");
        atCommit.ShouldBeGreaterThan(0, "the checkpoint-size hook never observed pre-images inside the build's commit");
        SqlStorageMemoryProbe.PeakPreImageBytes(storage).ShouldBe(atCommit);
        atCommit.ShouldBeLessThan(indexPages * Page.Size / 50,
            "freshly allocated index pages must retain header runs, not full page copies");
        SqlStorageMemoryProbe.SpilledPreImages(storage).ShouldBe(spilledBefore);
        SqlStorageMemoryProbe.PreImageBytes(storage).ShouldBe(0);
    }
}
