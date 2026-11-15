#if !DEBUG
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSetInnerSpillTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Fitting set and nested-loop allocations equal the archived base on each architecture (#1789)")]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(3, false)] [InlineData(3, true)] [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(9, false)] [InlineData(9, true)]
    public async Task ExecuteAsync_FittingAllocations_ShouldMatchArchivedBase(int shape, bool temp)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("set-inner-alloc");
        if (temp) { builder.Options.TempDirectory = Path.Combine(_root, "allocation-temp"); }
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await SeedAsync(session, 64);
        string sql = shape switch
        {
            0 => "SELECT id FROM t UNION ALL SELECT id FROM t",
            1 => "SELECT id FROM t UNION SELECT id FROM t",
            3 => "SELECT id FROM t INTERSECT ALL SELECT id FROM t",
            4 => "SELECT id FROM t EXCEPT SELECT id FROM t WHERE id%3=0",
            _ => "SELECT o.id,t.id FROM o CROSS JOIN t",
        };
        var request = SqlQueryRequest.FromSql(sql);
        var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(request.Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            Measure(100);
            long measured = long.MaxValue;
            // Act
            for (int round = 0; round < 3; round++) { measured = Math.Min(measured, Measure(1000)); }
            // Assert
            string key = $"{RuntimeInformation.ProcessArchitecture}-{shape}-{temp}";
            _output.WriteLine($"SET_INNER_ALLOCATION|{key}|{measured}");
            if (Environment.GetEnvironmentVariable("SQ_L4C_RECORD") is { } directory)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "allocation-" + key + ".txt"), measured.ToString(CultureInfo.InvariantCulture));
            }
            else if (Environment.GetEnvironmentVariable("SQ_L4C_ALLOCATION_BASELINE") is { } baseline)
            { measured.ShouldBe(long.Parse(await File.ReadAllTextAsync(Path.Combine(baseline, "allocation-" + key + ".txt")), CultureInfo.InvariantCulture)); }
            if (Environment.GetEnvironmentVariable("SQ_L4C_RECORD") is null)
            {
                // Archived 1439d9ff6, .NET 10.0.13, independently measured on both runtimes.
                long expected = (RuntimeInformation.ProcessArchitecture, shape) switch
                {
                    (Architecture.Arm64 or Architecture.X64, 0) => 64504000,
                    (Architecture.Arm64 or Architecture.X64, 1) => 63744000,
                    (Architecture.Arm64 or Architecture.X64, 3) => 67272000,
                    (Architecture.Arm64 or Architecture.X64, 4) => 62496000,
                    (Architecture.Arm64 or Architecture.X64, 9) => 45904000,
                    _ => throw new InvalidOperationException("Record the archived base for this architecture."),
                };
                measured.ShouldBe(expected);
            }
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
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("Measured set/inner did not complete synchronously."); }
                var result = (DatabaseResultSet)pending.GetAwaiter().GetResult();
                var disposal = result.DisposeAsync();
                if (!disposal.IsCompletedSuccessfully) { throw new InvalidOperationException("Measured set/inner did not dispose synchronously."); }
                disposal.GetAwaiter().GetResult();
                if (context.Metrics.HashMemoryBytes != 0) { throw new InvalidOperationException("Measured set/inner retained memory."); }
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
#endif
