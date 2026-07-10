using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

public class NamedPipeConnectionRoundTripTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - RoundTrip: Should carry bytes in both directions across multiple exchanges")]
    public async Task ClientAndServer_ShouldExchangeBytesBidirectionally()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        NamedPipeEndPoint endPoint = new(NamedPipeTestName.Create());

        await using NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = endPoint);

        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        NamedPipeConnectionFactory factory = new();

        await using Connection client = await factory.ConnectAsync(endPoint, cancellation.Token);
        await using Connection server = await acceptTask;

        // Both ends advertise the named-pipe protocol.
        client.Capabilities.Protocol.ShouldBe(ConnectionProtocol.NamedPipe);
        server.Capabilities.Protocol.ShouldBe(ConnectionProtocol.NamedPipe);

        // Act / Assert — client -> server
        await client.Output.WriteAsync(Encoding.UTF8.GetBytes("ping"), cancellation.Token);
        await client.Output.FlushAsync(cancellation.Token);

        byte[] received = await server.Input.ReadExactlyAsync(4, cancellation.Token);
        Encoding.UTF8.GetString(received).ShouldBe("ping");

        // Act / Assert — server -> client (a second exchange proves the connection stays live)
        await server.Output.WriteAsync(Encoding.UTF8.GetBytes("pong!"), cancellation.Token);
        await server.Output.FlushAsync(cancellation.Token);

        byte[] echoed = await client.Input.ReadExactlyAsync(5, cancellation.Token);
        Encoding.UTF8.GetString(echoed).ShouldBe("pong!");
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - RoundTrip: Should serve two clients on the same pipe name in sequence")]
    public async Task Listener_ShouldAcceptSuccessiveClientsOnSamePipeName()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        NamedPipeEndPoint endPoint = new(NamedPipeTestName.Create());

        await using NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = endPoint);

        NamedPipeConnectionFactory factory = new();

        // Act / Assert — first client
        ValueTask<Connection> firstAccept = listener.AcceptAsync(cancellation.Token);
        await using (Connection firstClient = await factory.ConnectAsync(endPoint, cancellation.Token))
        await using (Connection firstServer = await firstAccept)
        {
            await firstClient.Output.WriteAsync(Encoding.UTF8.GetBytes("one"), cancellation.Token);
            await firstClient.Output.FlushAsync(cancellation.Token);
            Encoding.UTF8.GetString(await firstServer.Input.ReadExactlyAsync(3, cancellation.Token)).ShouldBe("one");
        }

        // Act / Assert — second client on the same name after the first closed
        ValueTask<Connection> secondAccept = listener.AcceptAsync(cancellation.Token);
        await using (Connection secondClient = await factory.ConnectAsync(endPoint, cancellation.Token))
        await using (Connection secondServer = await secondAccept)
        {
            await secondClient.Output.WriteAsync(Encoding.UTF8.GetBytes("two"), cancellation.Token);
            await secondClient.Output.FlushAsync(cancellation.Token);
            Encoding.UTF8.GetString(await secondServer.Input.ReadExactlyAsync(3, cancellation.Token)).ShouldBe("two");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - RoundTrip: Should surface end-of-stream when the peer disposes")]
    public async Task Input_WhenPeerDisposes_ShouldObserveCompletion()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        NamedPipeEndPoint endPoint = new(NamedPipeTestName.Create());

        await using NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = endPoint);

        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        NamedPipeConnectionFactory factory = new();

        Connection client = await factory.ConnectAsync(endPoint, cancellation.Token);
        await using Connection server = await acceptTask;

        // Act — the client half-closes by disposing.
        await client.DisposeAsync();

        // Assert — the server observes end-of-stream on its next read.
        byte[] remainder = await server.Input.ReadToEndAsync(cancellation.Token);
        remainder.Length.ShouldBe(0);
    }
}
