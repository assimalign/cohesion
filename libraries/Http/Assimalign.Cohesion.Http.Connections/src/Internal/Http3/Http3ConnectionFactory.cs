using System;
using System.Runtime.Versioning;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// Produces <see cref="Http3Connection"/> instances, capturing the QPACK options
/// configured for this HTTP/3 registration plus the listener-wide response
/// interceptors, and enforcing the QUIC-capable platform guard at
/// connection-creation time.
/// </summary>
internal sealed class Http3ConnectionFactory : HttpMultiplexedConnectionFactory
{
    private readonly IHttpResponseInterceptor[] _responseInterceptors;
    private readonly Http3QPackOptions _qpackOptions;

    public Http3ConnectionFactory(IHttpResponseInterceptor[] responseInterceptors, Http3QPackOptions qpackOptions)
    {
        _responseInterceptors = responseInterceptors;
        _qpackOptions = qpackOptions;
    }

    public override HttpConnection Create(IMultiplexedConnection connection, bool isSecure)
    {
        if (!IsSupportedPlatform())
        {
            throw new PlatformNotSupportedException("HTTP/3 transports require a QUIC-capable platform.");
        }

        return new Http3Connection(connection, isSecure, _responseInterceptors, _qpackOptions);
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
