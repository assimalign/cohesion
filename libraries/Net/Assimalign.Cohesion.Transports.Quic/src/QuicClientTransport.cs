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
public sealed class QuicClientTransport : ClientTransport<QuicTransportConnection>
{
    private readonly QuicClientTransportOptions _options;
    private readonly TransportPipeline<QuicTransportContext> _pipeline;
    private readonly List<QuicTransportConnection> _connections;

    private bool _isDisposed;

    /// <summary>
    /// Creates a new QUIC client transport.
    /// </summary>
    /// <param name="options">The QUIC client options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public QuicClientTransport(QuicClientTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _pipeline = options.Pipeline;
        _connections = new List<QuicTransportConnection>();
    }

    /// <inheritdoc />
    public override TransportProtocol Protocol { get; } = TransportProtocol.Quic;

    /// <summary>
    /// Gets the active connections opened by this transport.
    /// </summary>
    public IReadOnlyCollection<QuicTransportConnection> Connections => _connections.AsReadOnly();

    /// <inheritdoc />
    public override async Task<QuicTransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(QuicClientTransport));

        var connectionOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = _options.EndPoint,
            ClientAuthenticationOptions = _options.ClientAuthenticationOptions,
            DefaultStreamErrorCode = _options.DefaultStreamErrorCode,
            DefaultCloseErrorCode = _options.DefaultCloseErrorCode
        };

        QuicConnection quicConnection = await QuicConnection.ConnectAsync(connectionOptions, cancellationToken).ConfigureAwait(false);
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
        // Add a callback to remove the connection from the list when it's aborted
        connection.ConnectionAborted.Register(con =>
        {
            _connections.Remove((QuicTransportConnection)con!);

        }, connection);

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

        foreach (QuicTransportConnection connection in _connections.ToArray())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
    }

    /// <summary>
    /// Creates a QUIC client transport using a configure callback.
    /// </summary>
    /// <param name="configure">The callback used to configure options.</param>
    /// <returns>A configured <see cref="QuicClientTransport"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static QuicClientTransport Create(Action<QuicClientTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        QuicClientTransportOptions options = new QuicClientTransportOptions();

        configure(options);

        return new QuicClientTransport(options);
    }
}
