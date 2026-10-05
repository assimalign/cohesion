using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A <c>CREATE INDEX</c> runs in one durable storage bracket, and a bracket keeps the pre-image of
/// every page it touches until it completes. Storage format 2 kept each as a full 8 KiB copy, so an
/// index build held a copy of every page it built, whatever the buffer pool's size. Format 3
/// (#1253) keeps each pre-image as its encoded byte runs, a few dozen bytes for a page the bracket
/// allocated, and spills past a budget (Storage DESIGN.md, "The memory bound of pre-images").
/// </summary>
/// <remarks>
/// The measurement is the managed heap a full collection keeps alive at the bracket's peak, while
/// its commit journals the index pages: the storage's checkpoint-size hook fires on the appending
/// thread, inside the commit, with every pre-image still held. The test runs in the timing
/// collection, alone, so no other test's objects are on the heap, and it reads files, so the data
/// file and the journal are not on the heap either. The bound is a ratio to the index's page bytes,
/// never an absolute size: format 2's copies alone made it above 1.
/// </remarks>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlCreateIndexMemoryTests : IDisposable
{
    private const int Rows = 9_000;
    private const int KeyLength = 900;
    private const long PoolBytes = 1024 * 1024; // the smallest pool an engine accepts (Storage.MinimumBufferPoolBytes)
    private const double AllowedHeapPerIndexByte = 0.25;

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
        // The debug consistency check keeps a full shadow of every page imaged since the checkpoint
        // by design (Storage DESIGN.md, "The debug consistency check"), so the bound holds only
        // with it off.
        if (Environment.GetEnvironmentVariable("COHESION_STORAGE_CONSISTENCY_CHECKS") is "1" or "true" or "True" or "TRUE")
        {
            return;
        }

        // Arrange: a table of wide, randomly ordered keys over the smallest pool.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "create-index-memory",
            RootPath = _rootPath,
            BufferPoolCapacity = PoolBytes,
            CheckpointInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = (SqlDatabaseInstance)await engine.CreateDatabaseAsync("db");
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

        // The hook fires once the commit has journaled a megabyte of the index's pages: the build
        // itself journals only the images of freshly allocated pages, a few dozen bytes each.
        long atPeak = 0;
        var engineHook = storage.OnCheckpointNeeded;
        storage.OnCheckpointNeeded = () =>
        {
            if (atPeak == 0)
            {
                atPeak = GC.GetTotalMemory(forceFullCollection: true);
            }

            engineHook?.Invoke();
        };
        storage.CheckpointJournalSize = storage.JournalLength + (1024 * 1024);
        long baseline = GC.GetTotalMemory(forceFullCollection: true);

        // Act
        await session.ExecuteAsync("CREATE INDEX t_k ON t(k)", cancellationToken: TestTimeout.Token(300));

        // Assert: the index is at least ten pools, and the heap at the bracket's peak held a small
        // fraction of its bytes.
        long indexPages = storage.PageManager.PageCount - pagesBefore;
        long poolPages = PoolBytes / 8192;
        indexPages.ShouldBeGreaterThanOrEqualTo(10 * poolPages, "the index must be built over at least ten times the pool");
        atPeak.ShouldBeGreaterThan(0, "the checkpoint-size hook never fired inside the build's commit");
        double ratio = (double)(atPeak - baseline) / (indexPages * 8192);
        ratio.ShouldBeLessThan(
            AllowedHeapPerIndexByte,
            $"the heap grew by {(atPeak - baseline) / 1048576.0:F1} MiB at the bracket's peak, building {indexPages:N0} index pages ({indexPages * 8192 / 1048576.0:F1} MiB)");
    }
}
