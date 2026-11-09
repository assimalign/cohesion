#if !DEBUG
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlHashFittingAllocationTests
{
    private readonly ITestOutputHelper _output;
    public SqlHashFittingAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Fitting hash paths: original allocations and corrected state charges with and without temp space (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FittingHashPaths_ShouldKeepBaseAllocations(bool temp)
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "sql-hash-alloc-" + Guid.NewGuid().ToString("N"));
        var builder = SqlDatabaseEngine.CreateBuilder("hash-fitting-alloc");
        if (temp) { builder.Options.TempDirectory = directory; }
        builder.Functions.Add(SqlScalarFunction.Create("identity_value", (long value) => value));
        try
        {
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE t(a INT,b INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO t VALUES(1,2),(1,3),(2,4)", cancellationToken: CancellationToken.None);
            await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            string[] sql =
            [
                "SELECT a,COUNT(*),SUM(b) FROM t GROUP BY a ORDER BY a",
                "SELECT identity_value(a),COUNT(*) FROM t GROUP BY identity_value(a)",
                "SELECT a,BOOL_AND(b>0) FROM t GROUP BY a",
                "SELECT DISTINCT a FROM t",
            ];
            long[] originalBytes = [12128000, 11648000, 11216000, 5168000];
            long[] correctedPeaks = [1472, 1384, 1384, 448];
            for (int index = 0; index < sql.Length; index++)
            {
                string statement = sql[index];
                var request = SqlQueryRequest.FromSql(statement); Measure(256);
                // Act
                long minimum = long.MaxValue;
                for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(1000)); }
                // Assert: c150bee5a allocations, .NET 10.0.13, ARM64 and x64, with both temp configurations.
                minimum.ShouldBe(originalBytes[index]);
                session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(correctedPeaks[index]);
                _output.WriteLine($"FITTING_ALLOC|temp={temp}|sql={statement}|bytes={minimum}|peak={session.LastStatementMetrics!.PeakHashMemoryBytes}");
                session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);

                long Measure(int count)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < count; i++)
                    {
                        var pending = session.ExecuteAsync(request, CancellationToken.None); pending.IsCompletedSuccessfully.ShouldBeTrue();
                        var result = (DatabaseResultSet)pending.Result;
                        var disposal = result.DisposeAsync(); disposal.IsCompletedSuccessfully.ShouldBeTrue(); disposal.GetAwaiter().GetResult();
                    }
                    return GC.GetAllocatedBytesForCurrentThread() - before;
                }
            }
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }
}
#endif
