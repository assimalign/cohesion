using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCteExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: grouped outer aggregate retains recursive ownership")]
    [InlineData("SELECT CAST((SELECT SUM(r.n)) AS BIGINT) FROM r GROUP BY r.n")]
    [InlineData("SELECT r.n FROM r GROUP BY r.n HAVING (SELECT SUM(r.n)) > 0")]
    [InlineData("(SELECT r.n FROM r GROUP BY r.n ORDER BY (SELECT SUM(r.n)))")]
    public async Task Execute_ReviewGroupedRecursiveAggregate_ShouldReportRecursiveDiagnostic(string term)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-review-grouped", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await RecursiveDiagnosticAsync(session, $"WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL {term}) SELECT n FROM r;");

        // Assert
        error.Code.ShouldBe("CS01061");
        error.Start.ShouldNotBeNull();
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
    }
}
