using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphMergeWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - MERGE whole patterns, branch SET and overlays round-trip on both transports (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Merge_ShouldRoundTripBranchesAndCounts(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var token = harness.Token;
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act / Assert
        var result = await connection.QueryAsync("FOR x IN [1,1,1] MERGE (n:P {id:x}) ON CREATE SET n.hits=1 ON MATCH SET n.hits=n.hits+1 RETURN n.hits", cancellationToken: token);
        result.Select(row => row[0]).ShouldBe(new object?[] { 3L, 3L, 3L });
        result.AffectedCount.ShouldBe(1);
        (await connection.ExecuteAsync("MERGE (:P {id:1})-[:R]-(:P {id:2})", cancellationToken: token)).ShouldBe(3);
        (await connection.ExecuteAsync("MERGE (:P {id:2})-[:R]->(:P {id:1})", cancellationToken: token)).ShouldBe(3);
        (await connection.ExecuteAsync("MERGE (:P {id:2})-[:R]-(:P {id:1})", cancellationToken: token)).ShouldBe(0);
        var bound = await connection.QueryAsync("MATCH (a:P {id:1}) MERGE (a)-[r:Bound]->(b:Q {id:3}) ON CREATE SET r.x=1 RETURN b.id", cancellationToken: token);
        bound.Count.ShouldBe(3);
        bound.AffectedCount.ShouldBe(6);
        var explained = await connection.QueryAsync("EXPLAIN MERGE (n:Never) ON CREATE SET n.x=1 ON MATCH SET n.x=2", cancellationToken: token);
        explained.Select(row => row[2]).ShouldContain("Merge");
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - MERGE failures preserve codes and the authenticated session (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_InvalidMerge_ShouldRetainDiagnosticsAndSession(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        var cases = new (string Query, string Code, ProtocolErrorCode Kind)[]
        {
            ("MERGE (n:P {id:NULL})", "CG03004", ProtocolErrorCode.ExecutionFailure),
            ("OPTIONAL MATCH (n:Absent) MERGE (n)-[:R]->()", "CG03005", ProtocolErrorCode.ExecutionFailure),
            ("MERGE ()-[:R*1..2]->()", "CG00002", ProtocolErrorCode.ParseFailure),
            ("MERGE (n:P) ON MATCH SET missing.x=1", "CG05001", ProtocolErrorCode.ExecutionFailure),
        };

        // Act / Assert
        foreach (var item in cases)
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.ExecuteAsync(item.Query, cancellationToken: harness.Token));
            error.Code.ShouldBe(item.Kind);
            error.Diagnostics[0].Code.ShouldBe(item.Code);
            (await connection.QueryAsync("RETURN 7", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(7L);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        }
        var pathError = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await foreach (var path in connection.QueryPathsAsync("MERGE (n:NoPathWrite) RETURN n", cancellationToken: harness.Token)) { }
        });
        pathError.Diagnostics[0].Code.ShouldBe("CG05001");
        (await connection.QueryAsync("MATCH (n) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - MERGE stale Snapshot conflicts survive both transports (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_StaleSnapshot_ShouldReportRetryableConflictAndRequireRollback(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await using var writer = await harness.Database.CreateSessionAsync(cancellationToken: harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await writer.ExecuteAsync("INSERT (:P {id:7})", cancellationToken: harness.Token);

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("MERGE (:P {id:7})", cancellationToken: harness.Token));

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CG04024");
        var aborted = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("RETURN 1", cancellationToken: harness.Token));
        aborted.Diagnostics[0].Code.ShouldBe("CG04007");
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.QueryAsync("MATCH (n:P) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }

}
