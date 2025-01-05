#if NET7_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;



[RequiresPreviewFeatures]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public sealed class QuicServerTransport : ServerTransport, IAsyncDisposable
{
    private QuicListener listener;

    private readonly QuicServerTransportOptions options;
    private readonly List<ITransportConnection> connections = new();

    public QuicServerTransport(QuicServerTransportOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        this.options = options;
        Middleware = options.Middleware;
    }

    public IReadOnlyCollection<ITransportConnection> Connections => connections.AsReadOnly();
    public override ProtocolType ProtocolType => ProtocolType.Quic;
    public override TransportMiddlewareHandler Middleware { get; }
    public override async Task<ITransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        listener = await QuicListener.ListenAsync(new()
        {
            ListenEndPoint = options.EndPoint,
            ListenBacklog = options.Backlog,
            ConnectionOptionsCallback = (connection, info, cancellationToken) => ValueTask.FromResult(new QuicServerConnectionOptions()
            {
                ServerAuthenticationOptions = new()
                {
                    AllowRenegotiation = true,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ApplicationProtocols = new()
                        {
                            SslApplicationProtocol.Http3
                        }
                },
                IdleTimeout = Timeout.InfiniteTimeSpan, // Kestrel manages connection lifetimes itself so it can send GoAway's.
                MaxInboundBidirectionalStreams = 100,
                MaxInboundUnidirectionalStreams = 10,
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
            }),
            ApplicationProtocols = new()
            {
                SslApplicationProtocol.Http3
            }
        }, cancellationToken);
        while (true)
        {
            try
            {
                var connection = await listener.AcceptConnectionAsync(cancellationToken);
                var stream = connection.AcceptInboundStreamAsync();

                return new QuicTransportConnection();
            }
            catch (Exception exception)
            {
                continue;
            }
        }
    }

    public override void Dispose()
    {
        (this as IAsyncDisposable).DisposeAsync().GetAwaiter().GetResult();
    }
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return listener.DisposeAsync();
    }


    public static QuicServerTransport Create(Action<QuicServerTransportOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new QuicServerTransportOptions();

        configure.Invoke(options);

        return new QuicServerTransport(options);
    }
}
#endif
