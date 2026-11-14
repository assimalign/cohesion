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
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task ExecuteAsync_WindowFittingAllocations_ShouldMatchBase(bool file, bool explicitTemp, bool application)
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
        var request = SqlQueryRequest.FromSql("SELECT id,ROW_NUMBER() OVER(PARTITION BY id%10 ORDER BY id),SUM(id) OVER(PARTITION BY id%10 ORDER BY id ROWS UNBOUNDED PRECEDING) FROM alloc");
        var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(request.Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        long minimum = long.MaxValue;
        try
        {
            Measure(100);
            // Act: independent rounds; no output or records inside the measured interval.
            for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(1000)); }
            // Assert
            string key = $"{RuntimeInformation.ProcessArchitecture}-{file}-{explicitTemp}-{application}.txt";
            _output.WriteLine($"WINDOW_FITTING_ALLOC|key={key}|statements=1000|bytes={minimum}");
            if (Environment.GetEnvironmentVariable("SQ_L4B_ALLOCATION_RECORD") is { } record)
            { Directory.CreateDirectory(record); await File.WriteAllTextAsync(Path.Combine(record, key), minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (Environment.GetEnvironmentVariable("SQ_L4B_ALLOCATION_BASELINE") is { } baseline)
            { minimum.ShouldBe(long.Parse(await File.ReadAllTextAsync(Path.Combine(baseline, key)), System.Globalization.CultureInfo.InvariantCulture)); }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }

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
}
#endif
