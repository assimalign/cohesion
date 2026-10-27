using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlMergeSourceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: UPDATE FROM source forms bind before and after rendering (#1715)")]
    [InlineData("s")]
    [InlineData("s AS b")]
    [InlineData("source_view AS b")]
    [InlineData("(SELECT id,v FROM s) AS b")]
    [InlineData("(SELECT id,v FROM s) AS b(k,value)")]
    [InlineData("INFORMATION_SCHEMA.TABLES AS b")]
    [InlineData("s AS b JOIN s AS c ON b.id=c.id")]
    [InlineData("s AS b LEFT JOIN s AS c USING(id)")]
    [InlineData("s AS b CROSS JOIN s AS c")]
    [InlineData("s AS b JOIN s AS c ON b.id=c.id CROSS JOIN s AS d")]
    [InlineData("s AS b CROSS JOIN s AS c JOIN s AS d ON c.id=d.id")]
    public async Task Plan_UpdateSourceForms_ShouldBindAndRoundTripAsync(string source)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-source-parity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s(id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW source_view AS SELECT id,v FROM s", cancellationToken: CancellationToken.None);
        string[] statements = ["UPDATE t SET v=1 FROM " + source + " WHERE TRUE RETURNING t.id",
            "MERGE INTO t USING " + source + " ON TRUE WHEN MATCHED THEN DO NOTHING RETURNING t.id"];

        foreach (string sql in statements)
        {
            // Act
            var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
            string rendered = Render(parsed.SqlExpression);
            var reparsed = (SqlQueryStatement)new SqlQueryParser().Parse(rendered);
            foreach (string spelling in new[] { sql, rendered })
            {
                await using var explanation = (await session.ExecuteAsync("EXPLAIN " + spelling, cancellationToken: CancellationToken.None))
                    .ShouldBeAssignableTo<DatabaseResultSet>();
                var operators = new List<string?>();
                await foreach (var row in explanation.GetRowsAsync(CancellationToken.None)) { operators.Add(row.GetString(2)); }

                // Assert
                operators.ShouldContain(sql.StartsWith("MERGE") ? "Merge" : "Update");
            }
            parsed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            reparsed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Render(reparsed.SqlExpression).ShouldBe(rendered);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: unsupported read sources retain UPDATE FROM parser codes (#1715)")]
    [InlineData("(VALUES (1,2)) AS s(id,v)")]
    [InlineData("LATERAL (SELECT 1 AS id,2 AS v) AS s")]
    [InlineData("(SELECT 1 AS id,2 AS v)")]
    [InlineData("generate_series(1,2) AS s")]
    [InlineData("s AS b NATURAL JOIN s AS c")]
    public void Parse_UnsupportedSources_ShouldMatchUpdateCodes(string source)
    {
        // Arrange
        string update = "UPDATE t SET v=1 FROM " + source + " WHERE TRUE";
        string merge = "MERGE INTO t USING " + source + " ON TRUE WHEN MATCHED THEN DELETE";

        // Act
        var updateCodes = new SqlQueryParser().Parse(update).Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Code).Distinct().ToArray();
        var mergeCodes = new SqlQueryParser().Parse(merge).Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Code).Distinct().ToArray();

        // Assert
        updateCodes.ShouldNotBeEmpty();
        mergeCodes.ShouldBe(updateCodes);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: invalid source binding retains UPDATE FROM codes after rendering (#1715)")]
    [InlineData("absent AS b", "CS01014")]
    [InlineData("(SELECT t.id,t.v) AS b", "CS01015")]
    public async Task Plan_InvalidSources_ShouldMatchUpdateCodesAsync(string source, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-source-invalid", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT,v INT)", cancellationToken: CancellationToken.None);
        string[] statements = ["UPDATE t SET v=1 FROM " + source + " WHERE TRUE",
            "MERGE INTO t USING " + source + " ON TRUE WHEN MATCHED THEN DELETE"];

        foreach (string sql in statements)
        {
            var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
            string rendered = Render(parsed.SqlExpression);
            var reparsed = (SqlQueryStatement)new SqlQueryParser().Parse(rendered);
            foreach (string spelling in new[] { sql, rendered })
            {
                // Act
                var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                    await session.ExecuteAsync("EXPLAIN " + spelling, cancellationToken: CancellationToken.None));

                // Assert
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            }
            reparsed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Render(reparsed.SqlExpression).ShouldBe(rendered);
        }
    }

    private static string Render(SqlQueryExpression expression)
        => expression is SqlMergeExpression merge ? SqlExpressionRenderer.Render(merge)
            : SqlExpressionRenderer.Render(expression.ShouldBeOfType<SqlUpdateExpression>());
}
