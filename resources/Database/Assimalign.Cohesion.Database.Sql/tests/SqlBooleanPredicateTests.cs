using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlBooleanPredicateTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Integer literals: inferred and evaluated types agree (#1190)")]
    [InlineData("1", DatabaseType.Int32)]
    [InlineData("2147483648", DatabaseType.Int64)]
    public void Bind_IntegerLiteral_ShouldMatchItsStaticType(string text, DatabaseType type)
    {
        // Arrange
        var expression = ((SqlSelectExpression)SqlQueryRequest.FromSql($"SELECT {text}").Statement.SqlExpression).Columns[0].Expression;
        var evaluator = new SqlExpressionEvaluator([], null);

        // Act
        var value = evaluator.Evaluate(evaluator.Bind(expression), []);

        // Assert
        evaluator.StaticTypeOf(expression).ShouldBe(type);
        SqlValue.FromObject(value).Type.ShouldBe(type);
    }

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
    [InlineData("DELETE FROM t WHERE CASE WHEN TRUE THEN TRUE ELSE 1 END", "WHERE")]
    [InlineData("DELETE FROM t WHERE TRUE OR COALESCE(TRUE, 1)", "OR")]
    [InlineData("DELETE FROM t WHERE FALSE AND CASE WHEN TRUE THEN TRUE ELSE 1 END", "AND")]
    [InlineData("SELECT 1 FROM t WHERE COALESCE(TRUE, 1)", "WHERE")]
    [InlineData("SELECT 1 FROM t WHERE COALESCE(NULL, TRUE, 'invalid')", "WHERE")]
    [InlineData("SELECT 1 FROM t WHERE CASE 1 WHEN 1 THEN TRUE ELSE 1 END", "WHERE")]
    [InlineData("SELECT 1 FROM t WHERE 'invalid'", "WHERE")]
    [InlineData("SELECT 1 FROM t WHERE TRUE OR 'invalid'", "OR")]
    [InlineData("SELECT 1 FROM t WHERE CAST('true' AS TEXT)", "WHERE")]
    [InlineData("SELECT (CASE WHEN TRUE THEN TRUE ELSE 1 END) IS TRUE FROM t", "IS TRUE")]
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
            diagnostic.Code.ShouldBe("CS02003");
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

    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean predicates: supported Boolean sources and empty implicit groups execute (#1190)")]
    [InlineData("SELECT id FROM t WHERE flag", 1, false)]
    [InlineData("SELECT id FROM t WHERE domain_flag", 1, false)]
    [InlineData("SELECT id FROM t WHERE app_flag(id)", 1, false)]
    [InlineData("SELECT id FROM t WHERE (SELECT TRUE)", 1, false)]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT 1 WHERE t.flag)", 1, false)]
    [InlineData("SELECT 1 HAVING FALSE", 0, false)]
    [InlineData("SELECT SUM(1) WHERE FALSE", 1, true)]
    public async Task ExecuteAsync_BooleanSources_ShouldPreserveQuerySemantics(string sql, int count, bool nullResult)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("boolean-positive");
        builder.Functions.Add(SqlScalarFunction.Create("app_flag", static (int value) => value == 1));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN bool_domain AS BOOLEAN", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, flag BOOLEAN, domain_flag bool_domain)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, TRUE, TRUE)", cancellationToken: CancellationToken.None);

        // Act
        var rows = await SqlConstantSelectTests.RowsAsync(session, sql);

        // Assert
        rows.Count.ShouldBe(count);
        if (count != 0)
        {
            if (nullResult) { rows[0][0].ShouldBeNull(); }
            else { Convert.ToInt64(rows[0][0]).ShouldBe(1); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean predicates: Boolean input strings coerce only in literal positions (#1190)")]
    [InlineData("t", true)]
    [InlineData("TrUe", true)]
    [InlineData("f", false)]
    [InlineData("FaLsE", false)]
    [InlineData("YeS", true)]
    [InlineData("nO", false)]
    [InlineData("oN", true)]
    [InlineData("OfF", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("Y", true)]
    [InlineData("N", false)]
    public async Task ExecuteAsync_BooleanStringLiteral_ShouldCoerceInBooleanPositions(string literal, bool expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("boolean-literal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await SqlConstantSelectTests.RowsAsync(session,
            $"SELECT '{literal}' AND TRUE, FALSE OR '{literal}', NOT '{literal}', CASE WHEN '{literal}' THEN TRUE ELSE FALSE END, '{literal}' IS TRUE");
        var filtered = await SqlConstantSelectTests.RowsAsync(session,
            $"SELECT 1 WHERE COALESCE(NULL, CASE WHEN TRUE THEN '{literal}' ELSE 'false' END) IS TRUE");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { expected, expected, !expected, expected, expected });
        filtered.Count.ShouldBe(expected ? 1 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CHECK: all non-Boolean roots use CS02003 (#1190)")]
    [InlineData("CREATE TABLE t (a INT CHECK (a))")]
    [InlineData("CREATE TABLE t (a INT CHECK (a AND TRUE))")]
    [InlineData("CREATE TABLE t (a INT CHECK (NOT a))")]
    [InlineData("CREATE TABLE t (a INT CHECK (COALESCE(TRUE, a)))")]
    [InlineData("CREATE DOMAIN d AS INT CHECK (VALUE)")]
    [InlineData("CREATE DOMAIN d AS INT CHECK (VALUE OR FALSE)")]
    public async Task ExecuteAsync_NonBooleanCheck_ShouldUseOperandDiagnostic(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("boolean-check", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02003");
        failure.Diagnostics[0].Start.ShouldNotBeNull();
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
        var location = Assimalign.Cohesion.Database.Language.Location.Create(1, 1, 4, 12);
        SqlBoundExpression expression = position switch
        {
            "AND" => new SqlBoundLogical(false, [invalid, new SqlBoundConstant(true)], location),
            "OR" => new SqlBoundLogical(true, [invalid, new SqlBoundConstant(false)], location),
            "CASE WHEN" => new SqlBoundCase(null, [new SqlBoundWhen(invalid, new SqlBoundConstant(2), default, location)], null),
            _ => invalid,
        };

        // Act
        var failure = Should.Throw<DatabaseException>(() => evaluator.Matches(expression, []));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02003");
        failure.Message.ShouldContain(position, Case.Sensitive);
        if (position != "predicate") { failure.Diagnostics[0].Start.ShouldBe(location.Start); }
    }
}
