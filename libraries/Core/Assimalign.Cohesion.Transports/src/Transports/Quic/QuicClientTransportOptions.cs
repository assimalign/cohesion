#if NET7_0_OR_GREATER
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
/// Defines options for creating a QUIC client transport.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public sealed class QuicClientTransportOptions
{
    private readonly TransportPipelineBuilder<QuicTransportConnection, QuicTransportContext> _builder;
    private long _defaultStreamErrorCode;
    private long _defaultCloseErrorCode;

    /// <summary>
    /// Creates a new set of QUIC client transport options.
    /// </summary>
    public QuicClientTransportOptions()
    {
        _builder = new TransportPipelineBuilder<QuicTransportConnection, QuicTransportContext>();
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
    /// Gets or sets the remote endpoint to connect to.
    /// </summary>
    public IPEndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8080);

    /// <summary>
    /// Gets or sets the TLS authentication settings used by the client.
    /// </summary>
    public SslClientAuthenticationOptions ClientAuthenticationOptions { get; set; }

    /// <summary>
    /// Gets or sets the default stream type used when opening outbound streams.
    /// </summary>
    public QuicStreamType OutboundStreamType { get; set; } = QuicStreamType.Bidirectional;

    /// <summary>
    /// Gets or sets the error code used when closing the connection.
    /// </summary>
    public long DefaultCloseErrorCode
    {
        get => _defaultCloseErrorCode;
        set
        {
            QuicServerTransportOptions.ValidateErrorCode(value);
            _defaultCloseErrorCode = value;
        }
    }

    /// <summary>
    /// Gets or sets the error code used when a stream abort is triggered.
    /// </summary>
    public long DefaultStreamErrorCode
    {
        get => _defaultStreamErrorCode;
        set
        {
            QuicServerTransportOptions.ValidateErrorCode(value);
            _defaultStreamErrorCode = value;
        }
    }

    /// <summary>
    /// Adds middleware to the QUIC client transport pipeline.
    /// </summary>
    /// <param name="middleware">The middleware delegate to add.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="middleware"/> is <see langword="null"/>.</exception>
    public QuicClientTransportOptions Use(Func<QuicTransportConnection, QuicTransportContext, TransportMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _builder.Use(middleware);

        return this;
    }

    internal TransportPipeline BuildPipeline()
    {
        return (TransportPipeline)((ITransportPipelineBuilder)_builder).Build();
    }
}
#endif
