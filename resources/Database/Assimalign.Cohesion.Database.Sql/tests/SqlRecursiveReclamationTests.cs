using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlRecursiveReclamationTests
{
    private readonly ITestOutputHelper _output;
    public SqlRecursiveReclamationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive correlated predicates: pull until decisive and preserve UNKNOWN")]
    [InlineData("v IN", "n", "", true, 1)]
    [InlineData("v NOT IN", "n", "", false, 1)]
    [InlineData("v = ANY", "n", "", true, 1)]
    [InlineData("v = SOME", "n", "", true, 1)]
    [InlineData("v > ALL", "n", "", false, 1)]
    [InlineData("v IN", "n+1", "LIMIT 3", false, 3)]
    [InlineData("v NOT IN", "n+1", "LIMIT 3", true, 3)]
    [InlineData("v = ANY", "n+1", "LIMIT 3", false, 3)]
    [InlineData("v = SOME", "n+1", "LIMIT 3", false, 3)]
    [InlineData("v < ALL", "n+1", "LIMIT 3", true, 3)]
    [InlineData("v IN", "CAST(NULL AS BIGINT)", "LIMIT 3", null, 3)]
    [InlineData("v NOT IN", "CAST(NULL AS BIGINT)", "LIMIT 3", null, 3)]
    [InlineData("v = ANY", "CAST(NULL AS BIGINT)", "LIMIT 3", null, 3)]
    [InlineData("v = SOME", "CAST(NULL AS BIGINT)", "LIMIT 3", null, 3)]
    [InlineData("v > ALL", "CAST(NULL AS BIGINT)", "LIMIT 3", null, 3)]
    [InlineData("v IN", "CASE WHEN n=1 THEN CAST(NULL AS BIGINT) ELSE 1 END", "", true, 2)]
    [InlineData("v = ANY", "CASE WHEN n=1 THEN CAST(NULL AS BIGINT) ELSE 1 END", "", true, 2)]
    [InlineData("v = SOME", "CASE WHEN n=1 THEN CAST(NULL AS BIGINT) ELSE 1 END", "", true, 2)]
    [InlineData("v > ALL", "CASE WHEN n=1 THEN CAST(NULL AS BIGINT) ELSE 1 END", "", false, 2)]
    public async Task Execute_CorrelatedSet_ShouldStopAtDecision(string predicate, string value, string tail, bool? expected, long expectedRows)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-predicate", new() { StatementMemoryBudget = 32_768 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE vals(v BIGINT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO vals VALUES(1)", cancellationToken: CancellationToken.None);
        long produced = 0;
        SqlPlanExecutor.RecursiveProgress = (_, candidates, _) => produced = candidates;
        try
        {
            // Act
            var rows = await RowsAsync(session, $"WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT {predicate} (SELECT {value} FROM r WHERE n>=a.v {tail}) FROM vals a");

            // Assert
            rows.ShouldHaveSingleItem()[0].ShouldBe(expected);
            produced.ShouldBe(expectedRows);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive correlated slots: expired row results release their memory ownership")]
    [InlineData("EXISTS(SELECT n FROM r WHERE n=a.v)")]
    [InlineData("(SELECT n FROM r WHERE n=a.v) IS NOT NULL")]
    public async Task Execute_RepeatedCorrelation_ShouldReleaseExpiredValues(string predicate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-slots", new() { StatementMemoryBudget = 32_768 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE vals(v BIGINT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO vals VALUES " + string.Join(',', Enumerable.Repeat("(2)", 1000)), cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"WITH RECURSIVE r(n) AS (SELECT 1 UNION SELECT n FROM r) SELECT COUNT(*) FROM vals a WHERE {predicate}");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(0L);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThan(16_384);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive windows: finite inputs complete without a bound heuristic")]
    [InlineData("SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<100", 100)]
    [InlineData("SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<3", 3)]
    [InlineData("SELECT 1 UNION SELECT n FROM r", 1)]
    public async Task Execute_FiniteWindow_ShouldReturnEveryRow(string body, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-window-finite", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"WITH RECURSIVE r(n) AS ({body}) SELECT ROW_NUMBER() OVER() FROM r");

        // Assert
        rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(Enumerable.Range(1, count).Select(n => (long)n));
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive single-pass consumers: million-row counters and walks retain bounded state")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_MillionRows_ShouldReclaimWorkingAndConsumedRows(bool walk)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-million", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string term = walk ? "SELECT n+1,d+1 FROM r WHERE n<1000000" : "SELECT n+1 FROM r WHERE n<1000000";
        string declaration = walk ? "r(n,d) AS (SELECT 1,0" : "r(n) AS (SELECT 1";

        // Act
        var rows = await RowsAsync(session, $"WITH RECURSIVE {declaration} UNION ALL {term}) SELECT COUNT(*) FROM r");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1_000_000L);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThan(32_768);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        _output.WriteLine($"walk={walk}; rows=1000000; peak={session.LastStatementMetrics.PeakHashMemoryBytes}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive uneven readers: retain history for unopened readers then reclaim their consumed rows")]
    public async Task Execute_UnevenReaders_ShouldKeepEveryRowAndReleaseHistory()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-uneven", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<1000000) (SELECT COUNT(*),SUM(n) FROM (SELECT n FROM r LIMIT 10) d) UNION ALL (SELECT COUNT(*),SUM(n) FROM r)");

        // Assert
        rows.Select(row => row[0]).ShouldBe(new object?[] { 10L, 1_000_000L });
        rows.Select(row => row[1]).ShouldBe(new object?[] { 55L, 500_000_500_000L });
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThan(32_768);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive bounded state: infinite count and top-K window inputs cancel without retaining consumed history")]
    [InlineData("SELECT COUNT(*) FROM r")]
    [InlineData("SELECT ROW_NUMBER() OVER() FROM (SELECT n FROM r ORDER BY n LIMIT 10) d")]
    public async Task Execute_ConstantStateInfiniteInput_ShouldCancelAtRecordedProgress(string consumer)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-constant-state", new() { StatementMemoryBudget = 32_768 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        long produced = 0;
        SqlPlanExecutor.RecursiveProgress = (_, candidates, _) =>
        {
            produced = candidates;
            if (produced == 1000) { cancellation.Cancel(); }
        };
        try
        {
            // Act
            var error = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) " + consumer,
                cancellationToken: cancellation.Token));

            // Assert
            error.CancellationToken.ShouldBe(cancellation.Token);
            produced.ShouldBe(1000);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThan(16_384);
        }
        finally { SqlPlanExecutor.RecursiveProgress = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive preserved join reader: a million rows reclaim consumed history")]
    public async Task Execute_PreservedJoinReader_ShouldReclaimConsumedRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-preserved-reader", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<1000000) SELECT COUNT(*) FROM r LEFT JOIN (SELECT 1 AS v) d ON TRUE");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1_000_000L);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThan(32_768);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive view readers: reused plans count readers within each runtime scope")]
    public async Task Execute_ReusedViewScopes_ShouldReclaimEachSinglePassInput()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-view-readers", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW v AS WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<1000000) SELECT n FROM r", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "SELECT (SELECT COUNT(*) FROM v),(SELECT COUNT(*) FROM v)");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1_000_000L, 1_000_000L });
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThan(32_768);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
