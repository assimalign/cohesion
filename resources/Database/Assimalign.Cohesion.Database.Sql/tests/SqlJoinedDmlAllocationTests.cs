using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

#if !DEBUG
[Collection(SqlDmlSeekCollection.Name)]
public sealed class SqlJoinedDmlAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlJoinedDmlAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined memory: empty UPDATE FROM and ON CONFLICT retain their Release allocations (#1715, #1751)")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id=2 AND t.id=s.id", 26_208_000L)]
    [InlineData("INSERT INTO t VALUES(1,1) ON CONFLICT(id) DO NOTHING", 9_752_000L)]
    public async Task ExecuteAsync_NoRetainedRows_ShouldKeepBaseAllocations(string sql, long baseline)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("joined-memory-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string setup in new[] { "CREATE TABLE t(id INT PRIMARY KEY,v INT)", "CREATE TABLE s(id INT,v INT)", "INSERT INTO t VALUES(1,1)" })
        { await session.ExecuteAsync(setup, cancellationToken: CancellationToken.None); }
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql(sql);
        Measure();

        // Act
        long allocated = long.MaxValue;
        for (int round = 0; round < 3; round++) { allocated = Math.Min(allocated, Measure()); }
        _output.WriteLine($"{sql}; {RuntimeInformation.ProcessArchitecture}; bytes/1000={allocated}");

        // Assert
        // 98469eb9f, Release .NET 10.0.13 on win-arm64: exact bytes/1000, without headroom.
        allocated.ShouldBeLessThanOrEqualTo(baseline);

        long Measure()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("Allocation probe suspended."); }
                _ = pending.Result;
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
#endif
