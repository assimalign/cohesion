using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class TcpConnectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WriteAsync_ClientToServerEcho_ShouldRoundTripPayload()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(cancellation.Token);

        byte[] payload = [1, 2, 3, 4, 5];

        // Act
        await pair.Client.Output.WriteAsync(payload, cancellation.Token);

        byte[] received = await ReadBytesAsync(pair.Server.Input, payload.Length, cancellation.Token);

        await pair.Server.Output.WriteAsync(received, cancellation.Token);

        byte[] echoed = await ReadBytesAsync(pair.Client.Input, payload.Length, cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        echoed.ShouldBe(payload);
    }

    [Fact]
    public async Task Complete_OnClientOutput_ShouldCompleteServerRead()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(cancellation.Token);

        byte[] payload = [10, 20, 30];

        await pair.Client.Output.WriteAsync(payload, cancellation.Token);

        byte[] received = await ReadBytesAsync(pair.Server.Input, payload.Length, cancellation.Token);

        // Act
        // Completing the output is the graceful half-close: the client's send loop drains, the
        // socket sends FIN, and the server's read side observes completion.
        pair.Client.Output.Complete();

        ReadResult result = await pair.Server.Input.ReadAsync(cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        result.IsCompleted.ShouldBeTrue();
        result.Buffer.IsEmpty.ShouldBeTrue();

        pair.Server.Input.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task Abort_OnLiveConnection_ShouldSignalConnectionClosedAndAbortedState()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(cancellation.Token);

        Task connectionClosed = WhenConnectionClosedAsync(pair.Client, cancellation.Token);

        // Act
        pair.Client.Abort();

        // Assert
        pair.Client.State.ShouldBe(ConnectionState.Aborted);

        // The closed signal is raised asynchronously once the pump loops have unwound.
        await connectionClosed;

        pair.Client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Direction_OnLoopbackPair_ShouldBeBidirectional()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        // Act
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(cancellation.Token);

        // Assert
        pair.Client.Direction.ShouldBe(ConnectionDirection.Bidirectional);
        pair.Server.Direction.ShouldBe(ConnectionDirection.Bidirectional);
    }

    [Fact]
    public async Task Id_AcrossTwoConnections_ShouldBeUnique()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        // Act
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(cancellation.Token);

        // Assert
        pair.Client.Id.ShouldNotBe(ConnectionId.Empty);
        pair.Server.Id.ShouldNotBe(ConnectionId.Empty);
        pair.Client.Id.ShouldNotBe(pair.Server.Id);
    }

    [Fact]
    public async Task EndPoints_OnLoopbackPair_ShouldBePopulatedAndConsistent()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        // Act
        await using LoopbackPair pair = await LoopbackPair.CreateAsync(cancellation.Token);

        // Assert
        pair.Client.LocalEndPoint.ShouldNotBeNull();
        pair.Client.RemoteEndPoint.ShouldNotBeNull();
        pair.Server.LocalEndPoint.ShouldNotBeNull();
        pair.Server.RemoteEndPoint.ShouldNotBeNull();

        pair.Client.LocalEndPoint.ShouldBe(pair.Server.RemoteEndPoint);
        pair.Client.RemoteEndPoint.ShouldBe(pair.Server.LocalEndPoint);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        await using LoopbackPair pair = await LoopbackPair.CreateAsync(cancellation.Token);

        // Act
        await pair.Client.DisposeAsync();

        Exception? exception = await Record.ExceptionAsync(async () => await pair.Client.DisposeAsync());

        // Assert
        exception.ShouldBeNull();
        pair.Client.State.ShouldBe(ConnectionState.Closed);
    }

    private static async Task<byte[]> ReadBytesAsync(PipeReader reader, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);

            if (result.Buffer.Length >= count)
            {
                byte[] bytes = result.Buffer.Slice(0, count).ToArray();

                reader.AdvanceTo(result.Buffer.GetPosition(count));

                return bytes;
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException($"The connection completed before {count} bytes were received.");
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    private static Task WhenConnectionClosedAsync(Connection connection, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.ConnectionClosed.Register(() => completion.TrySetResult());
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        return completion.Task;
    }

    private sealed class LoopbackPair : IAsyncDisposable
    {
        private LoopbackPair(TcpConnectionListener listener, Connection client, Connection server)
        {
            Listener = listener;
            Client = client;
            Server = server;
        }

        public TcpConnectionListener Listener { get; }

        public Connection Client { get; }

        public Connection Server { get; }

        public static async Task<LoopbackPair> CreateAsync(CancellationToken cancellationToken)
        {
            TcpConnectionListener listener = TcpConnectionListener.Create(
                options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

            try
            {
                // The listener binds lazily in the synchronous prefix of the first accept, so the
                // ephemeral endpoint is concrete once the pending accept task is handed back.
                ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellationToken);

                TcpConnectionFactory factory = new();

                Connection client = await factory.ConnectAsync(listener.EndPoint, cancellationToken);
                Connection server = await acceptTask;

                return new LoopbackPair(listener, client, server);
            }
            catch
            {
                await listener.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            await Listener.DisposeAsync();
        }
    }
}
