using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCteExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: qualified FOR and aliases execute")]
    [InlineData("share")]
    [InlineData("no")]
    [InlineData("key")]
    [InlineData("AS share")]
    public async Task Execute_ReviewQualifiedForWithAlias_ShouldReturnStoredValue(string suffix)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-review-alias", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (\"for\" INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (7);", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"SELECT t.for {suffix} FROM t;");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(7);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: MERGE USING keyword CTE executes and returns target")]
    [InlineData("over")]
    [InlineData("partition")]
    [InlineData("window")]
    [InlineData("natural")]
    public async Task Execute_ReviewMergeKeywordCommonTable_ShouldInsertAndReturn(string name)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-review-merge", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"WITH {name} AS (SELECT 1 AS id) MERGE INTO t USING {name} ON t.id = {name}.id WHEN NOT MATCHED THEN INSERT VALUES ({name}.id) RETURNING t.id;");

        // Assert
        Convert.ToInt64(rows.ShouldHaveSingleItem()[0]).ShouldBe(1L);
        (await RowsAsync(session, "SELECT id FROM t;")).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: recursive MERGE modifier refuses before effects")]
    [InlineData("SELECT 2 AS n")]
    [InlineData("SELECT 2 AS n UNION ALL SELECT n FROM r")]
    public async Task Execute_ReviewRecursiveMergePrefix_ShouldRefuseAtModifier(string body)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cte-review-recursive-merge", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var error = await RecursiveDiagnosticAsync(session, $"WITH RECURSIVE r AS ({body}) MERGE INTO t USING r ON t.id=r.n WHEN MATCHED THEN UPDATE SET id=9;");

        // Assert
        error.Code.ShouldBe("CK00001");
        error.Start.ShouldBe(5);
        error.End.ShouldBe(14);
        error.Message!.ShouldContain("WITH RECURSIVE for MERGE", Case.Sensitive);
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id;")).Select(row => row[0]).ShouldBe([1, 2, 3]);
    }
}
