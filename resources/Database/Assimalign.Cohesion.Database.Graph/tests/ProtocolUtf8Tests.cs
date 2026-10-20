using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class ProtocolUtf8Tests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Invalid UTF-8 is fatal in handshake and model requests")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Server_MalformedUtf8_ShouldSendProtocolViolationAndClose(bool startup)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var listener = new InMemoryConnectionListener();
        await using var engine = await GraphDatabaseEngine.CreateBuilder("utf8").AddDatabase("wireutf8")
            .AddServer(options => options.Listener = listener).BuildAsync(token);
        var server = engine.Servers.Single();
        await server.StartAsync(token);
        await using var transport = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, leaveOpen: true);
        byte[] payload = new ProtocolStartupMessage(ProtocolVersion.Current, "wireutf8", "x").Encode();
        if (startup) { payload[^1] = 0xFF; }

        // Act
        await WriteAsync(ProtocolMessageType.Startup, payload);
        if (!startup)
        {
            (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
            await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
            (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
            await WriteAsync((ProtocolMessageType)GraphProtocolMessageType.Execute, [0, 0, 0, 1, 0xFF, 0, 0, 0, 0]);
        }
        var response = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();

        // Assert
        response.Type.ShouldBe(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(response.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        (await channel.Reader.ReadFrameAsync(token)).ShouldBeNull();

        async Task WriteAsync(ProtocolMessageType type, byte[] bytes)
        {
            await channel.Writer.WriteFrameAsync(new(type, bytes), token);
            await channel.Writer.FlushAsync(token);
        }
    }
}