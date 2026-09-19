using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphProtocolTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Protocol: graph paths cross an in-memory connection")]
    public async Task Execute_Paths_ShouldPreserveElementsOverInMemory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph", token);
        await using var session = await database.CreateSessionAsync(token);
        var first = await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = "Alice" }, token);
        var last = await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = "Bob" }, token);
        var edge = await database.CreateRelationshipAsync(session, first.Id, last.Id, "KNOWS",
            new Dictionary<string, object?> { ["years"] = 3, ["confidence"] = 0.75m }, token);
        var pair = InMemoryConnectionPair.Create();
        await using var clientConnection = pair.Client;
        await using var serverConnection = pair.Server;
        await using var client = new ProtocolChannel(clientConnection.AsStream(), GraphProtocol.Family, leaveOpen: true);
        await using var server = new ProtocolChannel(serverConnection.AsStream(), GraphProtocol.Family, leaveOpen: true);

        Task serverTask = RespondAsync();
        await WriteAsync(client, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "graph", "test").Encode(), token);
        (await ReadAsync(client, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(client, ProtocolMessageType.AuthenticateResponse, [], token);
        (await ReadAsync(client, token)).Type.ShouldBe(ProtocolMessageType.Ready);
        await WriteAsync(client, (ProtocolMessageType)GraphProtocolMessageType.ExecutePaths,
            GraphProtocolExecuteMessage.Create("MATCH (a:Person)-[r:KNOWS]->(b:Person) RETURN a,r,b").Encode(), token);

        var frame = await ReadAsync(client, token);
        frame.Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.Path);
        var path = GraphProtocolPathMessage.Decode(frame.Payload.Span);
        path.Nodes.Count.ShouldBe(2);
        path.Nodes[0].Id.ShouldBe(first.Id);
        path.Nodes[0].Labels.ShouldBe(["Person"]);
        path.Nodes[0].Properties["name"].ShouldBe("Alice");
        path.Nodes[1].Id.ShouldBe(last.Id);
        path.Nodes[1].Properties["name"].ShouldBe("Bob");
        var relationship = path.Relationships.ShouldHaveSingleItem();
        relationship.Id.ShouldBe(edge.Id);
        relationship.From.ShouldBe(first.Id);
        relationship.To.ShouldBe(last.Id);
        relationship.Type.ShouldBe("KNOWS");
        relationship.Properties["years"].ShouldBe(3);
        relationship.Properties["confidence"].ShouldBe(0.75m);
        var complete = await ReadAsync(client, token);
        complete.Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.PathsComplete);
        GraphProtocolPathsCompleteMessage.Decode(complete.Payload.Span).PathCount.ShouldBe(1);
        await WriteAsync(client, ProtocolMessageType.Terminate, [], token);
        await serverTask;

        async Task RespondAsync()
        {
            var startup = await ReadAsync(server, token);
            startup.Type.ShouldBe(ProtocolMessageType.Startup);
            ProtocolStartupMessage.Decode(startup.Payload.Span).Database.ShouldBe("graph");
            await WriteAsync(server, ProtocolMessageType.Authenticate, [], token);
            (await ReadAsync(server, token)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
            await WriteAsync(server, ProtocolMessageType.Ready, [], token);
            var request = await ReadAsync(server, token);
            request.Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ExecutePaths);
            var message = GraphProtocolExecuteMessage.Decode(request.Payload.Span);
            await using var results = (QueryResultSet)await session.ExecuteAsync(GraphQueryRequest.FromGql(message.Statement), token);
            long count = 0;
            await foreach (var item in results.GetRowsAsync(token))
            {
                var path = new GraphProtocolPathMessage([(GraphNode)item.GetValue(0)!, (GraphNode)item.GetValue(2)!],
                    [(GraphRelationship)item.GetValue(1)!]);
                await WriteAsync(server, (ProtocolMessageType)GraphProtocolMessageType.Path, path.Encode(), token);
                count++;
            }
            await WriteAsync(server, (ProtocolMessageType)GraphProtocolMessageType.PathsComplete,
                new GraphProtocolPathsCompleteMessage(count).Encode(), token);
            (await ReadAsync(server, token)).Type.ShouldBe(ProtocolMessageType.Terminate);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Protocol: paths preserve reverse direction and all scalar property types")]
    public void Encode_ReversePathWithScalarProperties_ShouldPreserveIdentityAndTypes()
    {
        object?[] values = [null, false, true, "雪", byte.MaxValue, sbyte.MinValue, short.MinValue, ushort.MaxValue,
            int.MinValue, uint.MaxValue, long.MinValue, ulong.MaxValue, float.Epsilon, double.MaxValue, decimal.MaxValue];
        var properties = new Dictionary<string, object?>();
        for (int i = 0; i < values.Length; i++) { properties.Add(i.ToString(), values[i]); }
        var first = new GraphNode(new(ulong.MaxValue), ["Node"], properties);
        var last = new GraphNode(new(2), [], new Dictionary<string, object?>());
        var reverse = new GraphRelationship(new(3), "BACK", last.Id, first.Id, new Dictionary<string, object?>());
        var path = GraphProtocolPathMessage.Decode(new GraphProtocolPathMessage([first, last], [reverse]).Encode());
        path.Relationships[0].From.ShouldBe(last.Id);
        path.Relationships[0].To.ShouldBe(first.Id);
        path.Nodes[0].Id.ShouldBe(first.Id);
        foreach (var (name, value) in properties)
        {
            path.Nodes[0].Properties[name].ShouldBe(value);
            path.Nodes[0].Properties[name]?.GetType().ShouldBe(value?.GetType());
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Protocol: invalid paths and malformed lengths are rejected")]
    public void Decode_InvalidPath_ShouldReject()
    {
        var node = new GraphNode(new(1), [], new Dictionary<string, object?>());
        Should.Throw<ProtocolException>(() => new GraphProtocolPathMessage([], []).Encode());
        Should.Throw<ProtocolException>(() => new GraphProtocolPathMessage([node, node], []).Encode());
        Should.Throw<ProtocolException>(() => new GraphProtocolPathMessage([node, node],
            [new(new(3), "BAD", node.Id, new(2), new Dictionary<string, object?>())]).Encode());
        Should.Throw<ProtocolException>(() => GraphProtocolPathMessage.Decode([127, 255, 255, 255]));
        Should.Throw<ProtocolException>(() => GraphProtocolExecuteMessage.Decode([0, 0, 0, 0, 127, 255, 255, 255]));
        var payload = new GraphProtocolPathMessage([node], []).Encode();
        Should.Throw<ProtocolException>(() => GraphProtocolPathMessage.Decode([.. payload, 0]));
        Should.Throw<ProtocolException>(() => GraphProtocolPathsCompleteMessage.Decode(new byte[9]));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Protocol: catalog header counts are bounded before allocation")]
    public void Decode_OversizedCatalogHeaderCount_ShouldReject()
    {
        Should.Throw<ProtocolException>(() => GraphProtocolResultHeaderMessage.Decode([127, 255, 255, 255]));
        Should.Throw<ProtocolException>(() => GraphProtocolResultHeaderMessage.Decode([0, 0, 0, 1, 0, 0, 0, 0]));
        GraphProtocolResultHeaderMessage.Decode([0, 0, 0, 0]).Columns.ShouldBeEmpty();
    }

    private static async Task WriteAsync(ProtocolChannel channel, ProtocolMessageType type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => await channel.Reader.ReadFrameAsync(token) ?? throw new ProtocolException("Unexpected end of graph exchange.");
}
