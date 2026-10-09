using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// A raw TCP peer that completes the wire handshake over a real socket and then resets the
/// connection (an RST, not a FIN), the way a client process that dies or a middlebox that drops
/// the flow ends it. The in-memory driver cannot produce a reset.
/// </summary>
internal static class TcpResetPeer
{
    /// <summary>
    /// Connects to the server and runs the startup, authenticate and ready handshake.
    /// </summary>
    /// <param name="endPoint">The server's bound endpoint.</param>
    /// <param name="database">The database the startup names.</param>
    /// <param name="cancellationToken">Cancellation token for the handshake.</param>
    /// <returns>The connected socket, in the server's ready state.</returns>
    public static async Task<Socket> ConnectReadyAsync(EndPoint endPoint, string database, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await using ProtocolFrameReader reader = ProtocolFrameReader.Create(stream, leaveOpen: true);
            await using ProtocolFrameWriter writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);

            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, database, "tester").Encode()), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            ProtocolFrame? challenge = await reader.ReadFrameAsync(cancellationToken);
            challenge.ShouldNotBeNull();
            challenge.Value.Type.ShouldBe(ProtocolMessageType.Authenticate);
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            ProtocolFrame? ready = await reader.ReadFrameAsync(cancellationToken);
            ready.ShouldNotBeNull();
            ready.Value.Type.ShouldBe(ProtocolMessageType.Ready);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts sending pings the peer never reads the pongs of, so the server's pong writes fill the
    /// socket buffers and its pump blocks in a write: the reset that follows then fails the server's
    /// send, mid-session, instead of ending its next read. The returned send is never awaited
    /// successfully once the socket resets.
    /// </summary>
    /// <param name="socket">The connected socket, in the server's ready state.</param>
    /// <param name="frames">How many ping frames to send.</param>
    /// <returns>The pending send.</returns>
    public static Task FloodPingsAsync(Socket socket, int frames)
    {
        byte[] wire = new byte[ProtocolFrameHeader.Size * frames];
        for (int index = 0; index < frames; index++)
        {
            new ProtocolFrameHeader(ProtocolMessageType.Ping, 0).WriteTo(wire.AsSpan(index * ProtocolFrameHeader.Size));
        }

        return socket.SendAsync(wire, SocketFlags.None);
    }

    /// <summary>
    /// Resets the connection: a zero linger makes the close send an RST and discard what is
    /// unsent, so the server's next receive or send fails with a connection reset.
    /// </summary>
    /// <param name="socket">The connected socket.</param>
    public static void Reset(Socket socket)
    {
        socket.LingerState = new LingerOption(enable: true, seconds: 0);
        socket.Close();
    }
}
