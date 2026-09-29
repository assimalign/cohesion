using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Exercises unsupported correlation that can only be identified with catalog column metadata.</summary>
public sealed class SqlSubqueryBindingDiagnosticTests
{
    /// <summary>Unqualified outer references return a typed language diagnostic before any row is examined.</summary>
    /// <param name="sql">A subquery referencing a column that belongs only to its outer query.</param>
    /// <param name="emptyOuter">Whether the outer relation contains no rows.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Subquery: unqualified correlation returns COHDBL001 for populated and empty inputs")]
    [InlineData("SELECT outer_key FROM outer_rows WHERE EXISTS (SELECT inner_key FROM inner_rows WHERE inner_key = outer_key);", false)]
    [InlineData("SELECT outer_key FROM outer_rows WHERE EXISTS (SELECT inner_key FROM inner_rows WHERE inner_key = outer_key);", true)]
    [InlineData("SELECT (SELECT outer_key FROM inner_rows) FROM outer_rows;", false)]
    [InlineData("SELECT (SELECT outer_key FROM inner_rows) FROM outer_rows;", true)]
    [InlineData("SELECT outer_key FROM outer_rows WHERE outer_key IN (SELECT outer_key FROM inner_rows);", false)]
    [InlineData("SELECT outer_key FROM outer_rows WHERE outer_key IN (SELECT outer_key FROM inner_rows);", true)]
    public async Task Execute_UnqualifiedOuterReference_ShouldReturnUnsupportedDiagnostic(string sql, bool emptyOuter)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "subquery-binding" });
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE outer_rows (outer_key INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE inner_rows (inner_key INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO inner_rows VALUES (1);", cancellationToken: CancellationToken.None);
        if (!emptyOuter)
        {
            await session.ExecuteAsync("INSERT INTO outer_rows VALUES (1);", cancellationToken: CancellationToken.None);
        }
        var statement = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldBeEmpty();

        // Act
        var result = await session.ExecuteAsync(new SqlQueryRequest(statement), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(QueryResultStatus.Error);
        result.AffectedCount.ShouldBe(0);
        (result is QueryResultSet).ShouldBeFalse();
        var diagnostic = result.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message.ShouldNotBeNull().ShouldContain("Correlated subqueries are not supported");
        diagnostic.Message.ShouldContain("outer_key");
        diagnostic.Message.ShouldContain("local FROM/JOIN scope");
    }
}
