using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// A <c>CREATE INDEX</c> commits at most 64 rows per physical bracket (#1808).
/// Pre-image accounting must remain bounded when a later chunk updates pages made
/// by an earlier chunk, including randomly ordered keys over a small buffer pool.
/// </summary>
/// <remarks>The probe counts encoded pre-images and bookkeeping, independently of GC heap layout.</remarks>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlCreateIndexMemoryTests : IDisposable
{
    private const int rows = 9_000;
    private const int keyLength = 900;
    private const long poolBytes = 1024 * 1024; // the smallest pool an engine accepts (Storage.MinimumBufferPoolBytes)

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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CREATE INDEX: an index ten times the buffer pool keeps bounded pre-images per chunk (#1253, #1808)")]
    public async Task CreateIndex_IndexTenTimesThePool_ShouldKeepItsPreImagesSmall()
    {
        // Arrange: a table of wide, randomly ordered keys over the smallest pool.
        await using var engine = SqlDatabaseEngine.Create("create-index-memory", new SqlDatabaseEngineOptions
        {
            RootPath = _rootPath,
            BufferPoolCapacity = poolBytes,
            CheckpointInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync($"CREATE TABLE t (id INT PRIMARY KEY, k VARCHAR({keyLength}))", cancellationToken: TestTimeout.Token(60));
        var sql = new StringBuilder();
        for (int first = 0; first < rows; first += 100)
        {
            sql.Clear().Append("INSERT INTO t (id, k) VALUES ");
            for (int id = first; id < first + 100; id++)
            {
                string key = ((id * 7_919L) % rows).ToString("D6", CultureInfo.InvariantCulture).PadRight(keyLength, (char)('a' + (id % 26)));
                sql.Append(id == first ? "" : ", ").Append(CultureInfo.InvariantCulture, $"({id}, '{key}')");
            }

            await session.ExecuteAsync(sql.ToString(), cancellationToken: TestTimeout.Token(120));
        }

        var storage = database.DataStorage;
        long pagesBefore = storage.PageManager.PageCount;
        long spilledBefore = SqlStorageMemoryProbe.SpilledPreImages(storage);

        // Observe every build bracket before commit while its pre-images remain held.
        long buildPeak = 0;
        int chunks = 0;
        SqlPlanExecutor.IndexBuildBoundary = (stage, count, _) =>
        {
            if (stage != "chunk-before-commit") { return; }
            count.ShouldBeInRange(1, 64);
            chunks++;
            buildPeak = Math.Max(buildPeak, SqlStorageMemoryProbe.PreImageBytes(storage));
        };
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
        finally { storage.OnCheckpointNeeded = engineHook; SqlPlanExecutor.IndexBuildBoundary = null; }

        // Assert: existing pages have larger pre-images than fresh headers, but only one chunk retains them.
        // The 8 KiB storage page is format-defined; the OS/GC page size does not enter this bound.
        long indexPages = storage.PageManager.PageCount - pagesBefore;
        long poolPages = poolBytes / Page.Size;
        storage.BufferPoolCapacity.ShouldBe((int)poolPages);
        indexPages.ShouldBeGreaterThanOrEqualTo(10 * poolPages, "the index must be built over at least ten times the pool");
        atCommit.ShouldBeGreaterThan(0, "the checkpoint-size hook never observed pre-images inside the build's commit");
        chunks.ShouldBe((rows + 63) / 64);
        buildPeak.ShouldBeGreaterThan(0);
        buildPeak.ShouldBeLessThanOrEqualTo(64L * 2 * Page.Size,
            "pre-images must be bounded by one chunk rather than the whole tree");
        atCommit.ShouldBeLessThanOrEqualTo(buildPeak);
        SqlStorageMemoryProbe.SpilledPreImages(storage).ShouldBe(spilledBefore);
        SqlStorageMemoryProbe.PreImageBytes(storage).ShouldBe(0);
    }
}
