using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlExplainWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - EXPLAIN: indexed descriptions equal in-process rows on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ExplainSeek_ShouldMatchInProcessAsync(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        var database = await harness.Engine.OpenDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_score ON users (score);", cancellationToken: CancellationToken.None);
        const string source = "EXPLAIN SELECT name FROM users WHERE score >= 100 AND score < 200;";
        await using var description = (await session.ExecuteAsync(source, cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        var expected = new List<object?[]>();
        await foreach (var row in description.GetRowsAsync(CancellationToken.None))
        { expected.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        await using var connection = await harness.Client.ConnectAsync(CancellationToken.None);
        // Act
        var result = await connection.QueryAsync(source, cancellationToken: CancellationToken.None);
        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(description.Columns.Select(column => column.Name));
        result.Columns.Select(column => column.Type).ShouldBe(description.Columns.Select(column => column.Type));
        result.Count.ShouldBe(expected.Count);
        for (int i = 0; i < result.Count; i++)
        { Enumerable.Range(0, 8).Select(ordinal => result[i][ordinal]).ShouldBe(expected[i]); }
        result.ShouldContain(row => Equals(row[4], "by_score"));
    }
}
