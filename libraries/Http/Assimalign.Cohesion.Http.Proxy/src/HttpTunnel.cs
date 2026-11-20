using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Serves an HTTP/1.1 <c>CONNECT</c> request (RFC 9110 §9.3.6) by connecting to the destination and
/// relaying bytes between it and the client, the tunnel of a forward proxy.
/// </summary>
/// <remarks>
/// <para>
/// The tunnel connects before it answers, so a destination that cannot be reached is reported to the
/// client as 502 Bad Gateway, or 504 Gateway Timeout after <see cref="HttpTunnelOptions.ConnectTimeout"/>,
/// instead of as a tunnel that closes at once. Once connected, it accepts the request through the
/// exchange's <see cref="IHttpProtocolUpgrade"/> (the server writes the 200) and relays until either
/// side closes; the client finishing its sending is passed on as a half-close.
/// </para>
/// <para>
/// A <c>CONNECT</c> over HTTP/2 or HTTP/3 is not served: the server exposes no protocol upgrade for
/// it, and the tunnel answers 501 Not Implemented.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("HttpTunnel")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HttpTunnel : IHttpTunnel
{
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _idleTimeout;
    private readonly Func<DnsEndPoint, CancellationToken, ValueTask<Stream>> _connect;

    private HttpTunnel(HttpTunnelOptions options)
    {
        _connectTimeout = options.ConnectTimeout;
        _idleTimeout = options.IdleTimeout;
        _connect = options.ConnectCallback ?? ConnectAsync;
    }

    /// <summary>
    /// Creates a tunnel.
    /// </summary>
    /// <param name="options">
    /// The tunnel's options, or <see langword="null"/> for the defaults. The values are copied.
    /// </param>
    /// <returns>The tunnel.</returns>
    public static HttpTunnel Create(HttpTunnelOptions? options = null)
    {
        return new HttpTunnel(options ?? new HttpTunnelOptions());
    }

    /// <inheritdoc />
    public async Task<HttpProxyResult> RelayAsync(IHttpContext context, DnsEndPoint destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(destination);

        IHttpProtocolUpgrade? upgrade = context.Upgrade;
        if (upgrade is not { Kind: HttpProtocolUpgradeKind.Connect })
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
            return HttpProxyResult.NotSupported;
        }

        string authority = destination.Host + ":" + destination.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Stream destinationStream;

        using (ActivityTimeout connecting = new(_connectTimeout, cancellationToken, context.RequestCancelled))
        {
            try
            {
                destinationStream = await _connect(destination, connecting.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
            {
                if (connecting.IsCanceledByCaller)
                {
                    context.Cancel();
                    return HttpProxyResult.Canceled;
                }

                HttpProxyResult result = exception is OperationCanceledException
                    ? HttpProxyResult.DestinationTimedOut
                    : HttpProxyResult.DestinationUnreachable;
                HttpProxyEventSource.Log.DestinationFailed(result, authority, exception);
                context.Response.StatusCode = result == HttpProxyResult.DestinationTimedOut
                    ? HttpStatusCode.GatewayTimeout
                    : HttpStatusCode.BadGateway;
                return result;
            }
        }

        using ActivityTimeout activity = new(_idleTimeout, cancellationToken, context.RequestCancelled);
        Stream clientStream;

        try
        {
            clientStream = await upgrade.AcceptAsync(activity.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            await destinationStream.DisposeAsync().ConfigureAwait(false);
            context.Cancel();
            return HttpProxyResult.Canceled;
        }

        HttpProxyEventSource.Log.TunnelOpened(destination);
        StreamRelayResult relay = default;

        try
        {
            relay = await StreamRelay.RelayAsync(clientStream, destinationStream, activity).ConfigureAwait(false);
        }
        finally
        {
            HttpProxyEventSource.Log.TunnelClosed(destination, relay.BytesToDestination, relay.BytesToClient);
        }

        return activity.IsCanceledByCaller && relay.Failure is OperationCanceledException
            ? HttpProxyResult.Canceled
            : HttpProxyResult.Completed;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint destination, CancellationToken cancellationToken)
    {
        // A dual-mode socket reaches IPv4 and IPv6 addresses alike, and connecting by name tries each
        // address the name resolves to.
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(destination, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            // Cleanup only: the socket is disposed and the failure propagates unchanged.
            socket.Dispose();
            throw;
        }
    }

    private sealed class DebugView
    {
        public DebugView(HttpTunnel value)
        {
        }
    }
}
