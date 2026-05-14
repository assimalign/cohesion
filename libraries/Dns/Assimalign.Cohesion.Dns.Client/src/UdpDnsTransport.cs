using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// DNS-over-UDP transport. One datagram out, one datagram in. Implements RFC 1035 &#167; 4.2.1
/// against a single fixed upstream <see cref="DnsTransport.Endpoint"/>.
/// </summary>
/// <remarks>
/// <para>
/// The transport opens one connected UDP socket per <see cref="UdpDnsTransport"/> instance and
/// reuses it across exchanges &#8211; this keeps the local source port stable and avoids the
/// kernel cost of bind/connect per query, which matters when a resolver issues thousands of
/// queries per second. Each exchange uses its own receive buffer so concurrent
/// <see cref="ExchangeAsync"/> calls on the same transport instance don't trample each other's
/// datagrams; correlation by DNS message ID happens at the resolver layer, not here.
/// </para>
/// <para>
/// Truncated responses (TC bit set in the response header) are surfaced to the caller as-is.
/// The resolver layer is responsible for noticing TC and retrying over TCP per RFC 5966.
/// </para>
/// <para>
/// <strong>Implementation note.</strong> The transport uses <see cref="Socket"/> directly rather
/// than building on <c>Assimalign.Cohesion.Transports.UdpClientTransport</c>. The Transports
/// library models bidirectional pipe-backed flows with pluggable middleware &#8211; great for
/// long-lived server / streaming connections, gratuitous for one-shot DNS request/response. If
/// the Transports library grows a request/response shape we can revisit; the public surface
/// here doesn't change either way.
/// </para>
/// </remarks>
public sealed class UdpDnsTransport : DnsTransport
{
    private readonly UdpDnsTransportOptions _options;
    private readonly Socket _socket;
    private readonly SemaphoreSlim _exchangeLock;

    /// <summary>
    /// Creates a new UDP DNS transport bound to <paramref name="options"/>.<c>EndPoint</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/>.<c>EndPoint</c> is <see langword="null"/>.</exception>
    public UdpDnsTransport(UdpDnsTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EndPoint is null)
        {
            throw new ArgumentException(
                $"{nameof(UdpDnsTransportOptions)}.{nameof(UdpDnsTransportOptions.EndPoint)} is required.",
                nameof(options));
        }
        if (options.QueryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(UdpDnsTransportOptions)}.{nameof(UdpDnsTransportOptions.QueryTimeout)} must be positive.",
                nameof(options));
        }
        if (options.MaxResponseSize <= 0 || options.MaxResponseSize > 65_535)
        {
            throw new ArgumentException(
                $"{nameof(UdpDnsTransportOptions)}.{nameof(UdpDnsTransportOptions.MaxResponseSize)} must be in [1, 65535].",
                nameof(options));
        }

        _options = options;

        AddressFamily family = options.EndPoint switch
        {
            IPEndPoint ip => ip.AddressFamily,
            DnsEndPoint dns => dns.AddressFamily,
            _ => AddressFamily.InterNetwork,
        };

        _socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        // Serialize sends so two concurrent exchanges don't have their datagrams interleaved on
        // the socket's send queue. We hold the lock only for the brief window of the actual
        // send + receive; in practice resolvers run one exchange per transport anyway.
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

        // RFC 1035 §4.2.1: UDP messages are limited to 512 octets without EDNS. We accept the
        // EDNS-advertised payload size here; the resolver enforces the lower bound when it
        // composes the message.
        if (request.Length > _options.MaxResponseSize)
        {
            // Not strictly a transport failure — the message is just too big for UDP. Surface
            // it as Transport so the caller falls back to TCP.
            DnsException.ThrowTransport(
                $"UDP request size {request.Length} exceeds the configured MaxResponseSize {_options.MaxResponseSize}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.QueryTimeout);

        await _exchangeLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            try
            {
                await _socket.SendToAsync(
                    request,
                    SocketFlags.None,
                    _options.EndPoint!,
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                DnsException.ThrowTimeout($"UDP send to {Endpoint}");
            }
            catch (SocketException ex)
            {
                DnsException.ThrowTransport($"UDP send to {Endpoint}: {ex.SocketErrorCode}", ex);
            }

            // Allocate a per-exchange buffer so concurrent exchanges don't share storage. The
            // returned ReadOnlyMemory<byte> aliases this buffer; we never recycle it.
            byte[] buffer = new byte[_options.MaxResponseSize];

            try
            {
                SocketReceiveFromResult result = await _socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, _options.EndPoint!, timeoutCts.Token)
                    .ConfigureAwait(false);

                return buffer.AsMemory(0, result.ReceivedBytes);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                DnsException.ThrowTimeout($"UDP receive from {Endpoint}");
                throw; // unreachable; satisfies the analyzer
            }
            catch (SocketException ex)
            {
                DnsException.ThrowTransport($"UDP receive from {Endpoint}: {ex.SocketErrorCode}", ex);
                throw; // unreachable
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
        _exchangeLock.Dispose();
        _socket.Dispose();
    }
}
