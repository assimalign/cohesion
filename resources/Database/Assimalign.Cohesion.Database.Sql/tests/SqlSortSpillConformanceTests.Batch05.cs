using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Common group keys preserve values, CLR types and metadata across hash spill (#1788, #1789)")]
    [InlineData("COALESCE(d,r)")]
    [InlineData("CASE WHEN id%2=0 THEN id ELSE r END")]
    [InlineData("COALESCE(c4,'x')")]
    public async Task ExecuteAsync_CommonTypeGroupKeys_ShouldMatchFittingAndSpilling(string key)
    {
        // Arrange: every key has thousands of groups; the small grant must spill.
        await using var fitting = await Create("common-group-fit", 67108864);
        await using var spilling = await Create("common-group-spill", 262144);
        await using var a = await (await fitting.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilling.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await session.ExecuteAsync("CREATE TABLE choices(id INT,d NUMERIC(12,2),r REAL,c4 CHAR(4))", cancellationToken: CancellationToken.None);
            for (int first = 0; first < 8000; first += 200)
            {
                string values = string.Join(',', Enumerable.Range(first, 200).Select(i =>
                    $"({i},{(i % 2 == 0 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")},{i},{(i == 0 ? "NULL" : i == 1 ? "'x'" : i == 2 ? "'q'" : $"'{i:D4}'")})"));
                await session.ExecuteAsync("INSERT INTO choices VALUES" + values, cancellationToken: CancellationToken.None);
            }
        }
        string sql = $"SELECT {key} AS k,COUNT(*) FROM choices GROUP BY {key}";

        // Act.
        await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var expected = await Rows(left);
        var actual = await Rows(right);

        // Assert: RowValues compares CLR types and exact numeric representations too.
        actual.ShouldBe(expected);
        actual.Length.ShouldBe(key == "COALESCE(c4,'x')" ? 7999 : 8000);
        if (key == "COALESCE(c4,'x')")
        {
            actual.ShouldContain(row => Equals(row.Cells[0], "x") && Equals(row.Cells[1], 2L));
            actual.ShouldContain(row => Equals(row.Cells[0], "q   "));
        }
        right.Columns.Select(c => (c.Ordinal, c.Name, c.Type, c.MaxLength, c.IsNullable)).ShouldBe(
            left.Columns.Select(c => (c.Ordinal, c.Name, c.Type, c.MaxLength, c.IsNullable)));
        SqlHashSpillMetrics.For(a.LastStatementMetrics!).ShouldBeNull();
        SqlHashSpillMetrics.For(b.LastStatementMetrics!).ShouldNotBeNull().Batches.ShouldBeGreaterThan(0);
    }
}
