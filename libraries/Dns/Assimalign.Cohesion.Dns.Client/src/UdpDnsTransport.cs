using System;
using System.Globalization;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Dns.Internal;

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
[global::System.Diagnostics.DebuggerDisplay("UdpDnsTransport")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class UdpDnsTransport : DnsTransport
{
    private readonly EndPoint _endPoint;
    private readonly TimeSpan _queryTimeout;
    private readonly int _maxResponseSize;
    private readonly Socket _socket;
    private readonly SemaphoreSlim _exchangeLock;

    /// <summary>
    /// Creates a new UDP DNS transport bound to <paramref name="options"/>.<c>EndPoint</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/>.<c>EndPoint</c> is <see langword="null"/>.</exception>
    /// <remarks>Configuration values are captured at construction. Later option assignments do not change this instance.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">A numeric limit or timeout is outside its supported range.</exception>
    public UdpDnsTransport(UdpDnsTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _endPoint = DnsEndpointSnapshot.Copy(options.EndPoint!);
        _queryTimeout = options.QueryTimeout;
        _maxResponseSize = options.MaxResponseSize;
        if (_endPoint is null)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.OptionRequired, nameof(UdpDnsTransportOptions), nameof(UdpDnsTransportOptions.EndPoint)),
                nameof(options));
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_queryTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_maxResponseSize, 0, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_maxResponseSize, 65_535, nameof(options));

        AddressFamily family = _endPoint switch
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
    public override EndPoint Endpoint => DnsEndpointSnapshot.Copy(_endPoint);

    /// <inheritdoc />
    public override async Task<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (request.IsEmpty)
        {
            throw new ArgumentException(ErrorMessages.RequestEmpty, nameof(request));
        }

        // RFC 1035 §4.2.1: UDP messages are limited to 512 octets without EDNS. We accept the
        // EDNS-advertised payload size here; the resolver enforces the lower bound when it
        // composes the message.
        if (request.Length > _maxResponseSize)
        {
            // Not strictly a transport failure — the message is just too big for UDP. Surface
            // it as Transport so the caller falls back to TCP.
            DnsException.ThrowTransport(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.UdpRequestTooLarge, request.Length, _maxResponseSize));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_queryTimeout);

        await _exchangeLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            try
            {
                await _socket.SendToAsync(
                    request,
                    SocketFlags.None,
                    _endPoint,
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                DnsException.ThrowTimeout(string.Format(CultureInfo.CurrentCulture, ErrorMessages.UdpSendTimeout, Endpoint));
            }
            catch (SocketException ex)
            {
                DnsException.ThrowTransport(string.Format(CultureInfo.CurrentCulture, ErrorMessages.UdpSendFailure, Endpoint, ex.SocketErrorCode), ex);
            }

            // Allocate a per-exchange buffer so concurrent exchanges don't share storage. The
            // returned ReadOnlyMemory<byte> aliases this buffer; we never recycle it.
            byte[] buffer = new byte[_maxResponseSize];

            try
            {
                SocketReceiveFromResult result = await _socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, _endPoint, timeoutCts.Token)
                    .ConfigureAwait(false);

                return buffer.AsMemory(0, result.ReceivedBytes);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                DnsException.ThrowTimeout(string.Format(CultureInfo.CurrentCulture, ErrorMessages.UdpReceiveTimeout, Endpoint));
                throw; // unreachable; satisfies the analyzer
            }
            catch (SocketException ex)
            {
                DnsException.ThrowTransport(string.Format(CultureInfo.CurrentCulture, ErrorMessages.UdpReceiveFailure, Endpoint, ex.SocketErrorCode), ex);
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

    private sealed class DebugView
    {
        private readonly UdpDnsTransport _target;

        public DebugView(UdpDnsTransport value)
        {
            _target = value;
        }

        public bool IsDisposed => _target.IsDisposed;
    }
}
