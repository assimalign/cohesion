using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;


namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTruncateReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - TRUNCATE: all DML join sources retain stale-definition checks (#1716)")]
    [InlineData("INSERT INTO u SELECT c.id FROM c JOIN p ON c.pid=p.id")]
    [InlineData("INSERT INTO u SELECT id FROM (SELECT c.id FROM c JOIN p ON c.pid=p.id) AS joined")]
    [InlineData("INSERT INTO u SELECT id FROM v")]
    [InlineData("INSERT INTO u SELECT 2 WHERE EXISTS (SELECT c.id FROM c JOIN p ON c.pid=p.id)")]
    [InlineData("UPDATE u SET id=2 WHERE EXISTS (SELECT c.id FROM c JOIN p ON c.pid=p.id)")]
    [InlineData("DELETE FROM u WHERE EXISTS (SELECT c.id FROM c JOIN p ON c.pid=p.id)")]
    [InlineData("WITH joined AS (SELECT c.id FROM c JOIN p ON c.pid=p.id) INSERT INTO u SELECT id FROM joined")]
    [InlineData("UPDATE u SET id=c.id FROM c JOIN p ON c.pid=p.id")]
    [InlineData("DELETE FROM u USING c JOIN p ON c.pid=p.id")]
    public async Task ExecuteAsync_RetiredJoinFeedsDml_ShouldRefuseStaleSource(string sql)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-dml-sources", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(writer, "CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id))", timeout.Token);
        await ExecAsync(writer, "CREATE TABLE u (id INT)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO p VALUES (1)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO c VALUES (2,1)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO u VALUES (3)", timeout.Token);
        await ExecAsync(writer, "CREATE VIEW v AS SELECT c.id FROM c JOIN p ON c.pid=p.id", timeout.Token);
        await using var snapshot = await reader.BeginTransactionAsync(timeout.Token);
        await ExecAsync(writer, "TRUNCATE p CASCADE", timeout.Token);
        (await RowsAsync(reader, "SELECT id FROM v", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(2);

        // Act
        var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(() => ExecAsync(reader, sql, timeout.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04006");
        (await RowsAsync(writer, "SELECT id FROM u", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(3);
    }
}
