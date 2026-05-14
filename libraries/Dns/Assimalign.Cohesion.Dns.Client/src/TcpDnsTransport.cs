using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// DNS-over-TCP transport. Frames DNS messages with the two-octet length prefix mandated by
/// RFC 1035 &#167; 4.2.2 and reuses the connection across exchanges per RFC 7766.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="TcpDnsTransport"/> instance owns one TCP connection at a time. The connection
/// is opened lazily on the first <see cref="ExchangeAsync"/> call and recycled when:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="TcpDnsTransportOptions.IdleTimeout"/> elapses with no
///   in-flight exchange (the next exchange opens a fresh connection).</description></item>
///   <item><description>Any I/O on the connection fails &#8211; the next exchange opens a fresh
///   connection. The current exchange surfaces the failure as <see cref="DnsException"/>.</description></item>
///   <item><description>The transport is disposed.</description></item>
/// </list>
/// <para>
/// Concurrent exchanges on the same transport are serialized; RFC 7766 &#167; 6.2.1.1 pipelining
/// is a future optimization gated on resolver-side ID-correlation work.
/// </para>
/// <para>
/// <strong>Implementation note.</strong> The transport uses <see cref="Socket"/> directly. The
/// Cohesion.Transports library's <c>TcpClientTransport</c> is connection-pipe-pipeline shaped,
/// which is the right model for streaming protocols but adds layers for a request/response
/// transport that needs precise control over the two-octet framing and per-exchange recycle
/// semantics. Re-evaluate once Cohesion.Transports grows a request/response shape.
/// </para>
/// </remarks>
public sealed class TcpDnsTransport : DnsTransport
{
    private readonly TcpDnsTransportOptions _options;
    private readonly SemaphoreSlim _exchangeLock;

    private Socket? _socket;
    private DateTime _lastActivityUtc;

    /// <summary>
    /// Creates a new TCP DNS transport bound to <paramref name="options"/>.<c>EndPoint</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/>.<c>EndPoint</c> is <see langword="null"/>
    /// or a timeout is non-positive.</exception>
    public TcpDnsTransport(TcpDnsTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EndPoint is null)
        {
            throw new ArgumentException(
                $"{nameof(TcpDnsTransportOptions)}.{nameof(TcpDnsTransportOptions.EndPoint)} is required.",
                nameof(options));
        }
        if (options.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(TcpDnsTransportOptions)}.{nameof(TcpDnsTransportOptions.ConnectTimeout)} must be positive.",
                nameof(options));
        }
        if (options.QueryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(TcpDnsTransportOptions)}.{nameof(TcpDnsTransportOptions.QueryTimeout)} must be positive.",
                nameof(options));
        }
        if (options.IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(TcpDnsTransportOptions)}.{nameof(TcpDnsTransportOptions.IdleTimeout)} must be positive.",
                nameof(options));
        }

        _options = options;
        _exchangeLock = new SemaphoreSlim(1, 1);
    }

    /// <inheritdoc />
    public override EndPoint Endpoint => _options.EndPoint!;

    /// <inheritdoc />
    public override async Task<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (request.IsEmpty)
        {
            throw new ArgumentException("DNS request must be non-empty.", nameof(request));
        }
        if (request.Length > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"DNS request size {request.Length} exceeds the TCP framing limit (65535 octets).",
                nameof(request));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.QueryTimeout);

        try
        {
            await _exchangeLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (TimedOut(timeoutCts, cancellationToken))
        {
            DnsException.ThrowTimeout($"TCP exchange queue for {Endpoint}");
            throw; // unreachable
        }

        try
        {
            // Recycle the connection if it went idle past IdleTimeout. The check happens
            // under the exchange lock so we can't race with another exchange opening.
            if (_socket is not null && DateTime.UtcNow - _lastActivityUtc > _options.IdleTimeout)
            {
                CloseConnection();
            }

            // One retry budget: if we're reusing a possibly-stale connection and the I/O
            // fails, reopen and try once more. RFC 7766 §6.2.3 — servers MAY close idle
            // connections; clients SHOULD treat that as recoverable for idempotent queries
            // (which all DNS queries are).
            bool reusedConnection = _socket is not null;
            bool retried = false;

            while (true)
            {
                if (_socket is null)
                {
                    await ConnectAsync(timeoutCts, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await WriteFramedAsync(request, timeoutCts.Token).ConfigureAwait(false);
                    ReadOnlyMemory<byte> response = await ReadFramedAsync(timeoutCts.Token).ConfigureAwait(false);
                    _lastActivityUtc = DateTime.UtcNow;
                    return response;
                }
                catch (OperationCanceledException) when (TimedOut(timeoutCts, cancellationToken))
                {
                    CloseConnection();
                    DnsException.ThrowTimeout($"TCP exchange with {Endpoint}");
                    throw; // unreachable
                }
                catch (DnsException ex) when (ex.Code == DnsErrorCode.Transport && reusedConnection && !retried)
                {
                    // Stale-connection retry: close and try once more on a fresh socket.
                    CloseConnection();
                    reusedConnection = false;
                    retried = true;
                    continue;
                }
                catch (SocketException ex) when (reusedConnection && !retried)
                {
                    CloseConnection();
                    reusedConnection = false;
                    retried = true;
                    _ = ex; // discard; we'll retry
                    continue;
                }
                catch (SocketException ex)
                {
                    CloseConnection();
                    DnsException.ThrowTransport($"TCP I/O with {Endpoint}: {ex.SocketErrorCode}", ex);
                    throw; // unreachable
                }
                catch
                {
                    CloseConnection();
                    throw;
                }
            }
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        CloseConnection();
        _exchangeLock.Dispose();
    }

    private async Task ConnectAsync(CancellationTokenSource exchangeTimeoutCts, CancellationToken externalToken)
    {
        AddressFamily family = _options.EndPoint switch
        {
            IPEndPoint ip => ip.AddressFamily,
            DnsEndPoint dns => dns.AddressFamily,
            _ => AddressFamily.InterNetwork,
        };

        var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(exchangeTimeoutCts.Token);
        connectCts.CancelAfter(_options.ConnectTimeout);

        try
        {
            await socket.ConnectAsync(_options.EndPoint!, connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (externalToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            DnsException.ThrowTimeout($"TCP connect to {Endpoint}");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            DnsException.ThrowTransport($"TCP connect to {Endpoint}: {ex.SocketErrorCode}", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _lastActivityUtc = DateTime.UtcNow;
    }

    private async Task WriteFramedAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        // RFC 1035 §4.2.2: two-octet length prefix, network byte order, followed by the message.
        // Compose the prefix + message in one buffer to keep the send to a single syscall.
        int totalLength = request.Length + 2;
        byte[] framed = new byte[totalLength];
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(0, 2), (ushort)request.Length);
        request.CopyTo(framed.AsMemory(2));

        int sent = 0;
        while (sent < totalLength)
        {
            int n = await _socket!
                .SendAsync(framed.AsMemory(sent, totalLength - sent), SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                DnsException.ThrowTransport($"TCP send to {Endpoint}: socket closed mid-write");
            }
            sent += n;
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadFramedAsync(CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[2];
        await ReadExactAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);

        if (length == 0)
        {
            DnsException.ThrowMalformed($"TCP response from {Endpoint} declared zero-length message");
        }

        byte[] message = new byte[length];
        await ReadExactAsync(message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int received = 0;
        while (received < buffer.Length)
        {
            int n = await _socket!
                .ReceiveAsync(buffer[received..], SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);

            if (n == 0)
            {
                DnsException.ThrowTransport($"TCP receive from {Endpoint}: connection closed before {buffer.Length} octets read");
            }
            received += n;
        }
    }

    private void CloseConnection()
    {
        Socket? s = _socket;
        if (s is null)
        {
            return;
        }
        _socket = null;
        try
        {
            s.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        s.Dispose();
    }

    private static bool TimedOut(CancellationTokenSource timeoutCts, CancellationToken externalToken)
        => timeoutCts.IsCancellationRequested && !externalToken.IsCancellationRequested;
}
