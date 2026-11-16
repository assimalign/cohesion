using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: grouped CHAR and NULL outputs retain base values and metadata (#1788)")]
    [InlineData("CASE WHEN TRUE THEN CAST('c' AS CHAR(4)) ELSE '11' END", DatabaseType.String, "c")]
    [InlineData("CASE WHEN FALSE THEN CAST('c' AS CHAR(4)) ELSE '11' END", DatabaseType.String, "11")]
    [InlineData("COALESCE(CAST('c' AS CHAR(4)), '11')", DatabaseType.String, "c")]
    [InlineData("COALESCE(CAST(NULL AS CHAR(4)), '11')", DatabaseType.String, "11")]
    [InlineData("CASE WHEN TRUE THEN '11' ELSE CAST('c' AS CHAR(4)) END", DatabaseType.String, "11")]
    [InlineData("CASE WHEN FALSE THEN '11' ELSE CAST('c' AS CHAR(4)) END", DatabaseType.String, "c")]
    [InlineData("COALESCE('11', CAST('c' AS CHAR(4)))", DatabaseType.String, "11")]
    [InlineData("CASE WHEN TRUE THEN NULL ELSE NULL END", DatabaseType.Null, null)]
    [InlineData("CASE WHEN FALSE THEN NULL ELSE NULL END", DatabaseType.Null, null)]
    [InlineData("COALESCE(NULL, NULL)", DatabaseType.Null, null)]
    public async Task Execute_GroupedCharacterAndNull_ShouldRetainExistingOutput(string expression, DatabaseType type, string? expected)
    {
        // Arrange: captured from 17e22bb08, including CHAR padding removal and Null metadata.
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT)");
        await Execute(session, "INSERT INTO t VALUES(1)");

        foreach (string grouping in new[] { "", " GROUP BY id", " GROUP BY ROLLUP(id)" })
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                $"SELECT {expression}, COUNT(*) FROM t{grouping}", cancellationToken: CancellationToken.None);

            // Assert
            result.Columns[0].Type.ShouldBe(type);
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { rows++; row.GetValue(0).ShouldBe(expected); row.GetValue(1).ShouldBe(1L); }
            rows.ShouldBe(grouping.Contains("ROLLUP", System.StringComparison.Ordinal) ? 2 : 1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: grouped function arguments retain base overload selection (#1788)")]
    public async Task Execute_GroupedFunctionArgument_ShouldRetainExistingOverload()
    {
        // Arrange: ordinary scalar calls use the new common type; grouped output keeps its contract.
        var builder = SqlDatabaseEngine.CreateBuilder("grouped-overloads");
        builder.Functions.Add(SqlScalarFunction.Create("kind", static (int value) => "integer", SqlFunctionVolatility.Immutable));
        builder.Functions.Add(SqlScalarFunction.Create("kind", static (decimal value) => "numeric", SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT kind(CASE WHEN TRUE THEN 1 ELSE 2.5 END), kind(COALESCE(1, 2.5)), COUNT(*)");

        // Assert
        rows.ShouldBe(new object?[][] { ["integer", "integer", 1L] });
        // The base also selects INTEGER when the selected value is Decimal, then
        // refuses that call. Keep this grouped-output limitation unchanged.
        foreach (string expression in new[] { "CASE WHEN FALSE THEN 1 ELSE 2.5 END", "COALESCE(NULL::INT, 2.5)" })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session,
                $"SELECT kind({expression}), COUNT(*)"));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05060");
        }
    }
}
