using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

internal sealed class Http3Connection : HttpConnection
{
    private readonly IMultiplexTransportConnection _connection;
    private Http3ConnectionContext? _openContext;

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public Http3Connection(IMultiplexTransportConnection connection, bool isSecure)
        : base(connection, isSecure)
    {
        _connection = connection;
    }

    public override HttpConnectionContext Open()
    {
        return OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public override async ValueTask<HttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_openContext is not null)
        {
            return _openContext;
        }

        if (!IsSupportedPlatform())
        {
            throw new PlatformNotSupportedException("HTTP/3 transports require a QUIC-capable platform.");
        }

        _openContext = new Http3ConnectionContext(_connection, IsSecure);

        return _openContext;
    }

    public override ValueTask DisposeAsync()
    {
        return Connection.DisposeAsync();
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
