using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Quic;

/// <summary>
/// Defines options for creating outbound QUIC connections.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicConnectionFactoryOptions
{
    // The options default to HTTP/3 (the sole default ApplicationProtocols entry), so the
    // default error codes are the matching RFC 9114 §8.1 codes: H3_REQUEST_CANCELLED (0x10c)
    // for stream aborts and H3_NO_ERROR (0x100) for connection close, wherever the caller gives
    // no code of its own. A client dialing a different ALPN protocol overrides both alongside
    // ApplicationProtocols.
    private long _defaultStreamErrorCode = 0x10c;
    private long _defaultCloseErrorCode = 0x100;
    private TimeSpan _closeDeliveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates a new set of QUIC client transport options.
    /// </summary>
    public QuicConnectionFactoryOptions()
    {
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http3
            },
            EnabledSslProtocols = SslProtocols.Tls13,
            TargetHost = "localhost"
        };
    }

    /// <summary>
    /// Gets a new options instance populated with default values.
    /// </summary>
    public static QuicConnectionFactoryOptions Default => new();

    /// <summary>
    /// Gets or sets the default remote endpoint.
    /// </summary>
    /// <remarks>
    /// This is a default only; the endpoint passed to
    /// <see cref="QuicConnectionFactory.ConnectAsync(System.Net.EndPoint, System.Threading.CancellationToken)"/>
    /// always wins. Defaults to <c>127.0.0.1:8080</c>.
    /// </remarks>
    public IPEndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8080);

    /// <summary>
    /// Gets or sets the TLS authentication settings used by the client.
    /// </summary>
    public SslClientAuthenticationOptions ClientAuthenticationOptions { get; set; }

    /// <summary>
    /// Gets or sets the maximum read buffer size used by each stream pipe.
    /// </summary>
    public long? MaxReadBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum write buffer size used by each stream pipe.
    /// </summary>
    public long? MaxWriteBufferSize { get; set; } = 64 * 1024;

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
    /// default HTTP/3 application protocol. Override this when dialing a different ALPN
    /// protocol.
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
            QuicConnectionListenerOptions.ValidateErrorCode(value);
            _defaultCloseErrorCode = value;
        }
    }

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
    /// the default HTTP/3 application protocol. Override this when dialing a different ALPN
    /// protocol.
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
            QuicConnectionListenerOptions.ValidateErrorCode(value);
            _defaultStreamErrorCode = value;
        }
    }

    /// <summary>
    /// Gets or sets the longest a connection's disposal waits for its bidirectional streams to deliver what
    /// they sent before it closes the connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disposing a connection ends every bidirectional stream gracefully, then waits for the peer to
    /// acknowledge each stream's data and FIN, so a request's tail is not lost to the
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
            QuicConnectionListenerOptions.ValidateCloseDeliveryTimeout(value);
            _closeDeliveryTimeout = value;
        }
    }

    internal StreamPipeOptionsContext CreateStreamOptions()
    {
        return PipeOptionsFactory.CreateStreamOptions(MaxReadBufferSize, MaxWriteBufferSize);
    }
}
