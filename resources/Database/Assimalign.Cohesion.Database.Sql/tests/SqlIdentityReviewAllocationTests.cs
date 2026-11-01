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
public sealed class SqlIdentityReviewAllocationTests
{
    private readonly ITestOutputHelper _output;
    public SqlIdentityReviewAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordinary DML: retain pre-identity Release allocations")]
    [InlineData("INSERT INTO plain VALUES(1)", 534296L)]
    [InlineData("UPDATE t SET v=2 WHERE id=-1", 826368L)]
    [InlineData("DELETE FROM t WHERE id=-1", 789504L)]
    [InlineData("MERGE INTO t USING (SELECT 1 AS id) s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=2", 1815040L)]
    public async Task ExecuteAsync_SequenceFreeDml_ShouldRetainBaseAllocations(string sql, long maximum)
    {
        // Arrange: an FK-referenced parent exercises the former unconditional closure walk.
        await using var engine = SqlDatabaseEngine.Create("ordinary-allocation-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE plain(v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE child(id INT REFERENCES t(id))", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql(sql);
        Measure(128);
        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 5; round++) { minimum = Math.Min(minimum, Measure(64)); }
        // Assert
        _output.WriteLine($"{sql}: {minimum} B/64 = {minimum / 64.0:F4} B/request");
        minimum.ShouldBeLessThanOrEqualTo(maximum);

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("Measured request suspended."); }
                _ = pending.Result;
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
#endif
