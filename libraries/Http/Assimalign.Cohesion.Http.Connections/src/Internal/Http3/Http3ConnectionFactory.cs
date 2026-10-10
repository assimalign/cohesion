using System;
using System.Runtime.Versioning;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Produces <see cref="Http3Connection"/> instances, capturing the limits and QPACK options
/// configured for this HTTP/3 registration plus the listener-wide request/response
/// interceptors and per-exchange feature capacity, and enforcing the QUIC-capable platform
/// guard at connection-creation time.
/// </summary>
internal sealed class Http3ConnectionFactory : HttpMultiplexedConnectionFactory
{
    private readonly Http3ConnectionListenerOptions.Http3Limits _limits;
    private readonly IHttpExchangeInterceptor[] _requestInterceptors;
    private readonly IHttpExchangeInterceptor[] _responseInterceptors;
    private readonly int _featureCapacity;
    private readonly Http3QPackOptions _qpackOptions;

    public Http3ConnectionFactory(
        Http3ConnectionListenerOptions.Http3Limits limits,
        IHttpExchangeInterceptor[] requestInterceptors,
        IHttpExchangeInterceptor[] responseInterceptors,
        int featureCapacity,
        Http3QPackOptions qpackOptions)
    {
        _limits = limits;
        _requestInterceptors = requestInterceptors;
        _responseInterceptors = responseInterceptors;
        _featureCapacity = featureCapacity;
        _qpackOptions = qpackOptions;
    }

    public override HttpConnection Create(IMultiplexedConnection connection, bool isSecure)
    {
        if (!IsSupportedPlatform())
        {
            throw new PlatformNotSupportedException("HTTP/3 transports require a QUIC-capable platform.");
        }

        return new Http3Connection(connection, isSecure, _limits, _requestInterceptors, _responseInterceptors, _featureCapacity, _qpackOptions);
    }

    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    private static bool IsSupportedPlatform()
    {
        return OperatingSystem.IsWindows() ||
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS();
    }
}
