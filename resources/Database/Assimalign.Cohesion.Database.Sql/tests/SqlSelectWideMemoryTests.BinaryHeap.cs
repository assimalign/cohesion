using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectWideMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: forced GC counts every retained binary clone and one output-key alias (#1776)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BinaryCloneHeap_ShouldReportMeasuredGrowth(bool forceBase)
    {
        // Arrange: each row owns a 1 KiB clone; its output and key share that clone.
        const long expected = 10223616, legacy = 18284544;
        var builder = SqlDatabaseEngine.CreateBuilder("binary-clone-heap");
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, 8192).Select(k => $"({k})")), cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        await Execute(new Dictionary<string, object?> { ["p"] = new byte[1024] });
        long largestGrowth = 0, observed = 0;
        Dictionary<string, object?>? parameters = null;

        // Act: warm first, then retain three fresh sets of clones and force GC at peak.
        for (int round = 0; round < 3; round++)
        {
            parameters = null;
            await session.ExecuteAsync("SELECT 0", cancellationToken: CancellationToken.None);
            long before = GC.GetTotalMemory(true);
            parameters = new Dictionary<string, object?> { ["p"] = new byte[1024] };
            bool captured = false;
            try
            {
                SqlStatementMetrics.AfterMemoryReservation = (metrics, _) =>
                {
                    if (metrics.HashMemoryBytes != expected && metrics.HashMemoryBytes != legacy) { return; }
                    captured = true;
                    observed = metrics.HashMemoryBytes;
                    largestGrowth = Math.Max(largestGrowth, GC.GetTotalMemory(true) - before);
                };
                await Execute(parameters);
            }
            finally { SqlStatementMetrics.AfterMemoryReservation = null; }
            captured.ShouldBeTrue();
        }

        // Assert
        _output.WriteLine($"BINARY_HEAP|base={forceBase}|charged={observed}|heap={largestGrowth}|ratio={observed / (double)Math.Max(1, largestGrowth):F6}");
        observed.ShouldBe(expected);
        largestGrowth.ShouldBeGreaterThan(0);
        GC.KeepAlive(parameters);

        async Task Execute(Dictionary<string, object?> arguments)
        {
            await using var rows = (SqlMaterializedResultSet)await session.ExecuteAsync("SELECT @p AS p FROM t ORDER BY k,p", arguments, CancellationToken.None);
            rows.Rows.Count.ShouldBe(8192);
            rows.ClearRows();
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
    }
}
