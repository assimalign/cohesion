using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentArrayIndexWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Array DDL, correlated seeks and repeated UNNEST rows use the existing wire (#1530)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ArrayIndex_ShouldKeepCorrelationMultiplicityAndLocatedFailures(bool tcp)
    {
        // Arrange.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "cross", "{\"variants\":[{\"price\":20,\"stock\":0},{\"price\":5,\"stock\":1}]}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "good", "{\"variants\":[{\"price\":20,\"stock\":1},{\"price\":20,\"stock\":1}]}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.ExecuteAsync("CREATE INDEX ix ON items (DISTINCT ARRAY v.price FOR v IN variants WHEN v.stock > 0 END)", cancellationToken: harness.Token);
        const string query = "SELECT META().id AS id FROM items WHERE ANY t IN variants SATISFIES t.price > 10 AND t.stock > 0 END";

        // Act.
        var rows = await connection.QueryAsync(query, cancellationToken: harness.Token);
        var unnest = await connection.QueryAsync("SELECT t.price AS p FROM items d UNNEST d.variants t WHERE t.price > 10 AND t.stock > 0", cancellationToken: harness.Token);
        var plan = await connection.QueryAsync("EXPLAIN " + query, cancellationToken: harness.Token);

        // Assert.
        rows.ShouldHaveSingleItem()[0].ShouldBe("good");
        unnest.Select(row => row[0]).ShouldBe([20m, 20m]);
        plan.Documents.ShouldContain(node => node.GetProperty("operator").GetString() == "IndexSeek" &&
            node.GetProperty("index").GetString() == "ix" &&
            node.GetProperty("detail").GetString()!.Contains("array_residual=True", System.StringComparison.Ordinal));
        var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.ExecuteAsync(
            "CREATE INDEX bad ON items (DISTINCT ARRAY v.price FOR v IN variants WHEN v.stock > @stock END)", cancellationToken: harness.Token));
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD02027");
        diagnostic.Start.ShouldNotBeNull();
        (await connection.QueryAsync(query, cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe("good");
    }
}
