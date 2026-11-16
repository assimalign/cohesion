using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectWideMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: the headline reaches its exact default-grant row boundary (#1776)")]
    [InlineData(false, 67108864L)]
    [InlineData(true, 67108864L)]
    [InlineData(false, 67108776L)]
    [InlineData(true, 67108776L)]
    [InlineData(false, 67108775L)]
    [InlineData(true, 67108775L)]
    public async Task ExecuteAsync_HeadlineDefaultBoundary_ShouldRefuseNextRow(bool forceBase, long budget)
    {
        // Arrange: one payload + map entry is 2,097,240 B; each output/key entry adds 136 B.
        // 478,026 entries cost 67,108,776 B, leaving 88 B of the default grant.
        const int fits = 478026;
        var builder = SqlDatabaseEngine.CreateBuilder("headline-boundary");
        builder.Options.StatementMemoryBudget = budget;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        for (int start = 0; start < fits + 1; start += 4096)
        {
            int length = Math.Min(4096, fits + 1 - start);
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(start, length).Select(k => $"({k})")), cancellationToken: CancellationToken.None);
        }
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        var parameters = new Dictionary<string, object?> { ["p"] = new string('x', 1048576) };

        // Act / Assert
        if (budget >= 67108776)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT @p AS p FROM t WHERE k<478026 ORDER BY k,p", parameters, CancellationToken.None);
            ((SqlMaterializedResultSet)result).Rows.Count.ShouldBe(fits);
            session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(67108776);
            session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT @p AS p FROM t ORDER BY k,p", parameters, CancellationToken.None));
        error.Diagnostics[0].Code.ShouldBe("CS05067");
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        metrics.PeakHashMemoryBytes.ShouldBe(budget < 67108776 ? 67108640 : 67108776);
        metrics.RecordsExamined.ShouldBe(budget < 67108776 ? fits : fits + 1);
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
        _output.WriteLine($"HEADLINE_BOUNDARY|base={forceBase}|budget={budget}|peak={metrics.PeakHashMemoryBytes}|records={metrics.RecordsExamined}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: vector identity bookkeeping allocates once regardless of repeated references (#1776)")]
    public void VectorBytes_RepeatedWideValue_ShouldAllocateConstantMap()
    {
        // Arrange
        string value = new('x', 113);
        object?[] small = Enumerable.Repeat<object?>(value, 1000).ToArray(), large = Enumerable.Repeat<object?>(value, 10000).ToArray();
        Measure(small); Measure(large);

        // Act
        long first = long.MaxValue, second = long.MaxValue;
        for (int round = 0; round < 3; round++) { first = Math.Min(first, Measure(small)); second = Math.Min(second, Measure(large)); }

        // Assert
        first.ShouldBe(second);
        SqlSelectMemory.VectorBytes(small).ShouldBe(8376);
        SqlSelectMemory.VectorBytes(large).ShouldBe(80376);
        _output.WriteLine($"WIDE_VECTOR_ALLOCATION|1000={first}|10000={second}");

        static long Measure(object?[] values)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            SqlSelectMemory.VectorBytes(values);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
