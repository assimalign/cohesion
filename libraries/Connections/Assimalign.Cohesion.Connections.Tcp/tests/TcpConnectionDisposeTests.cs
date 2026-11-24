using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

/// <summary>
/// Pins the close contract of the TCP driver (#1086): <see cref="IAsyncDisposable.DisposeAsync"/> delivers
/// what the application flushed before the call, cannot hang on a peer that stops reading, and
/// <see cref="IConnection.Abort(Exception?)"/> still discards what has not been sent.
/// </summary>
public class TcpConnectionDisposeTests
{
    // A maximal default HTTP/2 frame: a 16,384-byte payload behind its 9-byte header.
    private const int FrameSize = 16_393;
    private const int Iterations = 500;

    // Far more than loopback socket buffers hold once the peer pins its receive buffer small.
    private const int UnsendableSize = 8 * 1024 * 1024;

    private static readonly TimeSpan _closeTimeout = TimeSpan.FromMilliseconds(250);

    [Theory(DisplayName = "Cohesion Test [Connections.Tcp] - DisposeAsync: Should deliver every flushed byte, then end of stream")]
    [InlineData(false, 1)]
    [InlineData(false, FrameSize)]
    [InlineData(false, 64 * 1024)]
    [InlineData(true, 1)]
    [InlineData(true, FrameSize)]
    [InlineData(true, 64 * 1024)]
    public async Task DisposeAsync_ImmediatelyAfterFlush_ShouldDeliverEveryByteThenEndOfStream(bool unixDomainSocket, int size)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(120));
        string? path = unixDomainSocket ? UnixSocketPath.Create() : null;
        EndPoint endPoint = path is null ? new IPEndPoint(IPAddress.Loopback, 0) : new UnixDomainSocketEndPoint(path);
        byte[] payload = CreatePayload(size);
        int failures = 0;
        string? firstFailure = null;

        try
        {
            await using TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = endPoint);
            await listener.BindAsync(cancellation.Token);

            // Act
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                ValueTask<IDuplexConnection> accepting = listener.AcceptAsync(cancellation.Token);
                using Socket peer = CreateSocket(listener.EndPoint!);
                await peer.ConnectAsync(listener.EndPoint!, cancellation.Token);
                IDuplexConnection server = await accepting;
                Task<byte[]> reading = ReceiveToEndAsync(peer, cancellation.Token);

                // The server returns from the flush and disposes at once: nothing waits for the send loop.
                await server.Output.WriteAsync(payload, cancellation.Token);
                await server.DisposeAsync();

                string? failure = await DescribeFailureAsync(reading, payload, server.State);
                if (failure is not null)
                {
                    failures++;
                    firstFailure ??= $"iteration {iteration}: {failure}";
                }
            }
        }
        finally
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        // Assert
        failures.ShouldBe(0, firstFailure);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - DisposeAsync: Should abort within the close timeout when the peer never reads")]
    public async Task DisposeAsync_WhenPeerNeverReads_ShouldAbortWithinCloseTimeout()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        await using RawLoopback loopback = await RawLoopback.CreateAsync(cancellation.Token);

        // No write threshold, so the flush completes while the send loop is still stuck on a full socket.
        await loopback.Connection.Output.WriteAsync(new byte[UnsendableSize], cancellation.Token);

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        await loopback.Connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        elapsed.Stop();

        // Assert
        loopback.Connection.State.ShouldBe(ConnectionState.Aborted);
        loopback.Connection.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(_closeTimeout - TimeSpan.FromMilliseconds(50));
        elapsed.Elapsed.ShouldBeLessThan(_closeTimeout + TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Abort: Should discard unsent output and signal ConnectionClosed")]
    public async Task Abort_AfterFlush_ShouldDiscardUnsentOutputAndSignalConnectionClosed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        await using RawLoopback loopback = await RawLoopback.CreateAsync(cancellation.Token);
        await loopback.Connection.Output.WriteAsync(new byte[UnsendableSize], cancellation.Token);

        // Act
        loopback.Connection.Abort();
        long received = await CountToEndAsync(loopback.Peer, cancellation.Token);

        // Assert
        loopback.Connection.State.ShouldBe(ConnectionState.Aborted);
        loopback.Connection.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        received.ShouldBeLessThan(UnsendableSize);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Complete: Should close gracefully, not abort, once the output drains")]
    public async Task Complete_OnOutput_ShouldMoveToClosingOnceDrained()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await using RawLoopback loopback = await RawLoopback.CreateAsync(cancellation.Token);
        byte[] payload = CreatePayload(FrameSize);
        Task<byte[]> reading = ReceiveToEndAsync(loopback.Peer, cancellation.Token);

        // Act
        await loopback.Connection.Output.WriteAsync(payload, cancellation.Token);
        await loopback.Connection.Output.CompleteAsync();
        byte[] received = await reading;
        while (loopback.Connection.State == ConnectionState.Open)
        {
            await Task.Delay(1, cancellation.Token);
        }

        ConnectionState drained = loopback.Connection.State;
        await loopback.Connection.DisposeAsync();

        // Assert
        received.ShouldBe(payload);
        drained.ShouldBe(ConnectionState.Closing);
        loopback.Connection.State.ShouldBe(ConnectionState.Closed);
    }

    private static byte[] CreatePayload(int size)
    {
        byte[] payload = new byte[size];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }

    private static Socket CreateSocket(EndPoint endPoint)
    {
        return endPoint is UnixDomainSocketEndPoint
            ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            : new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    }

    private static async Task<string?> DescribeFailureAsync(Task<byte[]> reading, byte[] expected, ConnectionState state)
    {
        byte[] received;
        try
        {
            received = await reading;
        }
        catch (SocketException exception)
        {
            return $"the peer's read failed with {exception.SocketErrorCode}";
        }

        if (received.Length != expected.Length)
        {
            return $"the peer read {received.Length} of {expected.Length} bytes before the end of stream";
        }

        if (!received.AsSpan().SequenceEqual(expected))
        {
            return "the peer read the right number of bytes, but not the bytes written";
        }

        return state == ConnectionState.Closed ? null : $"the connection ended {state}, not Closed";
    }

    private static async Task<byte[]> ReceiveToEndAsync(Socket socket, CancellationToken cancellationToken)
    {
        using MemoryStream received = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (count == 0)
            {
                return received.ToArray();
            }

            received.Write(buffer, 0, count);
        }
    }

    private static async Task<long> CountToEndAsync(Socket socket, CancellationToken cancellationToken)
    {
        long total = 0;
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int count = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                if (count == 0)
                {
                    return total;
                }

                total += count;
            }
        }
        catch (SocketException)
        {
            // A reset ends the stream as surely as a FIN; what arrived before it is what counts.
            return total;
        }
    }

    /// <summary>
    /// A driver connection over an accepted loopback socket and the raw client socket at its other end.
    /// The driver is constructed directly so the test controls its close timeout and write threshold,
    /// and both socket buffers are pinned small so a peer that does not read stalls the send loop.
    /// </summary>
    private sealed class RawLoopback : IAsyncDisposable
    {
        private RawLoopback(TcpConnection connection, Socket peer)
        {
            Connection = connection;
            Peer = peer;
        }

        public TcpConnection Connection { get; }

        public Socket Peer { get; }

        public static async Task<RawLoopback> CreateAsync(CancellationToken cancellationToken)
        {
            using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            Socket peer = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveBufferSize = 4096
            };

            try
            {
                await peer.ConnectAsync(listener.LocalEndPoint!, cancellationToken);
                Socket accepted = await listener.AcceptAsync(cancellationToken);
                accepted.SendBufferSize = 4096;

                SocketPipeOptionsContext pipeOptions = SocketPipeOptionsFactory.CreateSocketPipeOptions(
                    maxReadBufferSize: null,
                    maxWriteBufferSize: null,
                    unsafePreferInLineScheduling: false);
                TcpConnectionSettings settings = new()
                {
                    PipeOptions = pipeOptions,
                    CloseTimeout = _closeTimeout
                };

                return new RawLoopback(new TcpConnection(accepted, settings, default, pipeOptions), peer);
            }
            catch
            {
                peer.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            Peer.Dispose();
        }
    }
}
