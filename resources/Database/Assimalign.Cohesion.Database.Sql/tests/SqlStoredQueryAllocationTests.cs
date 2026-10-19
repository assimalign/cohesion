#if !DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

// Allocation budgets describe the optimized Release path, with tiering disabled by the test project.
[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlStoredQueryAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlStoredQueryAllocationTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Stored queries: ReadCommitted SELECT retains the pre-system-join allocation budget")]
    public async Task ExecuteAsync_StoredOnlyReadCommitted_ShouldRetainBaseAllocationsAsync()
    {
        // Arrange: base 3b720f405, Release, 1,000 warmups and the minimum of three
        // 5,000-query rounds: 21,466,480 bytes (4,293.296/query). Parsing and setup
        // stay outside measurement, as do the engine's worker-thread allocations.
        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        await using var engine = SqlDatabaseEngine.Create("allocation-regression", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql("SELECT a FROM t");
        const int iterations = 5_000;
        const long baseBytes = 21_466_480;
        Measure(1_000);

        // Act: every query and disposal must finish synchronously on the measuring thread.
        long allocated = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long sample = Measure(iterations);
            allocated = Math.Min(allocated, sample);
            _output.WriteLine($"Round {round + 1}: {sample / (double)iterations:F4} bytes/query.");
        }

        // Assert: an extra context, source, binding, or wrapper array exceeds the base budget.
        _output.WriteLine($"Base: {baseBytes / (double)iterations:F4}; new: {allocated / (double)iterations:F4} bytes/query.");
        allocated.ShouldBeLessThanOrEqualTo(baseBytes);
        session.LastStatementMetrics.ShouldNotBeNull().CatalogSnapshotCaptures.ShouldBe(0);
        session.LastStatementMetrics.CatalogSnapshot.ShouldBeNull();
        session.LastStatementMetrics.AccessPath.ShouldBe("scan");

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < count; index++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException("The measured statement did not complete synchronously.");
                }
                if (pending.Result is not DatabaseResultSet rows || rows.Status != DatabaseResultStatus.Success)
                {
                    throw new InvalidOperationException("The measured statement did not produce its result set.");
                }
                var dispose = rows.DisposeAsync();
                if (!dispose.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException("The measured result did not dispose synchronously.");
                }
                dispose.GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
#endif
