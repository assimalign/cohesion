#if !DEBUG
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlCorrelatedRetentionAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlCorrelatedRetentionAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT retention: correlated scalar and EXISTS retain reference thread allocations (#1776)")]
    [InlineData("SELECT k,(SELECT SUM(id+1) FROM u WHERE u.k=t.k) FROM t", 82_442_368L)]
    [InlineData("SELECT k,EXISTS(SELECT id+1 FROM u WHERE u.k=t.k) FROM t", 60_426_368L)]
    public async Task ExecuteAsync_CorrelatedBodies_ShouldKeepReferenceAllocations(string sql, long maximum)
    {
        // Arrange: identical test/filter at cc605c78d and head; synchronous request/disposal
        // on one measuring thread excludes workers and process-wide test-runner allocation.
        await using var engine = SqlDatabaseEngine.Create("correlated-retention-allocation", new() { TempDirectory = SqlAllocationTemp.Directory });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE u(id INT,k INT)", cancellationToken: CancellationToken.None);
        string insert = "INSERT INTO t VALUES" + string.Join(",", Enumerable.Repeat("(1)", 1000));
        await session.ExecuteAsync(insert, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO u VALUES(1,1),(2,1),(3,1),(4,1)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql(sql);
        Measure(32);

        // Act
        long small = Minimum();
        await session.ExecuteAsync(insert, cancellationToken: CancellationToken.None);
        Measure(32);
        long large = Minimum();

        // Assert: Release .NET 10.0.13 win-arm64; scalar pins SQ-L1a (2fabbf737), EXISTS keeps cc605c78d.
        // The increment excludes cache setup and catches walks allocating per correlated invocation.
        // Both sizes use the same query and thread.
        long increment = large - small;
        _output.WriteLine($"{sql}; thread bytes/16: 1000={small}, 2000={large}, increment={increment}; maximum={maximum}");
        increment.ShouldBeLessThanOrEqualTo(maximum);

        long Minimum()
        {
            long minimum = long.MaxValue;
            for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(16)); }
            return minimum;
        }

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException(); }
                var rows = (DatabaseResultSet)pending.Result;
                var disposal = rows.DisposeAsync();
                if (!disposal.IsCompletedSuccessfully) { throw new InvalidOperationException(); }
                disposal.GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
#endif
