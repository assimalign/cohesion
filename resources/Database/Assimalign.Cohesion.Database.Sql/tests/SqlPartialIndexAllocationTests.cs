#if !DEBUG
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlPartialIndexAllocationTests
{
    private readonly ITestOutputHelper _output;
    public SqlPartialIndexAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial indexes: bulk preparation reuses statement evaluation and membership storage (#1630)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BulkPreparation_ShouldBoundPerRowOverhead(bool update)
    {
        // Arrange
        const int rows = 512;
        await using var engine = SqlDatabaseEngine.Create("partial-bulk-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "plain", "parts" })
        {
            await SqlPartialIndexTests.Execute(session, $"CREATE TABLE {table}(v INT,active BOOLEAN)");
            await SqlPartialIndexTests.Execute(session, $"CREATE INDEX ix ON {table}(v)" + (table == "parts" ? " WHERE active" : ""));
            await SqlPartialIndexTests.Execute(session, $"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Repeat("(1,TRUE)", rows)));
        }
        var plain = SqlQueryRequest.FromSql(update ? "UPDATE plain SET v=v+1" : "INSERT INTO plain SELECT v,active FROM plain");
        var parts = SqlQueryRequest.FromSql(update ? "UPDATE parts SET v=v+1" : "INSERT INTO parts SELECT v,active FROM plain");
        long Measure(SqlQueryRequest request)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            var pending = session.ExecuteAsync(request, CancellationToken.None);
            pending.IsCompletedSuccessfully.ShouldBeTrue();
            _ = pending.Result;
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        async Task<long> Round(SqlQueryRequest request)
        {
            await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            long allocated = Measure(request);
            await transaction.RollbackAsync(CancellationToken.None);
            return allocated;
        }
        await Round(plain);
        await Round(parts);

        // Act
        long ordinary = long.MaxValue, partial = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            ordinary = Math.Min(ordinary, await Round(plain));
            partial = Math.Min(partial, await Round(parts));
        }

        // Assert
        _output.WriteLine($"Bulk {(update ? "UPDATE" : "INSERT")}: ordinary {ordinary / (double)rows:F3}, partial {partial / (double)rows:F3}, overhead {(partial - ordinary) / (double)rows:F3} B/row");
        ((partial - ordinary) / (double)rows).ShouldBeLessThan(240);
    }
}
#endif
