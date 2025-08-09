#if NET7_0_OR_GREATER
using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

[RequiresPreviewFeatures]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public sealed class QuicServerTransport : ServerTransport<QuicTransportConnection, QuicTransportContext>, IAsyncDisposable
{
    private QuicListener _listener;
    private TransportPipeline? _pipeline;

    private readonly QuicServerTransportOptions _options;
    private readonly List<ITransportConnection> connections = new();
    private readonly ConditionalWeakTable<QuicConnection, QuicTransportConnection> _pendingConnections;


    public QuicServerTransport(QuicServerTransportOptions options)
    {
        _options = ThrowHelper.ThrowIfNull(options);

        // If the SslServerAuthenticationOptions doesn't have a cert or protocols then the
        // QUIC connection will fail and the client receives an unhelpful message.
        // Validate the options on the server and log issues to improve debugging.
        ValidateServerAuthenticationOptions(_options.ServerAuthenticationOptions);
    }

    public override ProtocolType Protocol => ProtocolType.Quic;
    public IReadOnlyCollection<ITransportConnection> Connections => connections.AsReadOnly();
    public override async Task<QuicTransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        if (_pipeline is null)
        {
            _pipeline = (TransportPipeline)(this as ITransportPipelineBuilder).Build();
        }
        if (_listener is null)
        {
            _listener = await QuicListener.ListenAsync(new()
            {
                ListenEndPoint = _options.EndPoint,
                ApplicationProtocols =  _options.AcceptApplicationProtocols,
                ListenBacklog = _options.Backlog,
                ConnectionOptionsCallback = async (connection, info, cancellationToken) =>
                {
                    // Create the connection context inside the callback because it's passed
                    // to the connection callback. The field is then read once AcceptConnectionAsync
                    // finishes awaiting.
                    var currentAcceptingConnection = new QuicTransportConnection(connection);
                    
                    _pendingConnections.Add(connection, currentAcceptingConnection);

                    var context = new QuicTransportContext(currentAcceptingConnection)
                    {
                    };

                    await _pipeline.ExecuteAsync(context, cancellationToken);

                    return new QuicServerConnectionOptions()
                    {
                        ServerAuthenticationOptions = _options.ServerAuthenticationOptions!,
                        IdleTimeout = Timeout.InfiniteTimeSpan, // Manages connection lifetimes itself so it can send GoAway's.
                        MaxInboundBidirectionalStreams = _options.MaxBidirectionalStreamCount,
                        MaxInboundUnidirectionalStreams = _options.MaxUnidirectionalStreamCount,
                        DefaultCloseErrorCode = _options.DefaultCloseErrorCode,
                        DefaultStreamErrorCode = _options.DefaultStreamErrorCode,
                    };
                },
            }, cancellationToken);
        }
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                QuicConnection quicConnection = await _listener.AcceptConnectionAsync(cancellationToken);

                if (!_pendingConnections.TryGetValue(quicConnection, out var connection))
                {
                    throw new InvalidOperationException("Couldn't find ConnectionContext for QuicConnection.");
                }
                else
                {
                    _pendingConnections.Remove(quicConnection);
                }

                quicConnection.

                return connection;
            }
            catch (Exception exception)
            {
                continue;
            }
        }

        return null;
    }

    public override void Dispose()
    {
        (this as IAsyncDisposable).DisposeAsync().GetAwaiter().GetResult();
    }
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return _listener.DisposeAsync();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    public new QuicServerTransport Use(Func<QuicTransportContext, TransportMiddleware, Task> middleware)
    {
        ThrowHelper.ThrowIfNull(middleware);

        base.Use(middleware);

        return this;
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


    private void ValidateServerAuthenticationOptions(SslServerAuthenticationOptions serverAuthenticationOptions)
    {
        if (serverAuthenticationOptions.ServerCertificate == null &&
            serverAuthenticationOptions.ServerCertificateContext == null &&
            serverAuthenticationOptions.ServerCertificateSelectionCallback == null)
        {
            QuicLog.ConnectionListenerCertificateNotSpecified(_log);
        }
        if (serverAuthenticationOptions.ApplicationProtocols == null || serverAuthenticationOptions.ApplicationProtocols.Count == 0)
        {
            QuicLog.ConnectionListenerApplicationProtocolsNotSpecified(_log);
        }
    }
}
#endif
