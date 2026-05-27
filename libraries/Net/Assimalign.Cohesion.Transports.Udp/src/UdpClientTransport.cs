using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

[DebuggerDisplay("{Protocol} [{Kind}] - {_connections.Count}")]
public sealed class UdpClientTransport : ClientTransport<UdpTransportConnection>
{
    private readonly UdpClientTransportOptions _options;
    private readonly TransportPipeline<UdpTransportConnectionContext> _pipeline;
    private readonly List<UdpTransportConnection> _connections;

    private bool _isDisposed;

    /// <summary>
    /// Creates a new UDP client transport.
    /// </summary>
    /// <param name="options">The UDP client options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public UdpClientTransport(UdpClientTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _pipeline = options.Pipeline;
        _connections = new List<UdpTransportConnection>();
    }

    /// <inheritdoc />
    public override TransportProtocol Protocol { get; } = TransportProtocol.Udp;

    /// <summary>
    /// Gets the active connections opened by this transport.
    /// </summary>
    public IReadOnlyCollection<UdpTransportConnection> Connections => _connections.AsReadOnly();

    /// <inheritdoc />
    public override async Task<UdpTransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(UdpClientTransport));

        Socket socket = _options.EndPoint switch
        {
            UnixDomainSocketEndPoint => new Socket(_options.EndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Unspecified),
            _ => new Socket(_options.EndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        };

        if (_options.EndPoint is IPEndPoint ipEndPoint && ipEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = true;
        }

        await socket.ConnectAsync(_options.EndPoint, cancellationToken).ConfigureAwait(false);
        TransportPipeOptionsContext pipeOptions = _options.CreatePipeOptions();
        UdpTransportConnection connection;

        try
        {
            connection = new UdpTransportConnection(
                socket,
                Id,
                _pipeline,
                pipeOptions,
                ownsSocket: true);
        }
        catch
        {
            pipeOptions.Dispose();
            socket.Dispose();
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

        foreach (UdpTransportConnection connection in _connections.ToArray())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
    }

    /// <summary>
    /// Creates a UDP client transport using a configure callback.
    /// </summary>
    /// <param name="configure">The callback used to configure options.</param>
    /// <returns>A configured <see cref="UdpClientTransport"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static UdpClientTransport Create(Action<UdpClientTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        UdpClientTransportOptions options = new UdpClientTransportOptions();

        configure(options);

        return new UdpClientTransport(options);
    }
}
