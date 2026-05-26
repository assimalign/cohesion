using System;
using System.Runtime.Versioning;

using Assimalign.Cohesion.Http.Transports.Internal.Http1;
using Assimalign.Cohesion.Http.Transports.Internal.Http2;
using Assimalign.Cohesion.Http.Transports.Internal.Http3;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal sealed class HttpConnectionFactory
{
    private readonly Func<IHttpFeatureCollection>? _createFeatures;

    public HttpConnectionFactory(Func<IHttpFeatureCollection>? createFeatures = null)
    {
        _createFeatures = createFeatures;
    }

    public HttpConnection Create(HttpProtocolRegistration registration, ITransportConnection transportConnection)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(transportConnection);

        return registration.Protocol switch
        {
            HttpProtocol.Http11 when transportConnection is ISingleStreamTransportConnection singleStreamConnection =>
                new Http1Connection(singleStreamConnection, registration.IsSecure, _createFeatures),

            HttpProtocol.Http20 when transportConnection is ISingleStreamTransportConnection singleStreamConnection =>
                new Http2Connection(singleStreamConnection, registration.IsSecure, _createFeatures),

            HttpProtocol.Http30 when transportConnection is IMultiplexTransportConnection multiplexTransportConnection =>
                CreateHttp3Connection(multiplexTransportConnection, registration.IsSecure, _createFeatures),

            HttpProtocol.Http11 or HttpProtocol.Http20 =>
                throw new InvalidOperationException("HTTP/1.1 and HTTP/2 require a single-stream transport connection."),

            HttpProtocol.Http30 =>
                throw new InvalidOperationException("HTTP/3 requires a multiplexed transport connection."),

            _ =>
                throw new InvalidOperationException($"The configured HTTP protocol '{registration.Protocol}' is not supported.")
        };
    }

    private static Http3Connection CreateHttp3Connection(
        IMultiplexTransportConnection connection,
        bool isSecure,
        Func<IHttpFeatureCollection>? createFeatures)
    {
        if (!IsHttp3SupportedPlatform())
        {
            throw new PlatformNotSupportedException("HTTP/3 transports require a QUIC-capable platform.");
        }

        return new Http3Connection(connection, isSecure, createFeatures);
    }

    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    private static bool IsHttp3SupportedPlatform()
    {
        return OperatingSystem.IsWindows() ||
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS();
    }
}
