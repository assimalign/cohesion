using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Udp;

/// <summary>
/// Creates message-oriented UDP datagram connections for both the server (bind) and client (connect) roles.
/// </summary>
/// <remarks>
/// UDP is connectionless, so there is no listener/accept lifecycle: a server binds a local endpoint and
/// receives datagrams from any peer, while a client connects its socket to a fixed remote peer. Both roles
/// surface the same <see cref="IDatagramConnection"/> contract. Only IPv4 and IPv6 endpoints are supported.
/// </remarks>
public sealed class UdpConnectionFactory
{
    private readonly UdpBindOptions _serverOptions;
    private readonly UdpConnectOptions _clientOptions;

    /// <summary>
    /// Creates a new UDP connection factory using default server and client options.
    /// </summary>
    public UdpConnectionFactory()
        : this(null, null)
    {
    }

    /// <summary>
    /// Creates a new UDP connection factory.
    /// </summary>
    /// <param name="serverOptions">
    /// The default options applied when binding server sockets, or <see langword="null"/> to use
    /// <see cref="UdpBindOptions.Default"/>.
    /// </param>
    /// <param name="clientOptions">
    /// The default options applied when connecting client sockets, or <see langword="null"/> to use
    /// <see cref="UdpConnectOptions.Default"/>.
    /// </param>
    public UdpConnectionFactory(UdpBindOptions? serverOptions, UdpConnectOptions? clientOptions)
    {
        _serverOptions = serverOptions ?? UdpBindOptions.Default;
        _clientOptions = clientOptions ?? UdpConnectOptions.Default;
    }

    /// <summary>
    /// Gets the capabilities of connections produced by this factory.
    /// </summary>
    public ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Udp,
        ConnectionDelivery.Datagram,
        IsReliable: false,
        IsOrdered: false,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    /// <summary>
    /// Binds a server-side UDP socket using the factory's server options.
    /// </summary>
    /// <returns>An unconnected datagram connection bound to the configured local endpoint.</returns>
    /// <exception cref="NotSupportedException">Thrown when the configured endpoint is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the endpoint cannot be bound.</exception>
    public IDatagramConnection Bind()
    {
        return BindCore(_serverOptions.EndPoint, _serverOptions);
    }

    /// <summary>
    /// Binds a server-side UDP socket to the supplied local endpoint, applying the factory's
    /// server options for the remaining socket settings.
    /// </summary>
    /// <param name="endPoint">The local endpoint to bind.</param>
    /// <returns>An unconnected datagram connection bound to <paramref name="endPoint"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="endPoint"/> is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the endpoint cannot be bound.</exception>
    public IDatagramConnection Bind(EndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        return BindCore(endPoint, _serverOptions);
    }

    /// <summary>
    /// Binds a server-side UDP socket using the supplied options.
    /// </summary>
    /// <param name="options">The options describing the endpoint and socket settings to apply.</param>
    /// <returns>An unconnected datagram connection bound to the configured local endpoint.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when the configured endpoint is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the endpoint cannot be bound.</exception>
    public IDatagramConnection Bind(UdpBindOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return BindCore(options.EndPoint, options);
    }

    /// <summary>
    /// Binds a server-side UDP socket using the factory's server options.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the bind operation.</param>
    /// <returns>An unconnected datagram connection bound to the configured local endpoint.</returns>
    /// <exception cref="NotSupportedException">Thrown when the configured endpoint is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the endpoint cannot be bound.</exception>
    /// <remarks>
    /// Binding a UDP socket is a synchronous operation, so this overload always completes synchronously;
    /// it exists for call-site symmetry with connection-oriented drivers. If <paramref name="cancellationToken"/>
    /// is already canceled, a canceled task is returned without binding.
    /// </remarks>
    public ValueTask<IDatagramConnection> BindAsync(CancellationToken cancellationToken = default)
    {
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<IDatagramConnection>(cancellationToken)
            : ValueTask.FromResult(Bind());
    }

    /// <summary>
    /// Binds a server-side UDP socket to the supplied local endpoint, applying the factory's
    /// server options for the remaining socket settings.
    /// </summary>
    /// <param name="endPoint">The local endpoint to bind.</param>
    /// <param name="cancellationToken">A token to cancel the bind operation.</param>
    /// <returns>An unconnected datagram connection bound to <paramref name="endPoint"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="endPoint"/> is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the endpoint cannot be bound.</exception>
    /// <remarks>
    /// Binding a UDP socket is a synchronous operation, so this overload always completes synchronously;
    /// it exists for call-site symmetry with connection-oriented drivers. If <paramref name="cancellationToken"/>
    /// is already canceled, a canceled task is returned without binding.
    /// </remarks>
    public ValueTask<IDatagramConnection> BindAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<IDatagramConnection>(cancellationToken)
            : ValueTask.FromResult(Bind(endPoint));
    }

    /// <summary>
    /// Binds a server-side UDP socket using the supplied options.
    /// </summary>
    /// <param name="options">The options describing the endpoint and socket settings to apply.</param>
    /// <param name="cancellationToken">A token to cancel the bind operation.</param>
    /// <returns>An unconnected datagram connection bound to the configured local endpoint.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when the configured endpoint is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the endpoint cannot be bound.</exception>
    /// <remarks>
    /// Binding a UDP socket is a synchronous operation, so this overload always completes synchronously;
    /// it exists for call-site symmetry with connection-oriented drivers. If <paramref name="cancellationToken"/>
    /// is already canceled, a canceled task is returned without binding.
    /// </remarks>
    public ValueTask<IDatagramConnection> BindAsync(UdpBindOptions options, CancellationToken cancellationToken = default)
    {
        return cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<IDatagramConnection>(cancellationToken)
            : ValueTask.FromResult(Bind(options));
    }

    /// <summary>
    /// Connects a client-side UDP socket to the supplied remote endpoint using the factory's client options.
    /// </summary>
    /// <param name="remoteEndPoint">The remote endpoint to connect to.</param>
    /// <returns>A connected datagram connection whose <see cref="IDatagramConnection.RemoteEndPoint"/> is the peer.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="remoteEndPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="remoteEndPoint"/> is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the socket cannot be connected.</exception>
    public IDatagramConnection Connect(EndPoint remoteEndPoint)
    {
        return Connect(remoteEndPoint, _clientOptions);
    }

    /// <summary>
    /// Connects a client-side UDP socket to the supplied remote endpoint using the supplied options.
    /// </summary>
    /// <param name="remoteEndPoint">The remote endpoint to connect to.</param>
    /// <param name="options">The options describing the socket settings to apply.</param>
    /// <returns>A connected datagram connection whose <see cref="IDatagramConnection.RemoteEndPoint"/> is the peer.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="remoteEndPoint"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="remoteEndPoint"/> is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the socket cannot be connected.</exception>
    public IDatagramConnection Connect(EndPoint remoteEndPoint, UdpConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(options);

        Socket socket = CreateSocket(remoteEndPoint.AddressFamily);

        try
        {
            ApplyClientOptions(socket, options);
            socket.Connect(remoteEndPoint);

            return new UdpDatagramConnection(socket, socket.LocalEndPoint!, socket.RemoteEndPoint);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects a client-side UDP socket to the supplied remote endpoint using the factory's client options.
    /// </summary>
    /// <param name="remoteEndPoint">The remote endpoint to connect to.</param>
    /// <param name="cancellationToken">A token to cancel the connect operation.</param>
    /// <returns>A connected datagram connection whose <see cref="IDatagramConnection.RemoteEndPoint"/> is the peer.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="remoteEndPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="remoteEndPoint"/> is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the socket cannot be connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    public ValueTask<IDatagramConnection> ConnectAsync(EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        return ConnectAsync(remoteEndPoint, _clientOptions, cancellationToken);
    }

    /// <summary>
    /// Connects a client-side UDP socket to the supplied remote endpoint using the supplied options.
    /// </summary>
    /// <param name="remoteEndPoint">The remote endpoint to connect to.</param>
    /// <param name="options">The options describing the socket settings to apply.</param>
    /// <param name="cancellationToken">A token to cancel the connect operation.</param>
    /// <returns>A connected datagram connection whose <see cref="IDatagramConnection.RemoteEndPoint"/> is the peer.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="remoteEndPoint"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="remoteEndPoint"/> is not IPv4 or IPv6.</exception>
    /// <exception cref="SocketException">Thrown when the socket cannot be connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    public async ValueTask<IDatagramConnection> ConnectAsync(EndPoint remoteEndPoint, UdpConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(options);

        Socket socket = CreateSocket(remoteEndPoint.AddressFamily);

        try
        {
            ApplyClientOptions(socket, options);
            await socket.ConnectAsync(remoteEndPoint, cancellationToken).ConfigureAwait(false);

            return new UdpDatagramConnection(socket, socket.LocalEndPoint!, socket.RemoteEndPoint);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a UDP connection factory using a configure callback for the bind (server) side.
    /// </summary>
    /// <param name="configure">The callback used to configure the server options.</param>
    /// <returns>A configured <see cref="UdpConnectionFactory"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static UdpConnectionFactory Create(Action<UdpBindOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        UdpBindOptions options = new UdpBindOptions();

        configure(options);

        return new UdpConnectionFactory(options, null);
    }

    private static IDatagramConnection BindCore(EndPoint endPoint, UdpBindOptions options)
    {
        Socket socket = CreateSocket(endPoint.AddressFamily);

        try
        {
            if (options.DualMode && socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = true;
            }

            if (options.ReuseAddress)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            if (options.ReceiveBufferSize is int receiveBufferSize)
            {
                socket.ReceiveBufferSize = receiveBufferSize;
            }

            if (options.SendBufferSize is int sendBufferSize)
            {
                socket.SendBufferSize = sendBufferSize;
            }

            socket.Bind(endPoint);

            return new UdpDatagramConnection(socket, socket.LocalEndPoint!, null);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void ApplyClientOptions(Socket socket, UdpConnectOptions options)
    {
        if (options.DualMode && socket.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = true;
        }

        if (options.ReceiveBufferSize is int receiveBufferSize)
        {
            socket.ReceiveBufferSize = receiveBufferSize;
        }

        if (options.SendBufferSize is int sendBufferSize)
        {
            socket.SendBufferSize = sendBufferSize;
        }
    }

    private static Socket CreateSocket(AddressFamily addressFamily)
    {
        if (addressFamily is not AddressFamily.InterNetwork and not AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException($"The UDP transport only supports IPv4 and IPv6 endpoints; address family '{addressFamily}' is not supported.");
        }

        return new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
    }
}
