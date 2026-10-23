using System;
using System.Collections.Generic;
using System.Globalization;
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
[global::System.Diagnostics.DebuggerDisplay("QuicConnectionListenerOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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
    private int _backlog = 512;
    private int _maxBidirectionalStreamCount = 100;
    private int _maxUnidirectionalStreamCount = 10;

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
    /// <remarks>
    /// Defaults to 512. Zero selects the <see cref="QuicListenerOptions.ListenBacklog"/> default.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int Backlog
    {
        get => _backlog;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _backlog = value;
        }
    }

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
                throw new ArgumentOutOfRangeException(nameof(value), value, ErrorMessages.TimeoutInvalid);
            }

            _handshakeTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of concurrent inbound bidirectional streams per connection.
    /// </summary>
    /// <remarks>
    /// Accepts 0 through 65535 inclusive, matching the .NET 10
    /// <see cref="QuicConnectionOptions.MaxInboundBidirectionalStreams"/> implementation limit.
    /// Captured when the listener binds. Later changes do not alter the bound listener's handshake
    /// allowance or the stream-limit facet on the connections it accepts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside 0 through 65535.</exception>
    public int MaxBidirectionalStreamCount
    {
        get => _maxBidirectionalStreamCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, (int)ushort.MaxValue);
            _maxBidirectionalStreamCount = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of concurrent inbound unidirectional streams per connection.
    /// </summary>
    /// <remarks>
    /// Defaults to ten. Zero forbids inbound unidirectional streams when the bound bidirectional
    /// allowance is positive. Only when both allowances are zero does the transport grant one
    /// unidirectional stream so a pending native accept can observe idle peer closure.
    /// Accepts 0 through 65535 inclusive, matching the .NET 10
    /// <see cref="QuicConnectionOptions.MaxInboundUnidirectionalStreams"/> implementation limit.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside 0 through 65535.</exception>
    public int MaxUnidirectionalStreamCount
    {
        get => _maxUnidirectionalStreamCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, (int)ushort.MaxValue);
            _maxUnidirectionalStreamCount = value;
        }
    }

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
    /// <see cref="IMultiplexedConnection.Abort(long, Exception)"/> closes with the caller's code
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
            MaxInboundUnidirectionalStreams = maxInboundBidirectionalStreams == 0 && MaxUnidirectionalStreamCount == 0 ? 1 : MaxUnidirectionalStreamCount,
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
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, ErrorMessages.CloseDeliveryTimeoutInvalid);
        }
    }

    internal static void ValidateErrorCode(long errorCode)
    {
        const long minErrorCode = 0;
        const long maxErrorCode = (1L << 62) - 1;

        if (errorCode < minErrorCode || errorCode > maxErrorCode)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, string.Format(CultureInfo.CurrentCulture, ErrorMessages.ErrorCodeOutOfRange, minErrorCode, maxErrorCode));
        }
    }

    private sealed class DebugView
    {
        private readonly QuicConnectionListenerOptions _target;

        public DebugView(QuicConnectionListenerOptions value)
        {
            _target = value;
        }

        public IPEndPoint EndPoint => _target.EndPoint;
        public int Backlog => _target.Backlog;
        public TimeSpan HandshakeTimeout => _target.HandshakeTimeout;
        public int MaxBidirectionalStreamCount => _target.MaxBidirectionalStreamCount;
        public int MaxUnidirectionalStreamCount => _target.MaxUnidirectionalStreamCount;
        public long? MaxReadBufferSize => _target.MaxReadBufferSize;
        public long? MaxWriteBufferSize => _target.MaxWriteBufferSize;
        public SslServerAuthenticationOptions ServerAuthenticationOptions => _target.ServerAuthenticationOptions;
        public long DefaultStreamErrorCode => _target.DefaultStreamErrorCode;
        public long DefaultCloseErrorCode => _target.DefaultCloseErrorCode;
        public TimeSpan CloseDeliveryTimeout => _target.CloseDeliveryTimeout;
    }
}
