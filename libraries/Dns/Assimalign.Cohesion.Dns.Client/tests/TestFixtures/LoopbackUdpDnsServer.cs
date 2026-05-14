using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// A loopback UDP "DNS" server that bounces an arbitrary canned response back to whoever sends
/// it a datagram. Used to exercise <see cref="UdpDnsTransport"/> without hitting the real
/// network.
/// </summary>
internal sealed class LoopbackUdpDnsServer : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _shutdownCts;
    private readonly Task _loop;

    private Func<byte[], byte[]> _responder = _ => Array.Empty<byte>();

    public LoopbackUdpDnsServer()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        _shutdownCts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_shutdownCts.Token));
    }

    public IPEndPoint EndPoint { get; }

    /// <summary>
    /// Sets the responder. Called once per inbound datagram with the raw request bytes;
    /// returns the bytes to send back (empty = drop, simulating a black-holed server).
    /// </summary>
    public void OnRequest(Func<byte[], byte[]> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[65_535];
        EndPoint remoteAny = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteAny, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            byte[] request = buffer[..result.ReceivedBytes];
            byte[] response = _responder(request);
            if (response.Length == 0)
            {
                continue;
            }

            try
            {
                await _socket.SendToAsync(response, SocketFlags.None, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // Ignore — the test client may have disposed already.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        try
        {
            _socket.Close();
        }
        catch
        {
        }
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
        }
        _shutdownCts.Dispose();
        _socket.Dispose();
    }

    /// <summary>
    /// Builds a minimal DNS response (header only, all sections empty, NoError) carrying the
    /// supplied transaction ID. Useful when the test only cares about transport behavior.
    /// </summary>
    public static byte[] BuildMinimalResponse(ushort id, bool truncated = false)
    {
        // 12-octet header + nothing else.
        byte[] msg = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(0, 2), id);
        // Flags: QR=1 (response), OpCode=Query=0, AA=0, TC=truncated, RD=0, RA=1, RCODE=NoError.
        ushort flags = 0x8080;
        if (truncated)
        {
            flags |= 0x0200;
        }
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2, 2), flags);
        // QDCOUNT = ANCOUNT = NSCOUNT = ARCOUNT = 0
        return msg;
    }
}
