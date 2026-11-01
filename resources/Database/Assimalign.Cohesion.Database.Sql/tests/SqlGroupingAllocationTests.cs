#if !DEBUG
using System;
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
public sealed class SqlGroupingAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlGroupingAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping: plain keys and expression keys retain their base allocations (#1720)")]
    [InlineData("SELECT a,COUNT(*),SUM(b) FROM t GROUP BY a ORDER BY a", 62_360_000L)]
    [InlineData("SELECT a+1,COUNT(*),SUM(b) FROM t GROUP BY a+1 HAVING COUNT(*)>0 ORDER BY a+1", 90_560_000L)]
    public async Task ExecuteAsync_PlainGrouping_ShouldRetainBaseAllocations(string sql, long baseBytes)
    {
        // Arrange: the SQ-H2 baseline probe measured these budgets at both 46803a9ba
        // and 967254bbd: 1,000 warmups, then the minimum of three 5,000-query rounds.
        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        await using var engine = SqlDatabaseEngine.Create("group-alloc", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(a INT,b INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,2),(1,3),(2,4)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql(sql);
        Measure(1_000);

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(5_000)); }

        // Assert
        _output.WriteLine($"Plain grouping: {minimum} bytes / 5,000 queries; baseline {baseBytes}.");
        minimum.ShouldBeLessThanOrEqualTo(baseBytes);

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("The measured query did not complete synchronously."); }
                var rows = (DatabaseResultSet)pending.Result;
                var disposal = rows.DisposeAsync();
                if (!disposal.IsCompletedSuccessfully) { throw new InvalidOperationException("The measured disposal did not complete synchronously."); }
                disposal.GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
#endif
