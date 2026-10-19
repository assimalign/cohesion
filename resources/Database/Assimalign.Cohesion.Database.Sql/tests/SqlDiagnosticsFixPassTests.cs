using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDiagnosticsFixPassTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Diagnostics: UPDATE aggregate errors keep the assignment expression span")]
    public async Task Execute_UpdateAggregate_ShouldCarryAssignmentSpanAsync()
    {
        // Arrange
        var request = SqlQueryRequest.FromSql("UPDATE t SET id = 1 + SUM(id)");
        var value = ((SqlUpdateExpression)request.Statement.SqlExpression).Assignments.Single().Value;
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE017");
        diagnostic.Start.ShouldBe(value.Location!.Start);
        diagnostic.End.ShouldBe(value.Location.End);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Diagnostics: COLLATE type errors never point at an unrelated DEFAULT")]
    public async Task Execute_CollationTypeMismatch_ShouldUseStatementFallbackAsync()
    {
        // Arrange
        var request = SqlQueryRequest.FromSql("CREATE TABLE t (id INT COLLATE binary DEFAULT 1)");
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE013");
        diagnostic.Start.ShouldBe(request.Statement.SqlExpression.Location!.Start);
        diagnostic.End.ShouldBe(request.Statement.SqlExpression.Location.End);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Diagnostics: quoted niladic names remain column references in VALUES")]
    public async Task Execute_QuotedNiladicColumn_ShouldCarryColumnScopeDiagnosticAsync()
    {
        // Arrange
        var request = SqlQueryRequest.FromSql("INSERT INTO t VALUES (\"current_date\")");
        var column = ((SqlInsertExpression)request.Statement.SqlExpression).Values![0][0];
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE005");
        diagnostic.Start.ShouldBe(column.Location!.Start);
        diagnostic.End.ShouldBe(column.Location.End);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Diagnostics: catalog mutations carry a code and statement span")]
    [InlineData("DELETE FROM INFORMATION_SCHEMA.TABLES")]
    [InlineData("CREATE INDEX ix ON COHESION_SCHEMA.OBJECT_OWNERSHIP(OBJECT_NAME)")]
    [InlineData("DROP TABLE IF EXISTS INFORMATION_SCHEMA.TABLES")]
    public async Task Execute_CatalogWrite_ShouldCarryLocatedDiagnosticAsync(string sql)
    {
        // Arrange
        var request = SqlQueryRequest.FromSql(sql);
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE021");
        diagnostic.Start.ShouldBe(request.Statement.SqlExpression.Location!.Start);
        diagnostic.End.ShouldBe(request.Statement.SqlExpression.Location.End);
        diagnostic.Message.ShouldBe(failure.Message);
        failure.Message.ShouldNotContain("COHSQLE", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Diagnostics: scoped column failures use the offending column span")]
    [InlineData("INSERT INTO t VALUES (id)", "id")]
    [InlineData("SELECT id FROM t LIMIT id", "id")]
    [InlineData("SELECT id FROM t OFFSET 1 + id", "id")]
    public async Task Execute_ColumnOutsideScope_ShouldCarryColumnSpanAsync(string sql, string column)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE005");
        diagnostic.Start.ShouldBe(sql.LastIndexOf(column, System.StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + column.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Diagnostics: evaluator faults carry codes and expression spans")]
    [InlineData("SELECT id FROM t LIMIT NOT 1", "COHSQLE003", true)]
    [InlineData("SELECT CAST(9999999999999999999999999999999999 AS INT) FROM t", "COHSQLE002", false)]
    [InlineData("SELECT CAST('invalid' AS INT) FROM t", "COHSQLE022", false)]
    [InlineData("SELECT CAST('9999999999999999999999999999999999' AS INT) FROM t", "COHSQLE002", false)]
    [InlineData("SELECT @value + 1 FROM t", "COHSQLE003", false)]
    [InlineData("SELECT 1 / 0 FROM t", "COHSQLE001", false)]
    public async Task Execute_EvaluationFault_ShouldCarryExpressionSpanAsync(string sql, string code, bool count)
    {
        // Arrange
        var request = SqlQueryRequest.FromSql(sql);
        var select = (SqlSelectExpression)request.Statement.SqlExpression;
        var expression = count ? select.Limit! : select.Columns.Single().Expression;
        await using var engine = SqlDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("diagnostics", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql,
            new System.Collections.Generic.Dictionary<string, object?> { ["value"] = "text" }, CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(expression.Location!.Start);
        diagnostic.End.ShouldBe(expression.Location.End);
        diagnostic.Message.ShouldBe(failure.Message);
    }
}
