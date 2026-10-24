using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;

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
    [InlineData("inline-index")]
    [InlineData("inline-constraint-index")]
    public async Task ExecuteAsync_TableLargerThanPool_ShouldBoundRetainedWorkspace(string operation)
    {
        // Arrange: file-backed rows exceed the fixed 1 MiB pool; a full row/key set
        // is observable through weak references after collections during row validation, tree construction
        // and the unique-validation cursor walk, before its workspace is released.
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
                var workspace = new List<(WeakReference<object> Reference, long Bytes)>();
                int observedRows = 0;
                int observedKeys = 0;
                // Deliberate unrelated retention: a process-heap delta must not decide
                // whether this statement retains its rows or keys.
                byte[]? unrelated = null;
                SqlPlanExecutor.RowDecoded = row =>
                {
                    if (row.Length < 2 || row[1] is not string text || text.Length != keyLength) { return; }
                    observedRows++;
                    workspace.Add((new(row), 4 * IntPtr.Size + row.Length * IntPtr.Size));
                    workspace.Add((new(text), 4 * IntPtr.Size + text.Length * sizeof(char)));
                };
                SqlPlanExecutor.IndexKeyBuilt = key =>
                {
                    if (key.Length < keyLength) { return; }
                    MemoryMarshal.TryGetArray(key.Encoded, out var bytes).ShouldBeTrue();
                    observedKeys++;
                    workspace.Add((new(bytes.Array!), 4 * IntPtr.Size + bytes.Array!.Length));
                };
                long peak = 0;
                int samples = 0;
                int cursorEntries = 0;
                int cursorSamples = 0;
                bool uniqueBuild = operation is not ("constraint" or "column");
                session.ProvisioningProgress = (processed, total) =>
                {
                    total.ShouldBe((long)rows);
                    if (processed == 1 || processed == rows / 2 || processed == rows)
                    {
                        Sample();
                    }
                };
                if (operation.StartsWith("inline-", StringComparison.Ordinal)) { session.ProvisioningProgress = null; }
                SqlPlanExecutor.UniqueIndexEntryValidated = () =>
                {
                    cursorEntries++;
                    if (cursorEntries == 1 || cursorEntries == rows / 2 || cursorEntries == rows)
                    {
                        Sample();
                        cursorSamples++;
                    }
                };

                // Act
                try
                {
                    await session.ExecuteAsync(operation switch
                    {
                        "constraint" => "ALTER TABLE t ADD CONSTRAINT ck CHECK (LENGTH(k) > 0)",
                        "column" => "ALTER TABLE t ADD COLUMN v INT NOT NULL DEFAULT 7",
                        "index" or "inline-index" => "CREATE UNIQUE INDEX ix ON t(k)",
                        _ => "ALTER TABLE t ADD CONSTRAINT ix UNIQUE(k)",
                    }, cancellationToken: TestTimeout.Token(300));
                }
                finally
                {
                    SqlPlanExecutor.UniqueIndexEntryValidated = null;
                    SqlPlanExecutor.RowDecoded = null;
                    SqlPlanExecutor.IndexKeyBuilt = null;
                    session.ProvisioningProgress = null;
                }
                GC.KeepAlive(unrelated);
                minimum = Math.Min(minimum, peak);
                _output.WriteLine($"{operation} round {round}: retained workspace={peak:N0} bytes; samples={samples}; cursor entries={cursorEntries}; cursor samples={cursorSamples}");

                // Assert: prove sampling occurs inside the cursor walk, including its
                // midpoint and final key, while an accidentally retained key list is live.
                samples.ShouldBeGreaterThanOrEqualTo(2);
                observedRows.ShouldBeGreaterThanOrEqualTo(rows);
                observedKeys.ShouldBeGreaterThanOrEqualTo(uniqueBuild ? rows * 2 : 0);
                cursorEntries.ShouldBe(uniqueBuild ? rows : 0);
                cursorSamples.ShouldBe(uniqueBuild ? 3 : 0);
                peak.ShouldBeLessThan((long)rows * keyLength * sizeof(char) / 4);

                void Sample()
                {
                    unrelated ??= new byte[8 * 1024 * 1024];
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    // Charge the whole configured pool, including bytes present before DDL.
                    peak = Math.Max(peak, 1024 * 1024 + RetainedBytes(workspace));
                    samples++;
                }
            }

            long decodedTableBytes = (long)rows * keyLength * sizeof(char);
            _output.WriteLine($"{operation} minimum of 3: {minimum:N0} / {decodedTableBytes:N0} bytes ({(double)minimum / decodedTableBytes:P2})");
            minimum.ShouldBeLessThan(decodedTableBytes / 4,
                "retained workspace must fit below one quarter of the decoded table, including the entire configured buffer pool");
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

#if !DEBUG
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

#endif

    // Keep TryGetTarget's temporary strong references out of the next collection's stack.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RetainedBytes(List<(WeakReference<object> Reference, long Bytes)> workspace)
    {
        long retained = 0;
        foreach (var allocation in workspace)
        {
            if (allocation.Reference.TryGetTarget(out _)) { retained += allocation.Bytes; }
        }
        return retained;
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
