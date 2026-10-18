using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSubqueryExecutionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: cancellation within a per-row child preserves BEGIN and the next statement")]
    public async Task Execute_CancellationWithinCorrelatedChild_ShouldKeepTransactionActive()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var function = new CorrelationCancellationFunction(cancellation);
        var builder = SqlDatabaseEngine.CreateBuilder("correlation-cancel");
        builder.Functions.Add(function);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "CREATE TABLE u (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2), (3), (4)");
        await ExecuteAsync(session, "INSERT INTO u VALUES (1), (2), (3), (4)");
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "INSERT INTO t VALUES (5)");
        var transaction = session.CurrentTransaction.ShouldNotBeNull();

        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(() => session.ExecuteAsync(
            "SELECT t.id FROM t WHERE EXISTS (SELECT u.id FROM u WHERE u.id = t.id AND cancel_third(u.id))",
            cancellationToken: cancellation.Token).AsTask());

        // Assert
        error.CancellationToken.ShouldBe(cancellation.Token);
        function.Calls.ShouldBe(3);
        function.LastToken.ShouldBe(cancellation.Token);
        transaction.State.ShouldBe(TransactionState.Active);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(5L);
        await ExecuteAsync(session, "ROLLBACK");
        (await RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(4L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: aggregate arguments grouping keys catalog rows and result shaping retain their frames")]
    public async Task Execute_CorrelatedGroupingAndShaping_ShouldEvaluateCorrectFrames()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated-shaping");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE sales (id INT, k INT, amount INT)");
        await ExecuteAsync(session, "CREATE TABLE labels (k INT, threshold INT)");
        await ExecuteAsync(session, "INSERT INTO sales VALUES (1, 1, 4), (2, 1, 6), (3, 2, 8), (4, 3, 3)");
        await ExecuteAsync(session, "INSERT INTO labels VALUES (1, 9), (2, 10)");

        // Act / Assert
        var sums = await RowsAsync(session, "SELECT s.k, SUM((SELECT l.threshold FROM labels l WHERE l.k = s.k)) FROM sales s GROUP BY s.k ORDER BY s.k");
        sums.Count.ShouldBe(3);
        sums[0].ShouldBe(new object?[] { 1, 18m });
        sums[1].ShouldBe(new object?[] { 2, 10m });
        sums[2].ShouldBe(new object?[] { 3, null });
        (await RowsAsync(session, "SELECT COUNT(*) FROM sales s GROUP BY (SELECT l.threshold FROM labels l WHERE l.k = s.k) ORDER BY 1"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1L, 1L, 2L });
        (await RowsAsync(session, "SELECT s.k, COUNT(*) FROM sales s GROUP BY s.k ORDER BY (SELECT l.threshold FROM labels l WHERE l.k = s.k) DESC"))
            .Select(row => row[0]).ShouldBe(new object?[] { 2, 1, 3 });
        (await RowsAsync(session, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES c WHERE EXISTS (SELECT 1 FROM sales s WHERE c.TABLE_NAME = 'sales' AND s.id = 1)"))
            .ShouldHaveSingleItem()[0].ShouldBe("sales");
        (await RowsAsync(session, "SELECT DISTINCT (SELECT l.threshold FROM labels l WHERE l.k = s.k) FROM sales s ORDER BY 1 LIMIT 1 OFFSET 1"))
            .ShouldHaveSingleItem()[0].ShouldBe(9);
        (await RowsAsync(session, "SELECT DISTINCT (SELECT l.threshold FROM labels l WHERE l.k = s.k) FROM sales s ORDER BY 1 LIMIT 1 OFFSET 2"))
            .ShouldHaveSingleItem()[0].ShouldBe(10);
        (await RowsAsync(session, "SELECT s.id, l.k FROM sales s LEFT JOIN labels l ON s.k = l.k AND NOT EXISTS (SELECT x.k FROM labels x WHERE x.k = s.k AND x.threshold < 10) ORDER BY s.id"))
            .Select(row => row[1]).ShouldBe(new object?[] { null, null, 2, null });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlation: skipped scalar children would fail cardinality if evaluated")]
    [InlineData("COALESCE(9, (SELECT x.id FROM choices x WHERE c.id = 1))", "9")]
    [InlineData("c.id = 1 OR (SELECT x.id FROM choices x WHERE c.id = 1) = 2", "True")]
    [InlineData("c.id = 2 AND (SELECT x.id FROM choices x WHERE c.id = 1) = 2", "False")]
    public async Task Execute_LazyCorrelatedChild_ShouldSkipCardinalityFailure(string expression, string expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("lazy-child");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        var rows = await RowsAsync(session, $"SELECT {expression} FROM candidates c WHERE c.id = 1");

        // Assert
        rows.ShouldHaveSingleItem()[0]!.ToString().ShouldBe(expected);
        await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session,
            "SELECT (SELECT x.id FROM choices x WHERE c.id = 1) FROM candidates c WHERE c.id = 1"));
    }
}
