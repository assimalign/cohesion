using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlDmlSeekCollection
{
    public const string Name = "SQL DML seeks";
}

[Collection(SqlDmlSeekCollection.Name)]
public sealed class SqlDmlSeekCostTests
{
    private readonly ITestOutputHelper _output;

    public SqlDmlSeekCostTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DML seek: distinct-key cost stays bounded at 1k and 50k rows (#1274)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DistinctPrimaryKeys_ShouldHaveConstantRowVisitsAndBoundedCost(bool delete)
    {
        // Arrange: no incoming foreign keys, no repeated hot keys or purge worker.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var engine = SqlDatabaseEngine.Create("dml-seek-cost", new());
        var database = await engine.CreateDatabaseAsync("cost", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        foreach (int size in new[] { 1_000, 50_000 })
        {
            await session.ExecuteAsync($"CREATE TABLE t{size} (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
            for (int offset = 0; offset < size; offset += 500)
            {
                string values = string.Join(",", Enumerable.Range(offset, 500).Select(id => $"({id},0)"));
                await session.ExecuteAsync($"INSERT INTO t{size} VALUES {values}", cancellationToken: timeout.Token);
            }
        }

        // Act: warm both paths, then take the minimum of five rounds in this process.
        const int operations = 32;
        double small = double.MaxValue, large = double.MaxValue;
        long smallVisits = 0, largeVisits = 0;
        for (int round = -1; round < 5; round++)
        {
            foreach (int size in round % 2 == 0 ? new[] { 1_000, 50_000 } : new[] { 50_000, 1_000 })
            {
                long visits = 0;
                long started = Stopwatch.GetTimestamp();
                for (int operation = 0; operation < operations; operation++)
                {
                    int key = 100 + (round + 1) * operations + operation;
                    string sql = delete ? $"DELETE FROM t{size} WHERE id = {key}" : $"UPDATE t{size} SET v = 1 WHERE id = {key}";
                    var result = await session.ExecuteAsync(sql, cancellationToken: timeout.Token);
                    result.AffectedCount.ShouldBe(1);
                    visits += session.LastStatementMetrics.ShouldNotBeNull().RecordsExamined;
                }
                double micros = Stopwatch.GetElapsedTime(started).TotalMicroseconds / operations;
                if (round < 0) { continue; }
                if (size == 1_000) { small = Math.Min(small, micros); smallVisits = visits / operations; }
                else { large = Math.Min(large, micros); largeVisits = visits / operations; }
            }
        }

        // Assert: work is deterministic; elapsed time is only a same-process size ratio.
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{(delete ? "DELETE" : "UPDATE")}: 1k={small:F2} us/op, 50k={large:F2} us/op, ratio={large / small:F2}; visits/op={smallVisits}/{largeVisits}"));
        smallVisits.ShouldBe(1);
        largeVisits.ShouldBe(1);
        (large / small).ShouldBeLessThanOrEqualTo(2d);
    }
}
