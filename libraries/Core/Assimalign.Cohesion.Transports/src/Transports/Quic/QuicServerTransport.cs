using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

[DebuggerDisplay("{Protocol} [{Kind}] - {_connections.Count}")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public sealed class QuicServerTransport : ServerTransport<QuicTransportConnection>
{
    private readonly QuicServerTransportOptions _options;
    private readonly TransportPipeline _pipeline;
    private readonly List<QuicTransportConnection> _connections;
    private readonly Lock _listenerLock;

    private QuicListener? _listener;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new QUIC server transport.
    /// </summary>
    /// <param name="options">The QUIC server options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public QuicServerTransport(QuicServerTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _pipeline = options.BuildPipeline();
        _connections = new List<QuicTransportConnection>();
        _listenerLock = new Lock();
    }

    /// <inheritdoc />
    public override TransportProtocol Protocol { get; } = TransportProtocol.Quic;

    /// <summary>
    /// Gets the active connections accepted by this transport.
    /// </summary>
    public IReadOnlyCollection<QuicTransportConnection> Connections => _connections.AsReadOnly();

    /// <inheritdoc />
    public override async Task<QuicTransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(QuicServerTransport));

        await EnsureListenerAsync(cancellationToken).ConfigureAwait(false);

        QuicConnection quicConnection = await _listener!.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
        TransportStreamPipeOptionsContext streamOptions = _options.CreateStreamOptions();
        QuicTransportConnection connection;

        try
        {
            connection = new QuicTransportConnection(
                quicConnection,
                Id,
                _pipeline,
                _options.OutboundStreamType,
                streamOptions,
                _options.DefaultCloseErrorCode);
        }
        catch
        {
            streamOptions.Dispose();
            await quicConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        connection.OnDispose = () => _connections.Remove(connection);

        _connections.Add(connection);

        TransportEventSource.Log.TransportInitialized(Protocol, Id);

        return connection;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }

        foreach (QuicTransportConnection connection in _connections.ToArray())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
    }

    /// <summary>
    /// Creates a QUIC server transport using a configure callback.
    /// </summary>
    /// <param name="configure">The callback used to configure options.</param>
    /// <returns>A configured <see cref="QuicServerTransport"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static QuicServerTransport Create(Action<QuicServerTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        QuicServerTransportOptions options = new QuicServerTransportOptions();

        configure(options);

        return new QuicServerTransport(options);
    }

    private async Task EnsureListenerAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            return;
        }

        ValidateServerAuthenticationOptions();

        QuicListener? listener;

        lock (_listenerLock)
        {
            if (_listener is not null)
            {
                return;
            }
        }

        listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = _options.EndPoint,
            ApplicationProtocols = _options.ServerAuthenticationOptions.ApplicationProtocols!,
            ListenBacklog = _options.Backlog,
            ConnectionOptionsCallback = (connection, sslClientHelloInfo, token) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                ServerAuthenticationOptions = _options.ServerAuthenticationOptions,
                MaxInboundBidirectionalStreams = _options.MaxBidirectionalStreamCount,
                MaxInboundUnidirectionalStreams = _options.MaxUnidirectionalStreamCount,
                DefaultCloseErrorCode = _options.DefaultCloseErrorCode,
                DefaultStreamErrorCode = _options.DefaultStreamErrorCode
            })
        }, cancellationToken).ConfigureAwait(false);

        lock (_listenerLock)
        {
            _listener ??= listener;
        }
    }

    private void ValidateServerAuthenticationOptions()
    {
        if (_options.ServerAuthenticationOptions.ServerCertificate is null &&
            _options.ServerAuthenticationOptions.ServerCertificateContext is null &&
            _options.ServerAuthenticationOptions.ServerCertificateSelectionCallback is null)
        {
            throw new InvalidOperationException("A server certificate is required for QUIC server authentication.");
        }

        if (_options.ServerAuthenticationOptions.ApplicationProtocols is null ||
            _options.ServerAuthenticationOptions.ApplicationProtocols.Count == 0)
        {
            throw new InvalidOperationException("At least one application protocol is required for QUIC server authentication.");
        }
    }
}
