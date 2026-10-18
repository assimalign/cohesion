using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlRoundRegressionTests
{
    public static IEnumerable<object[]> NegativeScales()
    {
        for (int scale = -1; scale >= -28; scale--)
        {
            yield return [scale, 1];
            yield return [scale, -1];
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ROUND: every negative scale rounds exact midpoint digits once for either sign")]
    [MemberData(nameof(NegativeScales))]
    public async Task Execute_NegativeScaleMidpoints_ShouldUseExactRemainder(int scale, int sign)
    {
        // Arrange
        decimal factor = 1m;
        for (int index = 0; index < -scale; index++) { factor *= 10m; }
        decimal epsilon = 1m;
        for (int index = 0; index < 29 + scale; index++) { epsilon /= 10m; }
        decimal midpoint = factor / 2m;
        string Text(decimal value) => (sign * value).ToString("0.0###########################", CultureInfo.InvariantCulture);
        await using var engine = SqlDatabaseEngine.Create("round-regression", new());
        var database = await engine.CreateDatabaseAsync("rounds", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync(
            $"SELECT ROUND({Text(midpoint - epsilon)}, {scale}), ROUND({Text(midpoint)}, {scale}), ROUND({Text(midpoint + epsilon)}, {scale}) FROM t;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await SqlT1ScalarTests.ReadAsync(result);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 0m, sign * factor, sign * factor });
    }
}
