using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

/// <summary>
/// Defines options for creating a QUIC server transport.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public sealed class QuicServerTransportOptions
{
    private readonly TransportPipelineBuilder<QuicTransportConnection, QuicTransportContext> _builder;
    private long _defaultStreamErrorCode;
    private long _defaultCloseErrorCode;

    /// <summary>
    /// Creates a new set of QUIC server transport options.
    /// </summary>
    public QuicServerTransportOptions()
    {
        _builder = new TransportPipelineBuilder<QuicTransportConnection, QuicTransportContext>();
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
    /// Gets or sets the endpoint used by the QUIC listener.
    /// </summary>
    public IPEndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8080);

    /// <summary>
    /// Gets or sets the listener backlog.
    /// </summary>
    public int Backlog { get; set; } = 512;

    /// <summary>
    /// Gets or sets the maximum number of concurrent inbound bidirectional streams.
    /// </summary>
    public int MaxBidirectionalStreamCount { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of concurrent inbound unidirectional streams.
    /// </summary>
    public int MaxUnidirectionalStreamCount { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum read buffer size.
    /// </summary>
    public long? MaxReadBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum write buffer size.
    /// </summary>
    public long? MaxWriteBufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets the TLS authentication settings used by the server.
    /// </summary>
    public SslServerAuthenticationOptions ServerAuthenticationOptions { get; set; }

    /// <summary>
    /// Gets or sets the stream type used when opening outbound streams from accepted connections.
    /// </summary>
    public QuicStreamType OutboundStreamType { get; set; } = QuicStreamType.Bidirectional;

    /// <summary>
    /// Gets or sets the error code used when a stream abort is triggered.
    /// </summary>
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
    /// Adds middleware to the QUIC server transport pipeline.
    /// </summary>
    /// <param name="middleware">The middleware delegate to add.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="middleware"/> is <see langword="null"/>.</exception>
    public QuicServerTransportOptions Use(Func<QuicTransportConnection, QuicTransportContext, TransportMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _builder.Use(middleware);

        return this;
    }

    internal TransportPipeline BuildPipeline()
    {
        return (TransportPipeline)((ITransportPipelineBuilder)_builder).Build();
    }

    internal TransportStreamPipeOptionsContext CreateStreamOptions()
    {
        return TransportPipeOptionsFactory.CreateStreamOptions(MaxReadBufferSize, MaxWriteBufferSize);
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
