using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Quic;

/// <summary>
/// Defines options for creating a QUIC connection listener.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicConnectionListenerOptions
{
    // The options default to HTTP/3 (the sole default ApplicationProtocols entry), so the
    // default error codes are the matching RFC 9114 §8.1 codes: H3_REQUEST_CANCELLED (0x10c)
    // for stream aborts and H3_NO_ERROR (0x100) for connection close, wherever the caller gives
    // no code of its own. A listener serving a different ALPN protocol overrides both alongside
    // ApplicationProtocols.
    private long _defaultStreamErrorCode = 0x10c;
    private long _defaultCloseErrorCode = 0x100;
    private TimeSpan _closeDeliveryTimeout = TimeSpan.FromSeconds(5);
    private TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Creates a new set of QUIC server transport options.
    /// </summary>
    public QuicConnectionListenerOptions()
    {
        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http3
            },
            EnabledSslProtocols = SslProtocols.Tls13
        };
    }

    /// <summary>
    /// Gets a new options instance populated with default values.
    /// </summary>
    public static QuicConnectionListenerOptions Default => new();

    /// <summary>
    /// Gets or sets the endpoint used by the QUIC listener.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>127.0.0.1:8080</c>.
    /// </remarks>
    public IPEndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8080);

    /// <summary>
    /// Gets or sets the listener backlog.
    /// </summary>
    public int Backlog { get; set; } = 512;

    /// <summary>
    /// Gets or sets the time allowed for an inbound QUIC connection's handshake to complete.
    /// </summary>
    /// <remarks>
    /// Defaults to ten seconds, matching <see cref="QuicServerConnectionOptions.HandshakeTimeout"/>.
    /// A positive value bounds each handshake, and <see cref="Timeout.InfiniteTimeSpan"/> disables
    /// that timeout. Zero is passed through to <c>System.Net.Quic</c>; its .NET 10 server path starts
    /// an immediate cancellation timer for zero, so use <see cref="Timeout.InfiniteTimeSpan"/> to
    /// disable the timeout. A failed handshake ends only that connection, not the listener's accept.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is negative other than <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public TimeSpan HandshakeTimeout
    {
        get => _handshakeTimeout;
        set
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A non-negative timeout or Timeout.InfiniteTimeSpan is required.");
            }

            _handshakeTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of concurrent inbound bidirectional streams per connection.
    /// </summary>
    /// <remarks>
    /// Captured when the listener binds. Later changes do not alter the bound listener's handshake
    /// allowance or the stream-limit facet on the connections it accepts.
    /// </remarks>
    public int MaxBidirectionalStreamCount { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of concurrent inbound unidirectional streams per connection.
    /// </summary>
    public int MaxUnidirectionalStreamCount { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum read buffer size used by each stream pipe.
    /// </summary>
    public long? MaxReadBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum write buffer size used by each stream pipe.
    /// </summary>
    public long? MaxWriteBufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets the TLS authentication settings used by the server.
    /// </summary>
    public SslServerAuthenticationOptions ServerAuthenticationOptions { get; set; }

    /// <summary>
    /// Gets or sets the error code a stream direction is ended with when the caller supplies none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The code goes on the wire only where the driver ends a direction on its own:
    /// <see cref="IConnection.Abort(Exception)"/> and <see cref="IAsyncDisposable.DisposeAsync"/> on a
    /// stream; completing a stream's <see cref="System.IO.Pipelines.IDuplexPipe.Input"/>, which stops a
    /// receiving direction still open; and completing its <see cref="System.IO.Pipelines.IDuplexPipe.Output"/>
    /// with an error, which resets the sending direction (a clean completion sends the FIN). A direction
    /// aborted through <see cref="IMultiplexedStreamAbort"/> carries the caller's code instead, and keeps
    /// it when the stream is later aborted or disposed.
    /// </para>
    /// <para>
    /// Changed from 10.0.0-preview.1 (#1330; see the Breaking changes in the driver's DESIGN): completing
    /// <see cref="System.IO.Pipelines.IDuplexPipe.Output"/> used to dispose the whole stream, which stopped a
    /// receiving direction still open with this code, and completing it with an error sent the FIN. It now
    /// ends only the sending direction, and completing it with an error sends <c>RESET_STREAM</c> with this
    /// code.
    /// </para>
    /// <para>
    /// Defaults to <c>0x10c</c> — HTTP/3 <c>H3_REQUEST_CANCELLED</c> (RFC 9114 §8.1) — matching
    /// the default HTTP/3 application protocol. Override this when the listener serves a
    /// different ALPN protocol.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is outside the valid QUIC error-code range of 0 to 2^62 - 1.
    /// </exception>
    public long DefaultStreamErrorCode
    {
        get => _defaultStreamErrorCode;
        set
        {
            ValidateErrorCode(value);
            _defaultStreamErrorCode = value;
        }
    }

    /// <summary>
    /// Gets or sets the error code a connection closes with when the caller supplies none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The code goes on the wire when the connection closes through
    /// <see cref="IMultiplexedConnection.Abort(Exception)"/> or
    /// <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// <see cref="IMultiplexedConnectionAbort.Abort(long, Exception)"/> closes with the caller's code
    /// instead; the first abort or disposal decides the close.
    /// </para>
    /// <para>
    /// Defaults to <c>0x100</c> — HTTP/3 <c>H3_NO_ERROR</c> (RFC 9114 §8.1) — matching the
    /// default HTTP/3 application protocol. Override this when the listener serves a different
    /// ALPN protocol.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is outside the valid QUIC error-code range of 0 to 2^62 - 1.
    /// </exception>
    public long DefaultCloseErrorCode
    {
        get => _defaultCloseErrorCode;
        set
        {
            ValidateErrorCode(value);
            _defaultCloseErrorCode = value;
        }
    }

    /// <summary>
    /// Gets or sets the longest a connection's disposal waits for its bidirectional streams to deliver what
    /// they sent before it closes the connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disposing a connection ends every bidirectional stream gracefully, then waits for the peer to
    /// acknowledge each stream's data and FIN, so a response's tail is not lost to the
    /// <c>CONNECTION_CLOSE</c> that follows. The waits run concurrently, and this bounds them all: a peer
    /// that withholds stream credit, or stops acknowledging, could otherwise hold the disposal, and the
    /// connection, open for as long as it keeps the connection alive. When the time runs out the connection
    /// closes with <see cref="DefaultCloseErrorCode"/>, which ends every stream still delivering, so its tail
    /// is lost.
    /// </para>
    /// <para>
    /// Defaults to five seconds. <see cref="TimeSpan.Zero"/> closes without waiting for any stream, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits without a bound.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>) or
    /// longer than <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    public TimeSpan CloseDeliveryTimeout
    {
        get => _closeDeliveryTimeout;
        set
        {
            ValidateCloseDeliveryTimeout(value);
            _closeDeliveryTimeout = value;
        }
    }

    internal QuicServerConnectionOptions CreateConnectionOptions(int maxInboundBidirectionalStreams)
    {
        return new QuicServerConnectionOptions
        {
            ServerAuthenticationOptions = ServerAuthenticationOptions,
            MaxInboundBidirectionalStreams = maxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = MaxUnidirectionalStreamCount,
            DefaultCloseErrorCode = DefaultCloseErrorCode,
            DefaultStreamErrorCode = DefaultStreamErrorCode,
            HandshakeTimeout = HandshakeTimeout
        };
    }

    internal StreamPipeOptionsContext CreateStreamOptions()
    {
        return PipeOptionsFactory.CreateStreamOptions(MaxReadBufferSize, MaxWriteBufferSize);
    }

    internal static void ValidateCloseDeliveryTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan
            && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A non-negative timeout of at most int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan, is required.");
        }
    }

    internal static void ValidateErrorCode(long errorCode)
    {
        const long minErrorCode = 0;
        const long maxErrorCode = (1L << 62) - 1;

        if (errorCode < minErrorCode || errorCode > maxErrorCode)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, $"A value between 0x{minErrorCode:x} and 0x{maxErrorCode:x} is required.");
        }
    }
}
