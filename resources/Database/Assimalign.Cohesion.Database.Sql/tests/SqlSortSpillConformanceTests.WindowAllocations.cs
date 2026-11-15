#if !DEBUG
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Fitting window allocations match the batch-base binary with each engine/temp configuration (#1789)")]
    [InlineData(false, false, false, 0)] [InlineData(false, true, false, 0)]
    [InlineData(true, false, false, 0)] [InlineData(true, true, false, 0)]
    [InlineData(false, false, true, 0)] [InlineData(false, true, true, 0)]
    [InlineData(true, false, true, 0)] [InlineData(true, true, true, 0)]
    [InlineData(true, false, false, 1)] [InlineData(true, true, false, 1)]
    [InlineData(true, false, false, 2)] [InlineData(true, true, false, 2)]
    [InlineData(true, false, false, 3)] [InlineData(true, true, false, 3)]
    public async Task ExecuteAsync_WindowFittingAllocations_ShouldMatchBase(bool file, bool explicitTemp, bool application, int shape)
    {
        // Arrange: prepared, synchronous execution isolates statement allocations.
        var builder = SqlDatabaseEngine.CreateBuilder("window-fitting-alloc");
        if (application) { builder.Functions.Add(SqlScalarFunction.Create("fit_value", static (int value) => value)); }
        if (file) { builder.Options.RootPath = Path.Combine(_root, "allocation-database"); }
        if (explicitTemp) { builder.Options.TempDirectory = Path.Combine(builder.Options.RootPath ?? _root, ".allocation-temp"); }
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE alloc(id INT)");
        await session.ExecuteAsync("INSERT INTO alloc VALUES" + string.Join(',', System.Linq.Enumerable.Range(0, 100).Select(i => $"({i})")));
        await session.ExecuteAsync("CREATE TABLE other(id INT)"); await session.ExecuteAsync("INSERT INTO other VALUES(1)");
        string sql = shape switch
        {
            1 => "SELECT alloc.id,ROW_NUMBER() OVER(PARTITION BY alloc.id%10 ORDER BY alloc.id) FROM alloc CROSS JOIN other",
            2 => "SELECT id,COUNT(*),ROW_NUMBER() OVER(PARTITION BY id%10 ORDER BY COUNT(*)) FROM alloc GROUP BY id",
            3 => "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%10 ORDER BY id) AS n,SUM(id) OVER(PARTITION BY id%10 ORDER BY id ROWS UNBOUNDED PRECEDING) AS s FROM alloc",
            _ => "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%10 ORDER BY id),SUM(id) OVER(PARTITION BY id%10 ORDER BY id ROWS UNBOUNDED PRECEDING) FROM alloc",
        };
        if (shape == 3)
        {
            long ctasMinimum = long.MaxValue;
            await MeasureTableAsAsync(10);
            for (int round = 0; round < 3; round++) { ctasMinimum = Math.Min(ctasMinimum, await MeasureTableAsAsync(100)); }
            await CheckAsync(ctasMinimum, 100);
            return;
        }
        var request = SqlQueryRequest.FromSql(sql);
        var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(request.Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        long minimum = long.MaxValue;
        try
        {
            Measure(100);
            // Act: independent rounds; no output or records inside the measured interval.
            for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(1000)); }
            // Assert
            await CheckAsync(minimum, 1000);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }

        async Task CheckAsync(long measured, int count)
        {
            string key = $"{RuntimeInformation.ProcessArchitecture}-{file}-{explicitTemp}-{application}-{shape}.txt";
            _output.WriteLine($"WINDOW_FITTING_ALLOC|key={key}|statements={count}|bytes={measured}");
            if (Environment.GetEnvironmentVariable("SQ_L4B_ALLOCATION_RECORD") is { } record)
            { Directory.CreateDirectory(record); await File.WriteAllTextAsync(Path.Combine(record, key), measured.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (Environment.GetEnvironmentVariable("SQ_L4B_ALLOCATION_BASELINE") is { } baseline)
            { measured.ShouldBe(long.Parse(await File.ReadAllTextAsync(Path.Combine(baseline, key)), System.Globalization.CultureInfo.InvariantCulture)); }
            // Pins are recorded from 9973a4e36 on each architecture, including a file engine.
            // Recording a reference archive is the only mode that omits the committed guard.
            if (Environment.GetEnvironmentVariable("SQ_L4B_ALLOCATION_RECORD") is null)
            { measured.ShouldBeLessThanOrEqualTo(WindowAllocationPin(shape, RuntimeInformation.ProcessArchitecture)); }
        }

        async Task<long> MeasureTableAsAsync(int count)
        {
            // Selecting follows target binding; admission completes the source span.
            long sourceStart = 0, measured = 0;
            int sources = 0, sourceThread = 0, crossedThreads = 0;
            var perSource = new long[count];
            SqlPlanExecutor.TableAsStage = (stage, _) =>
            {
                if (stage != "Selecting") { return; }
                sourceThread = Environment.CurrentManagedThreadId;
                sourceStart = GC.GetAllocatedBytesForCurrentThread();
            };
            SqlPlanExecutor.TableAsSourceAdmitted = _ =>
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread() - sourceStart;
                if (sourceThread != Environment.CurrentManagedThreadId) { crossedThreads++; }
                perSource[sources++] = allocated; measured += allocated;
            };
            try
            {
                for (int i = 0; i < count; i++)
                {
                    await session.ExecuteAsync("CREATE TABLE alloc_copy AS " + sql);
                    await session.ExecuteAsync("DROP TABLE alloc_copy");
                }
            }
            finally { SqlPlanExecutor.TableAsStage = null; SqlPlanExecutor.TableAsSourceAdmitted = null; }
            if (Environment.GetEnvironmentVariable("SQ_L4B_ALLOCATION_DETAIL") is not null)
            { _output.WriteLine($"WINDOW_CTAS_ALLOC|count={count}|crossed_threads={crossedThreads}|bytes={string.Join(',', perSource)}"); }
            sources.ShouldBe(count); crossedThreads.ShouldBe(0);
            return measured;
        }

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var context = new SqlStatementContext(transaction, database.Coordinator).WithSession(session);
                var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
                var pending = executor.ExecuteRootAsync(plan, context, CancellationToken.None, session);
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("Measured window did not complete synchronously."); }
                var result = (DatabaseResultSet)pending.GetAwaiter().GetResult();
                var dispose = result.DisposeAsync(); if (!dispose.IsCompletedSuccessfully) { throw new InvalidOperationException("Measured window did not dispose synchronously."); } dispose.GetAwaiter().GetResult();
                if (context.Metrics.HashMemoryBytes != 0) { throw new InvalidOperationException("Measured window retained memory."); }
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    private static long WindowAllocationPin(int shape, Architecture architecture) => (shape, architecture) switch
    {
        (0, Architecture.Arm64 or Architecture.X64) => 172712000,
        (1, Architecture.Arm64 or Architecture.X64) => 90560000,
        (2, Architecture.Arm64 or Architecture.X64) => 145160000,
        (3, Architecture.Arm64 or Architecture.X64) => 17281600,
        _ => throw new InvalidOperationException("Record the reference allocation pin for this architecture and shape."),
    };
}
#endif
