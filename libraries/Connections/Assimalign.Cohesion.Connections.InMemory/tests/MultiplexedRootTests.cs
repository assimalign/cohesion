using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Connections.InMemory.Tests;

public class MultiplexedRootTests
{
    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Root stream abort: Both directions retain their first code and the lifecycle ends")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Abort_WithStreamCode_ShouldEndBothDirectionsAndPreserveFirstCode(bool alreadyResetWrite, bool alreadyResetRead)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            await using MultiplexedStream sender = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream receiver = await server.AcceptStreamAsync(cancellation.Token);
            if (alreadyResetWrite)
            {
                sender.DirectionalAbort.ShouldNotBeNull().AbortWrite(17);
            }
            if (alreadyResetRead)
            {
                sender.DirectionalAbort.ShouldNotBeNull().AbortRead(19);
            }

            // Act
            sender.Abort(42, new InvalidOperationException("Local reason"));
            sender.Abort(43);
            await sender.DisposeAsync();

            // Assert
            ConnectionResetException read = await Should.ThrowAsync<ConnectionResetException>(
                async () => await receiver.Input.ReadAsync(cancellation.Token));
            ConnectionResetException write = await Should.ThrowAsync<ConnectionResetException>(
                async () => await receiver.Output.WriteAsync(new byte[] { 1 }, cancellation.Token));
            read.ApplicationErrorCode.ShouldBe(alreadyResetWrite ? 17 : 42);
            write.ApplicationErrorCode.ShouldBe(alreadyResetRead ? 19 : 42);
            sender.State.ShouldBe(ConnectionState.Aborted);
            sender.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
            receiver.IsInputCompleted.ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Root stream abort: Disposal retains its clean close")]
    public async Task Abort_AfterStreamDisposal_ShouldPreserveCleanEnd()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            await using MultiplexedStream sender = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream receiver = await server.AcceptStreamAsync(cancellation.Token);
            await sender.DisposeAsync();

            // Act
            sender.Abort(42);

            // Assert
            sender.State.ShouldBe(ConnectionState.Closed);
            receiver.IsInputCompleted.ShouldBeTrue();
            var read = await receiver.Input.ReadAsync(cancellation.Token);
            read.IsCompleted.ShouldBeTrue();
            receiver.Input.AdvanceTo(read.Buffer.End);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Input completion: Only clean peer output completion is known complete")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsInputCompleted_AfterPeerOutputCompletion_ShouldReflectCleanEnd(bool failed)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            await using MultiplexedStream sender = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
            await using MultiplexedStream receiver = await server.AcceptStreamAsync(cancellation.Token);
            receiver.IsInputCompleted.ShouldBeFalse();
            await sender.Output.WriteAsync(new byte[] { 1 }, cancellation.Token);

            // Act: FIN can be known before buffered bytes have been consumed.
            await sender.Output.CompleteAsync(failed ? new ConnectionAbortedException() : null);

            // Assert
            receiver.IsInputCompleted.ShouldBe(!failed);
            if (!failed)
            {
                (await receiver.Input.ReadExactlyAsync(1, cancellation.Token)).ShouldBe(new byte[] { 1 });
                receiver.DirectionalAbort.ShouldNotBeNull().AbortRead(42);
                receiver.IsInputCompleted.ShouldBeFalse();
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Coded abort: Roots reject codes outside the driver range")]
    [InlineData(-1L)]
    [InlineData(1L << 62)]
    public async Task Abort_WithInvalidRootCode_ShouldRejectWithoutClosing(long code)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using (client)
        await using (server)
        {
            await using MultiplexedStream stream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);

            // Act / Assert
            Should.Throw<ArgumentOutOfRangeException>(() => stream.Abort(code));
            Should.Throw<ArgumentOutOfRangeException>(() => client.Abort(code));
            stream.State.ShouldBe(ConnectionState.Open);
            client.State.ShouldBe(ConnectionState.Open);
        }
    }
}
