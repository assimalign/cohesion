using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;

using Assimalign.Cohesion.Connections.Internal;

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
    // for stream aborts and H3_NO_ERROR (0x100) for connection close. A listener serving a
    // different ALPN protocol overrides both alongside ApplicationProtocols.
    private long _defaultStreamErrorCode = 0x10c;
    private long _defaultCloseErrorCode = 0x100;

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
    /// Gets or sets the maximum number of concurrent inbound bidirectional streams per connection.
    /// </summary>
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
    /// Gets or sets the error code used when a stream abort is triggered.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>0x10c</c> — HTTP/3 <c>H3_REQUEST_CANCELLED</c> (RFC 9114 §8.1) — matching
    /// the default HTTP/3 application protocol. Override this when the listener serves a
    /// different ALPN protocol.
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
    /// Gets or sets the error code used when a connection closes.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>0x100</c> — HTTP/3 <c>H3_NO_ERROR</c> (RFC 9114 §8.1) — matching the
    /// default HTTP/3 application protocol. Override this when the listener serves a different
    /// ALPN protocol.
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

    internal StreamPipeOptionsContext CreateStreamOptions()
    {
        return PipeOptionsFactory.CreateStreamOptions(MaxReadBufferSize, MaxWriteBufferSize);
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
