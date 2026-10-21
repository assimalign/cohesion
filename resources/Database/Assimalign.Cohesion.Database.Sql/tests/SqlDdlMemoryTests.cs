using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Measures retained DDL workspace over a table larger than the buffer pool (#1444).</summary>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlDdlMemoryTests
{
    private const int rows = 4_096;
    private const int keyLength = 900;
    private readonly ITestOutputHelper _output;

    public SqlDdlMemoryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DDL: validation and unique builds retain a bounded fraction of a wide table (#1444)")]
    [InlineData("constraint")]
    [InlineData("column")]
    [InlineData("index")]
    [InlineData("constraint-index")]
    public async Task ExecuteAsync_TableLargerThanPool_ShouldBoundRetainedWorkspace(string operation)
    {
        // Arrange: file-backed rows exceed the fixed 1 MiB pool; a full row/key set
        // is observable after full collections inside the existing progress callback.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-ddl-memory", Guid.NewGuid().ToString("N"));
        long minimum = long.MaxValue;
        try
        {
            for (int round = 0; round < 3; round++)
            {
                await using var engine = SqlDatabaseEngine.Create("ddl-memory", new SqlDatabaseEngineOptions
                {
                    RootPath = root,
                    BufferPoolCapacity = 1024 * 1024,
                    CheckpointInterval = TimeSpan.FromHours(1),
                    MaintenanceInterval = TimeSpan.FromHours(1),
                });
                var database = await engine.CreateDatabaseAsync("db" + round.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await SeedAsync(session);
                long baseline = GC.GetTotalMemory(forceFullCollection: true);
                long peak = 0;
                int samples = 0;
                session.ProvisioningProgress = (processed, total) =>
                {
                    total.ShouldBe((long)rows);
                    if (processed == 1 || processed == rows / 2 || processed == rows)
                    {
                        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
                        samples++;
                    }
                };

                // Act
                await session.ExecuteAsync(operation switch
                {
                    "constraint" => "ALTER TABLE t ADD CONSTRAINT ck CHECK (LENGTH(k) > 0)",
                    "column" => "ALTER TABLE t ADD COLUMN v INT NOT NULL DEFAULT 7",
                    "index" => "CREATE UNIQUE INDEX ix ON t(k)",
                    _ => "ALTER TABLE t ADD CONSTRAINT ix UNIQUE(k)",
                }, cancellationToken: TestTimeout.Token(300));
                session.ProvisioningProgress = null;
                minimum = Math.Min(minimum, peak);
                _output.WriteLine($"{operation} round {round}: retained workspace={peak:N0} bytes; samples={samples}");

                // Assert: prove that the callback sampled actual work, including its end.
                samples.ShouldBeGreaterThanOrEqualTo(2);
            }

            long decodedTableBytes = (long)rows * keyLength * sizeof(char);
            _output.WriteLine($"{operation} minimum of 3: {minimum:N0} / {decodedTableBytes:N0} bytes ({(double)minimum / decodedTableBytes:P2})");
            minimum.ShouldBeLessThan(decodedTableBytes / 4,
                "retained workspace must fit below one quarter of the decoded table, including pool and storage bookkeeping");
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unique builds: allocation overhead fits one bounded cursor pass instead of hex keys (#1444)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_UniqueBuild_ShouldAllocateLessThanAFullHexKeySet(bool deferred, bool constraint)
    {
        // Arrange: compare identical tree builds, then isolate duplicate detection's
        // allocation. Minimums of three rounds exclude setup, warm-up and drops.
        await using var engine = SqlDatabaseEngine.Create("unique-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        if (deferred) { session.ProvisioningProgress = (_, _) => { }; }
        long ordinary = long.MaxValue;
        long unique = long.MaxValue;

        // Act
        for (int round = 0; round <= 3; round++)
        {
            long currentOrdinary = await MeasureAsync("CREATE INDEX ordinary ON t(k)");
            if (constraint)
            {
                // Constraint DDL also coerces and validates every row before building.
                // Include the same validation work in the non-unique control.
                currentOrdinary += await MeasureAsync("ALTER TABLE t ADD CONSTRAINT ck CHECK(id >= 0)");
                await session.ExecuteAsync("ALTER TABLE t DROP CONSTRAINT ck", cancellationToken: TestTimeout.Token());
            }
            await session.ExecuteAsync("DROP INDEX ordinary ON t", cancellationToken: TestTimeout.Token());
            long currentUnique = await MeasureAsync(constraint
                ? "ALTER TABLE t ADD CONSTRAINT ix UNIQUE(k)" : "CREATE UNIQUE INDEX ix ON t(k)");
            await session.ExecuteAsync(constraint ? "ALTER TABLE t DROP CONSTRAINT ix" : "DROP INDEX ix ON t", cancellationToken: TestTimeout.Token());
            if (round > 0)
            {
                ordinary = Math.Min(ordinary, currentOrdinary);
                unique = Math.Min(unique, currentUnique);
            }
        }

        // Assert: a hex string alone needs four bytes per encoded key byte. The
        // cursor copies each key once plus bounded batches/descent bookkeeping.
        _output.WriteLine($"deferred={deferred}, constraint={constraint}, minimum of 3: ordinary={ordinary:N0}, unique={unique:N0}, overhead={unique - ordinary:N0} bytes");
        (unique - ordinary).ShouldBeLessThan(2L * rows * keyLength);

        async Task<long> MeasureAsync(string sql)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token(300));
            return GC.GetTotalAllocatedBytes(precise: true) - before;
        }
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync($"CREATE TABLE t (id INT, k VARCHAR({keyLength}))", cancellationToken: TestTimeout.Token());
        var sql = new StringBuilder();
        for (int first = 0; first < rows; first += 64)
        {
            sql.Clear().Append("INSERT INTO t VALUES ");
            for (int id = first; id < first + 64; id++)
            {
                string key = ((id * 7_919L) % rows).ToString("D6", CultureInfo.InvariantCulture).PadRight(keyLength, 'x');
                sql.Append(id == first ? "" : ",").Append(CultureInfo.InvariantCulture, $"({id},'{key}')");
            }
            await session.ExecuteAsync(sql.ToString(), cancellationToken: TestTimeout.Token(120));
        }
    }
}
