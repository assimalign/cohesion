using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlConstantSelectTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT without FROM: one implicit row composes with query operators (#1373)")]
    [InlineData("SELECT 1", 1)]
    [InlineData("SELECT 1 + 2 AS answer WHERE TRUE ORDER BY answer LIMIT 1", 3L)]
    [InlineData("SELECT DISTINCT 1 ORDER BY 1", 1)]
    [InlineData("SELECT (SELECT 1)", 1)]
    [InlineData("SELECT 1 WHERE 1 IN (SELECT 1)", 1)]
    [InlineData("SELECT 1 WHERE EXISTS (SELECT NULL)", 1)]
    [InlineData("SELECT COUNT(*)", 1L)]
    [InlineData("SELECT COUNT(*) WHERE FALSE HAVING COUNT(*) = 0", 0L)]
    [InlineData("SELECT answer FROM (SELECT 1 AS answer) d", 1)]
    [InlineData("SELECT 1 UNION SELECT 1", 1)]
    public async Task ExecuteAsync_WithoutFrom_ShouldUseOneImplicitRow(string sql, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("constant-select", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { expected });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT without FROM: filters and windows can remove the implicit row (#1373)")]
    [InlineData("SELECT 1 WHERE FALSE")]
    [InlineData("SELECT 1 WHERE NULL")]
    [InlineData("SELECT 1 LIMIT 0")]
    [InlineData("SELECT 1 OFFSET 1")]
    public async Task ExecuteAsync_RemovedImplicitRow_ShouldReturnNoRows(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("constant-empty", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT without FROM: views, inserts and correlated children retain their scopes (#1373)")]
    public async Task ExecuteAsync_ComposedConstantQueries_ShouldPreserveValues()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("constant-composition", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW answer AS SELECT 42 AS value", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t SELECT value FROM answer", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t SELECT 43", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "SELECT (SELECT t.id), (SELECT (SELECT t.id)) FROM t ORDER BY id");

        // Assert
        rows.ShouldBe(new object?[][] { [42, 42], [43, 43] });
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT *", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE013");
        failure.Message.ShouldContain("without FROM", Case.Sensitive);
    }

    internal static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < values.Length; i++) { values[i] = row.GetValue(i); }
            rows.Add(values);
        }
        return rows;
    }
}
