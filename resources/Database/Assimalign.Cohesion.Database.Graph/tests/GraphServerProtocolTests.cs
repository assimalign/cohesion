using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphServerProtocolTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Server: reserved transaction frame terminates the session")]
    public async Task Transaction_ReservedFrame_ShouldRejectAndClose()
    {
        await WithServerAsync(async (channel, token) =>
        {
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.Transaction, [], token);
            var error = await ReadErrorAsync(channel, token);
            error.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
            (await channel.Reader.ReadFrameAsync(token)).ShouldBeNull();
        });
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Server: invalid path requests fail without returning scalar frames")]
    [InlineData("MATCH (n) RETURN n.name")]
    [InlineData("MATCH (n) RETURN n,n")]
    [InlineData("CREATE (n:Injected) RETURN n")]
    [InlineData("MATCH (n) DETACH DELETE n")]
    [InlineData("SHOW LABELS")]
    public async Task ExecutePaths_InvalidShape_ShouldReturnOnlyErrorAndKeepSessionReady(string statement)
    {
        await WithServerAsync(async (channel, token) =>
        {
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ExecutePaths,
                GraphProtocolExecuteMessage.Create(statement).Encode(), token);
            (await ReadErrorAsync(channel, token)).Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);

            // Pong immediately follows Error: no scalar header or completion was substituted.
            await WriteAsync(channel, ProtocolMessageType.Ping, [], token);
            (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Pong);
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ExecutePaths,
                GraphProtocolExecuteMessage.Create("MATCH (n) RETURN n").Encode(), token);
            var complete = await ReadAsync(channel, token);
            complete.Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.PathsComplete);
            GraphProtocolPathsCompleteMessage.Decode(complete.Payload.Span).PathCount.ShouldBe(0);
        });
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Server: unsupported transaction text is a reusable parse failure")]
    [InlineData("BEGIN")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("")]
    public async Task Execute_UnsupportedText_ShouldReturnParseFailureAndKeepSessionReady(string statement)
    {
        await WithServerAsync(async (channel, token) =>
        {
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.Execute,
                GraphProtocolExecuteMessage.Create(statement).Encode(), token);
            (await ReadErrorAsync(channel, token)).Code.ShouldBe(ProtocolErrorCode.ParseFailure);
            await WriteAsync(channel, ProtocolMessageType.Ping, [], token);
            (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Pong);
        });
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Server: malformed parameter components terminate either exchange")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_MalformedParameter_ShouldReturnProtocolViolationAndClose(bool paths)
    {
        await WithServerAsync(async (channel, token) =>
        {
            var request = new GraphProtocolExecuteMessage("MATCH (n) RETURN n",
                new Dictionary<string, byte[]> { ["broken"] = [] });
            await WriteAsync(channel, (ProtocolMessageType)(paths ? GraphProtocolMessageType.ExecutePaths : GraphProtocolMessageType.Execute),
                request.Encode(), token);
            (await ReadErrorAsync(channel, token)).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
            (await channel.Reader.ReadFrameAsync(token)).ShouldBeNull();
        });
    }

    private static async Task WithServerAsync(Func<ProtocolChannel, CancellationToken, Task> action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create(new());
        await engine.CreateDatabaseAsync("graph", token);
        var listener = new InMemoryConnectionListener();
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using var connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), GraphProtocol.Family, leaveOpen: true);
        await WriteAsync(channel, ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "graph", "test").Encode(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, ProtocolMessageType.AuthenticateResponse, [], token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Ready);
        await action(channel, token);
    }

    private static async Task WriteAsync(ProtocolChannel channel, ProtocolMessageType type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => await channel.Reader.ReadFrameAsync(token) ?? throw new ProtocolException("Unexpected end of graph exchange.");

    private static async Task<ProtocolErrorMessage> ReadErrorAsync(ProtocolChannel channel, CancellationToken token)
    {
        var frame = await ReadAsync(channel, token);
        frame.Type.ShouldBe(ProtocolMessageType.Error);
        return ProtocolErrorMessage.Decode(frame.Payload.Span);
    }
}
