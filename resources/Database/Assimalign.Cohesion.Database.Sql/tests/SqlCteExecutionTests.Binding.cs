using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCteExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - WITH: invalid scope and constant predicates fail before reads or writes")]
    [InlineData("WITH t AS (SELECT 42 AS id) SELECT dbo.t.id FROM t", "COHSQLE015")]
    [InlineData("WITH c AS (SELECT 1 AS id) SELECT dbo.c.id FROM c", "COHSQLE015")]
    [InlineData("WITH t AS (SELECT 42 AS id) SELECT * FROM t CROSS JOIN dbo.t", "COHSQLE019")]
    [InlineData("WITH t AS (SELECT 42 AS id) SELECT * FROM dbo.t CROSS JOIN t", "COHSQLE019")]
    [InlineData("WITH t AS (SELECT 42 AS id) SELECT t.id FROM t x", "COHSQLE015")]
    [InlineData("WITH t AS (SELECT 1 AS id) DELETE FROM t AS victim WHERE id IN (SELECT dbo.t.id FROM t)", "COHSQLE015")]
    [InlineData("WITH unused AS (SELECT 1 WHERE COUNT(*) > 0) DELETE FROM t", "COHSQLE017")]
    [InlineData("WITH unused AS (SELECT COUNT(*) AS n WHERE COUNT(*) > 0) DELETE FROM t", "COHSQLE017")]
    [InlineData("WITH c AS (SELECT 1 AS x WHERE COUNT(*) > 0) SELECT x FROM c", "COHSQLE017")]
    [InlineData("WITH c AS (SELECT COUNT(*) AS n WHERE COUNT(*) > 0) SELECT n FROM c", "COHSQLE017")]
    [InlineData("WITH unused AS (SELECT 1 GROUP BY COUNT(*)) DELETE FROM t", "COHSQLE017")]
    [InlineData("WITH unused AS (SELECT *) DELETE FROM t", "COHSQLE013")]
    [InlineData("WITH a AS (SELECT id FROM b), b AS (SELECT 1 AS id) SELECT id FROM a", "COHSQLE026")]
    public async Task Execute_InvalidBinding_ShouldFailBeforeReadingAndPreserveRows(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-binding", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        if (sql.Contains("SELECT dbo.", StringComparison.Ordinal) && code == "COHSQLE015")
        {
            error.Message.ShouldContain("Missing FROM-clause entry", Case.Sensitive);
        }
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe([1, 2, 3]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: a later sibling falls back to a same-named stored table")]
    public async Task Execute_FutureNameWithStoredTable_ShouldReadCatalogRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-future", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE b (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO b VALUES (7), (9)", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session,
            "WITH a AS (SELECT id FROM b), b AS (SELECT 1 AS id) SELECT id FROM a ORDER BY id");

        // Assert
        rows.Select(row => row[0]).ShouldBe([7, 9]);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: schema-qualified columns bypass the CTE and correlate to a stored DML target")]
    public async Task Execute_QualifiedDmlTarget_ShouldCorrelatePastCommonTable()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-dml-qualifier", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var result = await session.ExecuteAsync(
            "WITH t AS (SELECT 1 AS id) DELETE FROM t WHERE id IN (SELECT dbo.t.id FROM t)",
            cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(3);
        (await RowsAsync(session, "SELECT id FROM t")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: aliases distinguish a CTE from a same-named stored relation")]
    public async Task Execute_AliasedCommonTable_ShouldKeepSchemaLessIdentity()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-alias", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session,
            "WITH t AS (SELECT 42 AS id) SELECT c.id, dbo.t.id FROM t c CROSS JOIN dbo.t ORDER BY dbo.t.id LIMIT 1");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([42L, 1]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - WITH: only the definition root receives an implicit row")]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT 2)")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT 2)")]
    [InlineData("SELECT (SELECT 2) AS id FROM t")]
    [InlineData("SELECT * FROM (SELECT 2 AS id) d")]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT *)")]
    [InlineData("SELECT * FROM (SELECT *) d")]
    public async Task Execute_NestedQueryWithoutFrom_ShouldKeepOrdinaryDiagnostic(string body)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-nested", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var ordinary = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(body, cancellationToken: CancellationToken.None));

        // Act
        var nested = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"WITH a AS ({body}) SELECT * FROM a", cancellationToken: CancellationToken.None));

        // Assert
        nested.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE013");
        nested.Message.ShouldBe(ordinary.Message);
        nested.Message.ShouldNotContain("common table", Case.Insensitive);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - WITH: constant definitions execute all supported SELECT modifiers")]
    [InlineData("WITH a AS (SELECT 1 AS x WHERE 1 = 0) SELECT COUNT(*) FROM a", 0L)]
    [InlineData("WITH a AS (SELECT DISTINCT 1 AS x) SELECT COUNT(*) FROM a", 1L)]
    [InlineData("WITH a AS (SELECT 1 AS x WHERE 1 = 1 ORDER BY x DESC LIMIT 1 OFFSET 0) SELECT x FROM a", 1L)]
    [InlineData("WITH a AS (SELECT 1 AS x ORDER BY 1 LIMIT 1) SELECT x FROM a", 1L)]
    [InlineData("WITH a AS (SELECT 1 AS x LIMIT 0) SELECT COUNT(*) FROM a", 0L)]
    [InlineData("WITH a AS (SELECT 1 AS x OFFSET 1) SELECT COUNT(*) FROM a", 0L)]
    [InlineData("WITH a AS (SELECT 1 AS x LIMIT 1 + 1 OFFSET 1 - 1) SELECT x FROM a", 1L)]
    [InlineData("WITH a AS (SELECT COUNT(*) AS n) SELECT n FROM a", 1L)]
    [InlineData("WITH a AS (SELECT SUM(2) AS n) SELECT n FROM a", 2L)]
    [InlineData("WITH a AS (SELECT COUNT(*) AS n WHERE 1 = 0) SELECT n FROM a", 0L)]
    [InlineData("WITH a AS (SELECT 1 + 1 AS k, COUNT(*) AS n GROUP BY 1 + 1 HAVING COUNT(*) = 1 ORDER BY k DESC LIMIT 1 OFFSET 0) SELECT SUM(k + n) FROM a", 3L)]
    [InlineData("WITH a AS (SELECT COUNT(*) AS n HAVING COUNT(*) > 1) SELECT COUNT(*) FROM a", 0L)]
    [InlineData("WITH a AS (SELECT 1 AS x HAVING 1 = 1) SELECT x FROM a", 1L)]
    [InlineData("WITH a AS (SELECT 1 AS x ORDER BY COUNT(*) DESC) SELECT x FROM a", 1L)]
    [InlineData("WITH a AS (SELECT (SELECT MAX(id) FROM t) AS x) SELECT x FROM a", 3L)]
    [InlineData("WITH a AS (SELECT 1 AS x WHERE EXISTS (SELECT id FROM t)) SELECT x FROM a", 1L)]
    public async Task Execute_ConstantDefinition_ShouldApplySelectClauses(string sql, long expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-constant", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        Convert.ToInt64(rows.ShouldHaveSingleItem()[0]).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: EXPLAIN describes constant filters and result modifiers")]
    public async Task Execute_ExplainConstant_ShouldDescribeFilterOrderingAndWindow()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-explain-constant", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session,
            "EXPLAIN WITH a AS (SELECT DISTINCT 1 AS x WHERE 1 = 1 ORDER BY x DESC LIMIT 1 OFFSET 0) SELECT x FROM a");

        // Assert
        rows.Select(row => row[2]).ShouldBe(["With", "Materialize", "Constant", "Select", "CteScan"]);
        rows.Single(row => Equals(row[2], "Constant"))[5]
            .ShouldBe("filter=True; order_keys=1; distinct=True; limit=1; offset=0");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
    }
}
