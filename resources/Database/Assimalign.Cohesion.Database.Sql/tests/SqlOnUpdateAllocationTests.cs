#if !DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SqlIdentityConcurrency")]
public sealed class SqlOnUpdateAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlOnUpdateAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordinary writes: preserve pre-action Release allocations on nonempty paths (#1723)")]
    [InlineData("INSERT INTO t VALUES(1,1) ON CONFLICT(id) DO UPDATE SET v=excluded.v", "", 1561584L)]
    [InlineData("INSERT INTO p VALUES(2,2,2) ON CONFLICT(id) DO UPDATE SET k=excluded.k", "DELETE FROM p WHERE id=2", 1636336L)]
    [InlineData("DELETE FROM t WHERE id=1", "INSERT INTO t VALUES(1,1)", 1389016L)]
    [InlineData("UPDATE p SET v=v WHERE id=1", "", 2101792L)]
    public async Task ExecuteAsync_NoActionCanFire_ShouldKeepBaseAllocations(string sql, string reset, long baseline)
    {
        // Arrange: reset outside each measurement so every request performs its advertised write.
        await using var engine = SqlDatabaseEngine.Create("ordinary-write-allocation", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,v INT)");
        await session.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY,k INT UNIQUE,v INT)");
        await session.ExecuteAsync("CREATE TABLE c(k INT REFERENCES p(k) ON UPDATE CASCADE)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1,1)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1,1)");
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql(sql);
        var cleanup = reset.Length == 0 ? null : SqlQueryRequest.FromSql(reset);
        Measure(128);

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 5; round++) { minimum = Math.Min(minimum, Measure(64)); }

        // Assert
        _output.WriteLine($"{sql}: {minimum} B/64 = {minimum / 64.0:F4} B/request");
        minimum.ShouldBeLessThanOrEqualTo(baseline);

        long Measure(int count)
        {
            long bytes = 0;
            for (int index = 0; index < count; index++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("Allocation probe suspended."); }
                _ = pending.Result;
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                if (cleanup is not null) { _ = session.ExecuteAsync(cleanup, CancellationToken.None).GetAwaiter().GetResult(); }
            }
            return bytes;
        }
    }
}
#endif
