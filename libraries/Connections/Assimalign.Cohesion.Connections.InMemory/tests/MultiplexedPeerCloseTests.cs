using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Connections.InMemory.Tests;

public class MultiplexedPeerCloseTests
{
    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Peer close: Either end aborting or disposing terminates accepts and active streams with its code")]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Close_FromEitherEnd_ShouldNotifyPeerAndFailActiveStreams(bool closeFromClient, bool abort)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            MultiplexedConnection closer = closeFromClient ? client : server;
            MultiplexedConnection peer = closeFromClient ? server : client;
            await using MultiplexedStream local = await closer.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream inbound = await peer.AcceptStreamAsync(cancellation.Token);
            await using MultiplexedStream outbound = await peer.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream remote = await closer.AcceptStreamAsync(cancellation.Token);
            Task<MultiplexedStream> peerAccept = peer.AcceptStreamAsync(cancellation.Token).AsTask();
            Task<MultiplexedStream> localAccept = closer.AcceptStreamAsync(cancellation.Token).AsTask();
            Task<ReadResult> inboundRead = inbound.Input.ReadAsync(cancellation.Token).AsTask();
            Task<ReadResult> outboundRead = outbound.Input.ReadAsync(cancellation.Token).AsTask();
            long expectedCode = abort ? 42 : 0;

            // Act
            if (abort)
            {
                closer.Abort(expectedCode, new InvalidOperationException("Local close reason"));
            }
            else
            {
                await closer.DisposeAsync();
            }
            // Assert: notification must come from the driver, before either end is closed again.
            peer.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            closer.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            closer.Abort(99);
            peer.Abort(100);
            // A later close at either end cannot replace the first pair close.
            closer.State.ShouldBe(abort ? ConnectionState.Aborted : ConnectionState.Closed);
            peer.State.ShouldBe(ConnectionState.Aborted);
            await Should.ThrowAsync<OperationCanceledException>(() => peerAccept);
            await Should.ThrowAsync<OperationCanceledException>(() => localAccept);
            await Should.ThrowAsync<OperationCanceledException>(async () => await peer.AcceptStreamAsync(cancellation.Token));
            (await Should.ThrowAsync<ConnectionResetException>(() => inboundRead)).ApplicationErrorCode.ShouldBe(expectedCode);
            (await Should.ThrowAsync<ConnectionResetException>(() => outboundRead)).ApplicationErrorCode.ShouldBe(expectedCode);

            foreach (MultiplexedStream stream in new[] { local, inbound, outbound, remote })
            {
                stream.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
                stream.IsInputCompleted.ShouldBeFalse();
                (await Should.ThrowAsync<ConnectionResetException>(
                    async () => await stream.Input.ReadAsync(cancellation.Token))).ApplicationErrorCode.ShouldBe(expectedCode);
                (await Should.ThrowAsync<ConnectionResetException>(
                    async () => await stream.Output.WriteAsync(new byte[] { 1 }, cancellation.Token))).ApplicationErrorCode.ShouldBe(expectedCode);
            }
            cancellation.IsCancellationRequested.ShouldBeFalse();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Peer close: Queued streams fail without being accepted after close")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispose_WithQueuedStream_ShouldFailOpenerAndEndPeerAccept(bool closeFromClient)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            MultiplexedConnection opener = closeFromClient ? client : server;
            MultiplexedConnection peer = closeFromClient ? server : client;
            await using MultiplexedStream stream = await opener.OpenStreamAsync(cancellationToken: cancellation.Token);

            // Act
            await opener.DisposeAsync();

            // Assert
            peer.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            await Should.ThrowAsync<OperationCanceledException>(async () => await peer.AcceptStreamAsync(cancellation.Token));
            (await Should.ThrowAsync<ConnectionResetException>(
                async () => await stream.Input.ReadAsync(cancellation.Token))).ApplicationErrorCode.ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Peer close: Connection callbacks see coded loss and can dispose their stream")]
    public async Task Abort_WithReentrantPeerCallback_ShouldPublishLossBeforeNotification()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            await using MultiplexedStream local = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream remote = await server.AcceptStreamAsync(cancellation.Token);
            Exception? observed = null;
            bool called = false;
            using CancellationTokenRegistration registration = server.ConnectionClosed.Register(() =>
            {
                called = true;
                try
                {
                    remote.Output.GetMemory(1);
                }
                catch (ConnectionResetException exception)
                {
                    observed = exception;
                }
                remote.DisposeAsync().GetAwaiter().GetResult();
            });

            // Act
            client.Abort(73);

            // Assert
            called.ShouldBeTrue();
            observed.ShouldBeOfType<ConnectionResetException>().ApplicationErrorCode.ShouldBe(73);
            (await Should.ThrowAsync<ConnectionResetException>(
                async () => await remote.Input.ReadAsync(cancellation.Token))).ApplicationErrorCode.ShouldBe(73);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Peer close: No-code abort uses zero from either end")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Abort_WithoutCode_ShouldReportZeroConnectionLoss(bool closeFromClient)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            MultiplexedConnection closer = closeFromClient ? client : server;
            MultiplexedConnection peer = closeFromClient ? server : client;
            await using MultiplexedStream local = await closer.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream remote = await peer.AcceptStreamAsync(cancellation.Token);

            // Act
            closer.Abort(new InvalidOperationException("Only the code crosses the connection"));

            // Assert
            peer.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            (await Should.ThrowAsync<ConnectionResetException>(
                async () => await remote.Input.ReadAsync(cancellation.Token))).ApplicationErrorCode.ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Peer close: A prior FIN does not hide loss on an active stream")]
    public async Task IsInputCompleted_AfterParentLossFollowingFin_ShouldReportFalse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            await using MultiplexedStream local = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream remote = await server.AcceptStreamAsync(cancellation.Token);
            await local.Output.CompleteAsync();
            remote.IsInputCompleted.ShouldBeTrue();

            // Act
            client.Abort(81);

            // Assert
            remote.IsInputCompleted.ShouldBeFalse();
            (await Should.ThrowAsync<ConnectionResetException>(
                async () => await remote.Input.ReadAsync(cancellation.Token))).ApplicationErrorCode.ShouldBe(81);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Peer close: Throwing callbacks cannot strand peer or stream notifications")]
    public async Task Abort_WithThrowingCallback_ShouldStillClosePeerAndStreams()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            await using MultiplexedStream local = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream remote = await server.AcceptStreamAsync(cancellation.Token);
            using CancellationTokenRegistration registration = client.ConnectionClosed.Register(
                () => throw new InvalidOperationException("A consumer callback failed"));

            // Act
            Should.NotThrow(() => client.Abort(89));

            // Assert
            server.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            local.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            remote.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            (await Should.ThrowAsync<ConnectionResetException>(
                async () => await remote.Input.ReadAsync(cancellation.Token))).ApplicationErrorCode.ShouldBe(89);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Peer close: Racing stream creation cannot escape connection loss")]
    public async Task OpenStreamAsync_RacingPeerAbort_ShouldRejectOrReturnATerminatedStream()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        for (int attempt = 0; attempt < 16; attempt++)
        {
            (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
            await using (client)
            await using (server)
            {
                TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                MultiplexedStream? opened = null;
                ConnectionException? rejected = null;
                Task open = Task.Run(async () =>
                {
                    await start.Task;
                    try
                    {
                        opened = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
                    }
                    catch (ConnectionException exception)
                    {
                        rejected = exception;
                    }
                });
                Task close = Task.Run(async () =>
                {
                    await start.Task;
                    server.Abort(91);
                });

                // Act
                start.SetResult();
                await Task.WhenAll(open, close).WaitAsync(cancellation.Token);

                // Assert
                client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
                if (opened is not null)
                {
                    await using (opened)
                    {
                        (await Should.ThrowAsync<ConnectionResetException>(
                            async () => await opened.Input.ReadAsync(cancellation.Token))).ApplicationErrorCode.ShouldBe(91);
                    }
                }
                else
                {
                    rejected.ShouldBeOfType<ConnectionResetException>().ApplicationErrorCode.ShouldBe(91);
                }
            }
        }
    }
}
