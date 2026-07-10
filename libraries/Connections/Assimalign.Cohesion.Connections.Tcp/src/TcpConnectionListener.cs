using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Internal;
using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp;

/// <summary>
/// Listens for inbound, reliable, ordered single-stream TCP connections on a local endpoint.
/// </summary>
/// <remarks>
/// The listening socket is bound lazily on the first call to <see cref="AcceptAsync(CancellationToken)"/>.
/// Each accepted connection is returned as a live <see cref="Connection"/> whose IO loops are
/// already running.
/// </remarks>
public sealed class TcpConnectionListener : ConnectionListener
{
    private readonly TcpConnectionListenerOptions _options;
    private readonly TcpConnectionSettings[] _settings;
    private readonly ListenerId _listenerId = ListenerId.New();
    private readonly ConcurrentDictionary<ConnectionId, TcpConnection> _connections = new();

    private EndPoint _endPoint;
    private Socket? _socket;
    private ConnectionProtocol _protocol;
    private string? _socketFilePath;
    private long _index; // long to prevent overflow
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpConnectionListener"/> class with default options.
    /// </summary>
    public TcpConnectionListener()
        : this(TcpConnectionListenerOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpConnectionListener"/> class.
    /// </summary>
    /// <param name="options">The binding and socket-tuning options for the listener.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public TcpConnectionListener(TcpConnectionListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _settings = options.CreateConnectionSettings();
        _endPoint = options.EndPoint;

        // The advertised protocol is known from the configured endpoint before binding; a Unix domain
        // socket endpoint yields a Unix domain socket listener. The actual bound protocol is re-derived
        // from the socket's address family at bind time for diagnostics (which also covers a file-handle
        // endpoint whose family is only known once the descriptor is adopted).
        _protocol = _endPoint is UnixDomainSocketEndPoint
            ? ConnectionProtocol.UnixDomainSocket
            : ConnectionProtocol.Tcp;

        Capabilities = new ConnectionCapabilities(
            _protocol,
            ConnectionDelivery.Stream,
            IsReliable: true,
            IsOrdered: true,
            IsMultiplexed: false,
            ConnectionSecurity.None);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Before the listener is bound this is the configured endpoint; afterwards it is the actual
    /// local endpoint of the listening socket (relevant when binding to port 0).
    /// </remarks>
    public override EndPoint EndPoint => _endPoint;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ConnectionCapabilities.Protocol"/> reports
    /// <see cref="ConnectionProtocol.UnixDomainSocket"/> when the configured endpoint is a
    /// <see cref="UnixDomainSocketEndPoint"/>, and <see cref="ConnectionProtocol.Tcp"/> otherwise.
    /// The delivery guarantees (reliable, ordered byte stream) are identical for both.
    /// </remarks>
    public override ConnectionCapabilities Capabilities { get; }

    /// <inheritdoc />
    public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        EnsureBound();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Socket socket = await _socket!.AcceptAsync(cancellationToken);

                TcpConnectionSettings settings = _settings[Interlocked.Increment(ref _index) % _settings.Length];

                if (socket.LocalEndPoint is IPEndPoint)
                {
                    socket.NoDelay = _options.NoDelay;
                }

                var connection = new TcpConnection(socket, settings, _listenerId);

                _connections.TryAdd(connection.Id, connection);

                connection.ConnectionClosed.Register(static state =>
                {
                    (TcpConnectionListener listener, TcpConnection closed) = ((TcpConnectionListener, TcpConnection))state!;

                    listener._connections.TryRemove(closed.Id, out _);

                }, (this, connection));

                return connection;
            }
            catch (ObjectDisposedException)
            {
                // The listening socket was closed; loop so cancellation can be observed.
                continue;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.OperationAborted)
            {
                // A call was made to DisposeAsync; loop so cancellation can be observed.
                continue;
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _socket?.Close();
        _socket?.Dispose();

        // Unlink the Unix domain socket file this listener bound so the path is free for the next bind.
        // Only a filesystem-backed path that this listener created is removed (never an inherited
        // file-handle socket or an abstract-namespace socket, which have no filesystem entry).
        if (_socketFilePath is not null)
        {
            UnixDomainSocketFile.Unlink(_socketFilePath);
            _socketFilePath = null;
        }

        // ConcurrentDictionary.Values returns a snapshot, so connections removing themselves
        // from the dictionary as they close do not invalidate the iteration.
        foreach (TcpConnection connection in _connections.Values)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();

        foreach (TcpConnectionSettings settings in _settings)
        {
            settings.PipeOptions.Dispose();
        }
    }

    /// <summary>
    /// Creates a new <see cref="TcpConnectionListener"/> configured by the supplied delegate.
    /// </summary>
    /// <param name="configure">A delegate used to configure the listener options.</param>
    /// <returns>A new <see cref="TcpConnectionListener"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static TcpConnectionListener Create(Action<TcpConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        TcpConnectionListenerOptions options = new();
        configure.Invoke(options);

        return new TcpConnectionListener(options);
    }

    private void EnsureBound()
    {
        if (_socket is not null)
        {
            return;
        }

        Socket socket = _endPoint is FileHandleEndPoint fileHandle
            ? AdoptInheritedSocket(fileHandle)
            : BindNewSocket();

        _protocol = SocketConnectionProtocol.FromAddressFamily(socket.AddressFamily);
        _endPoint = socket.LocalEndPoint ?? _endPoint;
        _socket = socket;

        ConnectionEventSource.Log.ListenerInitialized(_protocol, _listenerId);
    }

    /// <summary>
    /// Adopts a listening socket handed off by a parent process (systemd <c>.socket</c> activation,
    /// launchd, or a supervising process). The descriptor is already bound and listening, so it is
    /// wrapped and accepted on directly — re-binding or re-listening an inherited listening socket
    /// fails.
    /// </summary>
    private static Socket AdoptInheritedSocket(FileHandleEndPoint fileHandle)
    {
        /*
            We're passing "ownsHandle: true" here even though we don't necessarily
            own the handle because Socket.Dispose will clean-up everything safely.
            If the handle was already closed or disposed then the socket will
            be torn down gracefully, and if the caller never cleans up their handle
            then we'll do it for them.
        */
        return new Socket(new SafeSocketHandle((IntPtr)fileHandle.FileHandle, ownsHandle: true));
    }

    private Socket BindNewSocket()
    {
        if (_endPoint is UnixDomainSocketEndPoint)
        {
            // A Unix domain socket bound to a filesystem path leaves a socket file behind. Remove any
            // stale file left by a prior unclean shutdown so the bind does not fail with
            // AddressAlreadyInUse (rebind-after-crash), and remember the path so disposal can unlink it.
            _socketFilePath = UnixDomainSocketFile.ResolvePath(_endPoint);

            if (_socketFilePath is not null)
            {
                UnixDomainSocketFile.DeleteStale(_socketFilePath);
            }

            Socket unixSocket = new(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
            unixSocket.Bind(_endPoint);
            unixSocket.Listen(_options.Backlog);

            return unixSocket;
        }

        Socket socket = new(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        if (_endPoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
        {
            socket.DualMode = true;
        }

        socket.Bind(_endPoint);
        socket.Listen(_options.Backlog);

        return socket;
    }
}
