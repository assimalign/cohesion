using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentKeyJoinWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Key joins: bindings metadata NEST and diagnostics cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_KeyJoins_ShouldPreserveResultsAndConnectionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.QueryAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{n:1}), ('b',{n:2})", cancellationToken: harness.Token);
        // Act
        var joined = await connection.QueryAsync("SELECT META(a).id AS a, META(b).id AS b, META(a).version = META(b).version AS v FROM items AS a JOIN items AS b ON META(b).id = META(a).id ORDER BY a.n", cancellationToken: harness.Token);
        var star = await connection.QueryAsync("SELECT * FROM [1] AS a JOIN items AS b ON META(b).id = 'a'", cancellationToken: harness.Token);
        var qualified = await connection.QueryAsync("SELECT b.* FROM [1] AS a JOIN items AS b ON META(b).id = 'b'", cancellationToken: harness.Token);
        var left = await connection.QueryAsync("SELECT b, META(b).id AS id FROM [1] AS a LEFT JOIN items AS b ON META(b).id = 'absent'", cancellationToken: harness.Token);
        var nest = await connection.QueryAsync("SELECT RAW b FROM ['a','absent',NULL,MISSING] AS k LEFT NEST items AS b ON META(b).id = k", cancellationToken: harness.Token);
        var correlated = await connection.QueryAsync("SELECT RAW (SELECT RAW b.n FROM [1] AS s JOIN items AS b ON META(b).id = META(a).id) FROM items AS a ORDER BY a.n", cancellationToken: harness.Token);
        // Assert
        joined.Select(row => row[0]).ShouldBe(new object?[] { "a", "b" });
        joined.ShouldAllBe(row => Equals(row[0], row[1]) && Equals(row[2], true));
        star.Documents.Single().GetRawText().ShouldBe("{\"a\":1,\"b\":{\"n\":1}}");
        qualified.Documents.Single().GetRawText().ShouldBe("{\"n\":2}");
        left.Single().IsMissing(0).ShouldBeTrue();
        left.Single().IsMissing(1).ShouldBeTrue();
        nest.Documents.Select(value => value.GetRawText()).ShouldBe(new[] { "[{\"n\":1}]", "[]", "null", "null" });
        correlated.Documents.Select(value => value[0].GetInt32()).ShouldBe(new[] { 1, 2 });
        var plan = await connection.QueryAsync("EXPLAIN SELECT * FROM [1] AS a LEFT NEST items AS b ON META(b).id = 'a'", cancellationToken: harness.Token);
        plan.Documents.ShouldContain(value => value.GetProperty("operator").GetString() == "LeftKeyNest");
        foreach (var test in new[] {
            ("SELECT b FROM items AS a JOIN items AS b ON META(b).id = [META(a).id]", "CD02028"),
            ("SELECT b FROM items JOIN items AS b ON META(b).id = 'a'", "CD00016"),
            ("SELECT b FROM items AS a JOIN items AS b ON META(b).id = b.k", "CD01035") })
        {
            var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(test.Item1, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(test.Item2);
            error.Diagnostics.Single().Start.ShouldNotBeNull();
        }
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT COUNT(*) AS n FROM items", cancellationToken: harness.Token)).Single()[0].ShouldBe(2L);
    }
}
