using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// A loopback TCP "DNS" server that frames responses with the two-octet RFC 1035 &#167; 4.2.2
/// length prefix. Used to exercise <see cref="TcpDnsTransport"/>.
/// </summary>
internal sealed class LoopbackTcpDnsServer : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _shutdownCts;
    private readonly Task _loop;

    private Func<byte[], byte[]> _responder = _ => Array.Empty<byte>();
    private bool _holdConnection;

    public LoopbackTcpDnsServer()
    {
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(8);
        EndPoint = (IPEndPoint)_listener.LocalEndPoint!;
        _shutdownCts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_shutdownCts.Token));
    }

    public IPEndPoint EndPoint { get; }

    public int AcceptedConnections { get; private set; }

    /// <summary>
    /// Sets the per-request responder. Returns empty to drop (server stays silent after the
    /// request, simulating a hung peer).
    /// </summary>
    public void OnRequest(Func<byte[], byte[]> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    /// <summary>
    /// When set, the server keeps the connection open after responding so the next exchange
    /// reuses it.
    /// </summary>
    public bool HoldConnection
    {
        get => _holdConnection;
        set => _holdConnection = value;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket conn;
            try
            {
                conn = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
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

            AcceptedConnections++;
            _ = Task.Run(() => HandleConnectionAsync(conn, cancellationToken));
        }
    }

    private async Task HandleConnectionAsync(Socket conn, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] lengthBuf = new byte[2];
                if (!await ReadExactAsync(conn, lengthBuf, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                ushort len = BinaryPrimitives.ReadUInt16BigEndian(lengthBuf);
                byte[] msg = new byte[len];
                if (!await ReadExactAsync(conn, msg, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                byte[] response = _responder(msg);
                if (response.Length == 0)
                {
                    if (_holdConnection)
                    {
                        // Hang: keep the connection open without responding so the client's
                        // receive blocks indefinitely (used for cancellation tests).
                        try
                        {
                            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        return;
                    }
                    // Drop: close the socket without responding.
                    return;
                }

                byte[] framed = new byte[response.Length + 2];
                BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(0, 2), (ushort)response.Length);
                response.CopyTo(framed.AsSpan(2));

                await conn.SendAsync(framed, SocketFlags.None, cancellationToken).ConfigureAwait(false);

                if (!_holdConnection)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                conn.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }
            conn.Dispose();
        }
    }

    private static async Task<bool> ReadExactAsync(Socket conn, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n;
            try
            {
                n = await conn.ReceiveAsync(buffer[read..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
            if (n == 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        try
        {
            _listener.Close();
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
        _listener.Dispose();
    }
}
