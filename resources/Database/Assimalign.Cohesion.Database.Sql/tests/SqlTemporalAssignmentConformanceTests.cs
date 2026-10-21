using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTemporalAssignmentConformanceTests
{
    public static IEnumerable<object[]> Inputs() =>
    [
        ["INTERVAL", "12", TimeSpan.FromSeconds(12)],
        ["INTERVAL", " -12.0000001 ", TimeSpan.FromSeconds(-12).Add(TimeSpan.FromTicks(-1))],
        ["INTERVAL", "2 days 03:04:05", new TimeSpan(2, 3, 4, 5)],
        ["INTERVAL", "P1DT2H", TimeSpan.FromHours(26)],
        ["INTERVAL", "-1 02:03:04", TimeSpan.FromSeconds(-79016)],
        ["interval_domain", "12.5", TimeSpan.FromMilliseconds(12500)],
        ["TIME", " 13:45 ", new TimeOnly(13, 45)],
        ["TIME", "00:00:00.0000001", new TimeOnly(1)],
        ["time_domain", "23:59:59.1234567", new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(1234567))],
    ];

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Conformance: TIME and INTERVAL assignments share CAST input (#1422)")]
    [MemberData(nameof(Inputs))]
    public async Task ExecuteAsync_TemporalTextAssignment_ShouldMatchCastOnEveryWritePath(string type, string text, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("temporal-assignment", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN interval_domain AS INTERVAL;", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN time_domain AS TIME;", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (id INT, v {type} DEFAULT '{text}');", cancellationToken: CancellationToken.None);

        // Act
        foreach (string sql in new[]
        {
            "INSERT INTO t (id) VALUES (1);",
            $"INSERT INTO t VALUES (2, '{text}');",
            $"INSERT INTO t SELECT 3, '{text}';",
            "INSERT INTO t VALUES (4, NULL);",
            $"UPDATE t SET v = '{text}' WHERE id = 4;",
            $"ALTER TABLE t ADD COLUMN extra {type} DEFAULT '{text}';",
        })
        {
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        }
        await using var result = (await session.ExecuteAsync($"SELECT v, extra, CAST('{text}' AS {type}) FROM t ORDER BY id;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.GetValue(0).ShouldBe(expected);
            row.GetValue(1).ShouldBe(expected);
            row.GetValue(2).ShouldBe(expected);
            count++;
        }
        count.ShouldBe(4);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Conformance: invalid temporal assignment fails atomically (#1422)")]
    [InlineData("INTERVAL", "1.02:03:04")]
    [InlineData("INTERVAL", "1 year")]
    [InlineData("INTERVAL", "0.00000001")]
    [InlineData("TIME", "1 PM")]
    [InlineData("TIME", "1:02")]
    [InlineData("TIME", "24:00")]
    [InlineData("TIME", "10:00Z")]
    public async Task ExecuteAsync_InvalidTemporalText_ShouldRejectCastAndAssignment(string type, string text)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid-temporal-assignment", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (v {type});", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string sql in new[]
        {
            $"INSERT INTO t VALUES (NULL), ('{text}');",
            $"SELECT CAST('{text}' AS {type});",
        })
        {
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        }
        await using var result = (await session.ExecuteAsync("SELECT * FROM t;", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
        count.ShouldBe(0);
    }
}
