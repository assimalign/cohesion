using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphTextIndexWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Text index DDL and ordinal seeks retain InMemory and TCP sessions (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_TextIndex_ShouldKeepScanParityAndSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await connection.ExecuteAsync("INSERT (:N {k:1,p:'abcXbcd'}),(:N {k:2,p:'abcd'}),(:N {k:3,p:'😀😀'}),(:N {k:4,p:'😀😀😀'})", cancellationToken: harness.Token);
        string[] queries = ["MATCH (n:N) WHERE n.p CONTAINS 'abcd' RETURN n.k", "MATCH (n:N) WHERE n.p ENDS WITH 'bcd' RETURN n.k ORDER BY n.k",
            "MATCH (n:N) WHERE n.p STARTS WITH 'abc' RETURN n.k ORDER BY n.k", "MATCH (n:N) WHERE n.p='abcd' RETURN n.k",
            "MATCH (n:N) WHERE n.p CONTAINS '😀😀' RETURN n.k ORDER BY n.k"];
        var before = new object?[queries.Length][];
        for (int i = 0; i < queries.Length; i++)
        { before[i] = (await connection.QueryAsync(queries[i], cancellationToken: harness.Token)).Select(row => row[0]).ToArray(); }

        // Act
        await connection.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: harness.Token);
        await connection.ExecuteAsync("CREATE TEXT INDEX text_p IF NOT EXISTS FOR (n:N) ON (n.p)", cancellationToken: harness.Token);

        // Assert
        for (int i = 0; i < queries.Length; i++)
        { (await connection.QueryAsync(queries[i], cancellationToken: harness.Token)).Select(row => row[0]).ShouldBe(before[i]); }
        (await connection.QueryAsync("EXPLAIN " + queries[0], cancellationToken: harness.Token)).ShouldContain(row => Equals(row[2], "TextIndexSeek"));
        (await connection.QueryAsync("EXPLAIN " + queries[4], cancellationToken: harness.Token)).ShouldContain(row => Equals(row[2], "LabelScan"));
        await connection.ExecuteAsync("DROP INDEX text_p", cancellationToken: harness.Token);
        (await connection.QueryAsync(queries[0], cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
