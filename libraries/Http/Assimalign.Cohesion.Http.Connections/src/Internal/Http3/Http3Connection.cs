using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

internal sealed class Http3Connection : HttpConnection
{
    private readonly IMultiplexedConnection _connection;
    private readonly HttpServerLimits _limits;
    private readonly IHttpRequestInterceptor[] _interceptors;
    private Http3ConnectionContext? _openContext;

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public Http3Connection(IMultiplexedConnection connection, bool isSecure, HttpServerLimits limits, IHttpRequestInterceptor[] interceptors)
        : base(isSecure)
    {
        _connection = connection;
        _limits = limits;
        _interceptors = interceptors;
    }

    public override ConnectionId Id => _connection.Id;

    public override ConnectionState State => _connection.State;

    public override CancellationToken ConnectionClosed => _connection.ConnectionClosed;

    public override void Abort(Exception? reason = null)
    {
        _connection.Abort(reason);
    }

    public override HttpConnectionContext Open()
    {
        if (_openContext is not null)
        {
            return _openContext;
        }

        if (!IsSupportedPlatform())
        {
            throw new PlatformNotSupportedException("HTTP/3 transports require a QUIC-capable platform.");
        }

        return _openContext = new Http3ConnectionContext(_connection, IsSecure, _limits, _interceptors);
    }

    public override ValueTask<HttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask<HttpConnectionContext>(Open());
    }

    public override ValueTask DisposeAsync()
    {
        return _connection.DisposeAsync();
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
