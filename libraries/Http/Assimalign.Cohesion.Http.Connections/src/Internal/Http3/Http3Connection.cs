using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

internal sealed class Http3Connection : HttpConnection
{
    private readonly IMultiplexedConnection _connection;
    private readonly Http3ConnectionListenerOptions.Http3Limits _limits;
    private readonly IHttpExchangeInterceptor[] _requestInterceptors;
    private readonly IHttpExchangeInterceptor[] _responseInterceptors;
    private readonly Http3QPackOptions _qpackOptions;
    private Http3ConnectionContext? _openContext;

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public Http3Connection(
        IMultiplexedConnection connection,
        bool isSecure,
        Http3ConnectionListenerOptions.Http3Limits limits,
        IHttpExchangeInterceptor[] requestInterceptors,
        IHttpExchangeInterceptor[] responseInterceptors,
        Http3QPackOptions qpackOptions)
        : base(isSecure)
    {
        _connection = connection;
        _limits = limits;
        _requestInterceptors = requestInterceptors;
        _responseInterceptors = responseInterceptors;
        _qpackOptions = qpackOptions;
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

        return _openContext = new Http3ConnectionContext(_connection, IsSecure, _limits, _requestInterceptors, _responseInterceptors, _qpackOptions);
    }

    public override ValueTask<HttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask<HttpConnectionContext>(Open());
    }

    public override async ValueTask DisposeAsync()
    {
        // RFC 9114 §5.2 — announce graceful shutdown by writing GOAWAY on the
        // server's control stream before the QUIC CONNECTION_CLOSE, so streams
        // at or below the announced ID may finish their responses. GOAWAY
        // emission lives here in the HTTP/3 layer; the connection-first close
        // ordering (bidirectional streams drained, then CONNECTION_CLOSE, then
        // the critical unidirectional streams released) stays in the QUIC
        // driver's DisposeAsync. Emission is best-effort and precedes it.
        if (_openContext is not null)
        {
            await _openContext.SendGoAwayAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
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
