using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlBooleanPredicateTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean predicates: every clause rejects known non-Boolean types before reading rows (#1190)")]
    [InlineData("SELECT id FROM t WHERE a", "WHERE")]
    [InlineData("SELECT id FROM t WHERE a + 1", "WHERE")]
    [InlineData("SELECT id FROM t WHERE CASE WHEN TRUE THEN a ELSE 0 END", "WHERE")]
    [InlineData("SELECT id FROM t WHERE @p", "WHERE")]
    [InlineData("SELECT id FROM t WHERE CAST(NULL AS INT)", "WHERE")]
    [InlineData("SELECT COUNT(*) FROM t WHERE a", "WHERE")]
    [InlineData("UPDATE t SET a = a WHERE a", "WHERE")]
    [InlineData("DELETE FROM t WHERE @p", "WHERE")]
    [InlineData("SELECT t.id FROM t JOIN t u ON t.a", "JOIN ON")]
    [InlineData("SELECT COUNT(*) FROM t HAVING SUM(a)", "HAVING")]
    [InlineData("SELECT a FROM t GROUP BY a HAVING a", "HAVING")]
    [InlineData("INSERT INTO t VALUES(1,2) ON CONFLICT(id) DO UPDATE SET a = excluded.a WHERE excluded.a", "ON CONFLICT DO UPDATE WHERE")]
    [InlineData("SELECT CASE WHEN a THEN 1 ELSE 2 END FROM t", "CASE WHEN")]
    [InlineData("UPDATE t SET a = CASE WHEN a THEN 1 END WHERE id = 1", "CASE WHEN")]
    [InlineData("SELECT FALSE AND a FROM t", "AND")]
    [InlineData("SELECT TRUE OR a FROM t", "OR")]
    [InlineData("SELECT NOT a FROM t", "NOT")]
    [InlineData("SELECT a AS n FROM t ORDER BY n AND TRUE", "AND")]
    [InlineData("SELECT a AS n FROM t ORDER BY CASE WHEN n THEN 1 END", "CASE WHEN")]
    [InlineData("SELECT 1 WHERE 1", "WHERE")]
    [InlineData("SELECT (SELECT 1 WHERE 1)", "WHERE")]
    [InlineData("WITH bad AS (SELECT 1 WHERE 1) SELECT 2", "WHERE")]
    public async Task ExecuteAsync_NonBooleanPredicate_ShouldFailBeforeReadingRows(string sql, string clause)
    {
        foreach (bool populated in new[] { false, true })
        {
            // Arrange
            await using var engine = SqlDatabaseEngine.Create("boolean-predicate", new());
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, a INT)", cancellationToken: CancellationToken.None);
            if (populated) { await session.ExecuteAsync("INSERT INTO t VALUES(1,1)", cancellationToken: CancellationToken.None); }
            await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
            var parameters = new Dictionary<string, object?> { ["p"] = "text" };

            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(sql, parameters, CancellationToken.None));

            // Assert
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("COHSQLE003");
            diagnostic.Start.ShouldNotBeNull();
            failure.Message.ShouldContain(clause, Case.Sensitive);
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
            (await SqlConstantSelectTests.RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(populated ? 1L : 0L);
            await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean predicates: NULL remains UNKNOWN and simple CASE retains value comparison (#1190)")]
    [InlineData("SELECT NULL AND TRUE, NULL OR FALSE, NOT NULL, CASE WHEN NULL THEN 1 END", null, null, null, null)]
    [InlineData("SELECT NULL AND FALSE, NULL OR TRUE, CASE 1 WHEN 1 THEN 2 END, CASE WHEN TRUE THEN 3 END", false, true, 2, 3)]
    public async Task ExecuteAsync_ValidBooleanPositions_ShouldPreserveThreeValuedLogic(string sql, object? a, object? b, object? c, object? d)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("boolean-unknown", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await SqlConstantSelectTests.RowsAsync(session, sql);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new[] { a, b, c, d });
        (await SqlConstantSelectTests.RowsAsync(session, "SELECT 1 WHERE NULL")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean predicates: supplied Boolean and NULL parameters retain their truth values (#1190)")]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    [InlineData(null, 0)]
    public async Task ExecuteAsync_BooleanParameter_ShouldRetainTruthValue(bool? value, int expectedRows)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("boolean-parameter", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["p"] = value };

        // Act
        await using var result = (await session.ExecuteAsync("SELECT 1 WHERE @p", parameters, CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }

        // Assert
        count.ShouldBe(expectedRows);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean predicates: runtime backstops reject untyped values (#1190)")]
    [InlineData("predicate")]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("CASE WHEN")]
    public void Evaluate_NonBooleanRuntimeValue_ShouldRaiseOperandDiagnostic(string position)
    {
        // Arrange
        var evaluator = SqlExpressionEvaluator.ForExecution(null, CancellationToken.None);
        var invalid = new SqlBoundConstant(1);
        SqlBoundExpression expression = position switch
        {
            "AND" => new SqlBoundLogical(false, [invalid, new SqlBoundConstant(true)]),
            "OR" => new SqlBoundLogical(true, [invalid, new SqlBoundConstant(false)]),
            "CASE WHEN" => new SqlBoundCase(null, [new SqlBoundWhen(invalid, new SqlBoundConstant(2), default)], null),
            _ => invalid,
        };

        // Act
        var failure = Should.Throw<DatabaseException>(() => evaluator.Matches(expression, []));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE003");
        failure.Message.ShouldContain(position, Case.Sensitive);
    }
}
