using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphSchemaDdlWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Schema DDL: create drop discovery refusal and session reuse cross InMemory and TCP")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SchemaDdl_ShouldCoordinateTokensRulesAndTransactionsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (string text in new[]
        {
            "CREATE LABEL Empty IF NOT EXISTS", "DROP LABEL Empty IF EXISTS",
            "CREATE RELATIONSHIP TYPE Empty IF NOT EXISTS", "DROP RELATIONSHIP TYPE Empty IF EXISTS",
            "CREATE INDEX ix FOR (n:Fresh) ON (n.p)", "CREATE INDEX ix IF NOT EXISTS FOR (n:Fresh) ON (n.p)",
            "CREATE CONSTRAINT typed FOR (n:Fresh) REQUIRE n.p IS :: INTEGER",
            "CREATE CONSTRAINT required FOR (n:Fresh) REQUIRE n.p IS NOT NULL",
            "CREATE CONSTRAINT FOR ()-[r:EDGE]-() REQUIRE r.p IS NOT NULL",
        }) { await connection.ExecuteAsync(text, cancellationToken: harness.Token); }
        (await connection.QueryAsync("SHOW INDEXES", cancellationToken: harness.Token)).ShouldHaveSingleItem()[3].ShouldBe("ix");
        var rules = await connection.QueryAsync("SHOW CONSTRAINTS", cancellationToken: harness.Token);
        rules.Count.ShouldBe(3);
        var invalid = await Should.ThrowAsync<GraphClientException>(async () => await connection.ExecuteAsync("INSERT (:Fresh)", cancellationToken: harness.Token));
        invalid.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        await connection.ExecuteAsync("DROP CONSTRAINT required", cancellationToken: harness.Token);
        await connection.ExecuteAsync("DROP CONSTRAINT typed IF EXISTS", cancellationToken: harness.Token);
        await connection.ExecuteAsync("DROP INDEX ix", cancellationToken: harness.Token);
        await connection.ExecuteAsync("DROP INDEX ix IF EXISTS", cancellationToken: harness.Token);
        await connection.ExecuteAsync("CREATE INDEX FOR (n:Fresh) ON (n.p)", cancellationToken: harness.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        var refused = await Should.ThrowAsync<GraphClientException>(async () => await connection.ExecuteAsync("CREATE INDEX other FOR (n:Refused) ON (n.p)", cancellationToken: harness.Token));
        refused.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04025");
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).Select(row => row[2]).ShouldBe(["Fresh"]);
        await connection.ExecuteAsync("INSERT (:Fresh)", cancellationToken: harness.Token);
        (await connection.QueryAsync("MATCH (n:Fresh) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
