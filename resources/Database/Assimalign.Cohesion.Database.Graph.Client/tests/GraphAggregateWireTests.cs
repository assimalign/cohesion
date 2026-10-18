using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphAggregateWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Aggregates: scalar types and negotiated lists round-trip over both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_Aggregates_ShouldPreserveNamesTypesAndGroupingAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name:'Alice', age:42, active:TRUE}), (:Person {name:'Bob', age:17, active:FALSE}), (:Person {name:'Isolated', age:30})", cancellationToken: harness.Token);
        // Act
        var result = await connection.QueryAsync("MATCH (a:Person) RETURN count(*) AS n, count(a.name) AS names, count(DISTINCT a.active) AS flags, sum(a.age) AS total, avg(a.age) AS mean, collect_list(a.active) AS active", cancellationToken: harness.Token);
        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(["n", "names", "flags", "total", "mean", "active"]);
        result.Columns.Take(3).ShouldAllBe(column => column.Type == DatabaseType.Int64);
        var row = result.ShouldHaveSingleItem();
        row[0].ShouldBeOfType<long>().ShouldBe(3L); row[1].ShouldBeOfType<long>().ShouldBe(3L); row[2].ShouldBeOfType<long>().ShouldBe(2L);
        row[3].ShouldBeOfType<long>().ShouldBe(89L); row[4].ShouldBeOfType<double>().ShouldBe(89d / 3, 1e-12);
        row[5].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { true, false });
        var decimals = await connection.QueryAsync("MATCH (a:Person) RETURN sum($d) AS total, avg($d) AS mean", new Dictionary<string, object?> { ["d"] = 0.1m }, harness.Token);
        decimals.ShouldHaveSingleItem()[0].ShouldBeOfType<decimal>().ShouldBe(0.3m);
        decimals[0][1].ShouldBeOfType<decimal>().ShouldBe(0.1m);
        var empty = await connection.QueryAsync("MATCH (a:Person {name:'Nobody'}) RETURN count(*) AS n, collect(a.name) AS names", cancellationToken: harness.Token);
        empty.Columns[0].Type.ShouldBe(DatabaseType.Int64);
        empty.ShouldHaveSingleItem()[0].ShouldBeOfType<long>().ShouldBe(0L); empty[0][1].ShouldBeOfType<object?[]>().ShouldBeEmpty();
        var grouped = await connection.QueryAsync("MATCH (a:Person) RETURN a.active, count(*) AS n GROUP BY a.active ORDER BY a.active NULLS LAST LIMIT 2", cancellationToken: harness.Token);
        grouped.Count.ShouldBe(2); grouped[0].ToArray().ShouldBe(new object?[] { false, 1L }); grouped[1].ToArray().ShouldBe(new object?[] { true, 1L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Aggregates: overflow precedes ResultHeader and retains the authenticated pooled session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_AggregateOverflow_ShouldReuseExactPooledSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        Guid sessionId;
        await using (var connection = await harness.Client.ConnectAsync(harness.Token))
        {
            await connection.ExecuteAsync("INSERT (:Value {n:1}), (:Value {n:2})", cancellationToken: harness.Token);
            sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
            // Act
            var error = await Should.ThrowAsync<GraphClientException>(async () =>
                await connection.QueryAsync("MATCH (a:Value) RETURN sum(9223372036854775807)", cancellationToken: harness.Token));
            // Assert: the exchange only certifies reusable completion for an error before any header.
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
            connection.IsOpen.ShouldBeTrue();
        }
        await using var reused = await harness.Client.ConnectAsync(harness.Token);
        (await reused.QueryAsync("MATCH (a:Value) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Aggregates: ExecutePaths rejects aggregate projection and keeps the session reusable")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPaths_Aggregates_ShouldRetainPathDiagnosticAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await foreach (var path in connection.QueryPathsAsync("MATCH (a) RETURN count(a)", cancellationToken: harness.Token)) { }
        });
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        error.Message.ShouldContain("Path execution requires", Case.Sensitive);
        (await connection.QueryAsync("RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Aggregates: collect requires composite negotiation and errors are sent before a result header")]
    public async Task Execute_UnnegotiatedCollectAndOverflow_ShouldRejectBeforeHeaderAsync()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var local = await harness.Database.CreateSessionAsync(harness.Token);
        await local.ExecuteAsync("INSERT (:Value), (:Value)", cancellationToken: harness.Token);
        await using var transport = await harness.ConnectionFactory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "graph", "tester").Encode());
        (await Read()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await Send(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        channel.SetAgreement(ProtocolReadyMessage.Decode((await Read()).Payload.Span));
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (var (source, code) in new[]
        {
            ("RETURN collect_list(1)", "COHDBG016"),
            ("RETURN collect(1)", "COHDBG016"),
            ("RETURN sum('text')", "COHDBG013"),
            ("MATCH (a:Value) RETURN sum(9223372036854775807)", "COHDBG013"),
        })
        {
            await Execute(source);
            var frame = await Read(); frame.Type.ShouldBe(ProtocolMessageType.Error);
            var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure); error.Message.ShouldStartWith(code + ": ", Case.Sensitive);
        }
        await Execute("RETURN count(*) AS n");
        var header = await Read(); header.Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultHeader);
        (await Read()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultRow);
        (await Read()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultComplete);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);

        async Task Execute(string source) => await Send((ProtocolMessageType)GraphProtocolMessageType.Execute, new GraphProtocolExecuteMessage(source, new Dictionary<string, byte[]>()).Encode());
        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token); await channel.Writer.FlushAsync(harness.Token); }
        async Task<ProtocolFrame> Read() => (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
    }
}
