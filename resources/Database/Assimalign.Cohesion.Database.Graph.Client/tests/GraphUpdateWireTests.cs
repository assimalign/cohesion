using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphUpdateWireTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - SET/REMOVE: counts, values and failures preserve the ready session (#1149)")]
    public async Task Execute_Updates_ShouldRoundTripCountsAndRefreshValues()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:P {x:1})-[:R]->(:P {x:1})", cancellationToken: harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act / Assert
        (await connection.ExecuteAsync("MATCH (a:P) SET a.x=$value, a:Admin",
            new Dictionary<string, object?> { ["value"] = 2 }, harness.Token)).ShouldBe(2);
        var rows = await connection.QueryAsync("MATCH (a:P) SET a.x=$value RETURN a.x",
            new Dictionary<string, object?> { ["value"] = 3 }, harness.Token);
        rows.Select(row => row[0]).ShouldBe([3, 3]);
        rows.AffectedCount.ShouldBe(2);
        (await connection.ExecuteAsync("MATCH (a:Admin) REMOVE a:Admin", cancellationToken: harness.Token)).ShouldBe(2);
        (await connection.QueryAsync("MATCH (a:Admin) RETURN a.x", cancellationToken: harness.Token)).ShouldBeEmpty();
        var failure = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("MATCH ()-[r:R]->() SET r:X", cancellationToken: harness.Token));
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldContain("COHDBG003", Case.Sensitive);
        (await connection.QueryAsync("MATCH (a:P) RETURN a.x", cancellationToken: harness.Token)).Count.ShouldBe(2);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
