using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp;

/// <summary>
/// Listens for inbound, reliable, ordered single-stream TCP connections on a local endpoint.
/// </summary>
/// <remarks>
/// <see cref="BindAsync(CancellationToken)"/> acquires the listening socket explicitly. For backward
/// compatibility, <see cref="AcceptAsync(CancellationToken)"/> binds the listener when necessary.
/// Each accepted connection is returned as a live <see cref="Connection"/> whose IO loops are
/// already running.
/// </remarks>
public sealed class TcpConnectionListener : ConnectionListener
{
    private readonly TcpConnectionListenerOptions _options;
    private readonly TcpConnectionSettings[] _settings;
    private readonly ListenerId _listenerId = ListenerId.New();
    private readonly ConcurrentDictionary<ConnectionId, TcpConnection> _connections = new();
    private readonly Func<Socket, CancellationToken, ValueTask<Socket>> _acceptSocket;
    private readonly TcpAcceptBackoff _backoff = new();
    // Cancelled on disposal to end a back-off wait, and never disposed, so a wait that races disposal can
    // still link to its token.
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Lock _gate = new();

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
        : this(options, AcceptSocketAsync)
    {
    }

    // The test seam: acceptSocket stands in for the accept on the listening socket, so a test can fail it
    // with errors a real socket cannot be made to report on demand, such as running out of descriptors.
    internal TcpConnectionListener(TcpConnectionListenerOptions options, Func<Socket, CancellationToken, ValueTask<Socket>> acceptSocket)
    {
        ArgumentNullException.ThrowIfNull(options);

        _acceptSocket = acceptSocket;
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
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_socket is not null)
            {
                return ValueTask.CompletedTask;
            }

            (Socket socket, string? socketFilePath) = _endPoint is FileHandleEndPoint fileHandle
                ? (AdoptInheritedSocket(fileHandle), null)
                : BindNewSocket();

            _protocol = SocketConnectionProtocol.FromAddressFamily(socket.AddressFamily);
            _endPoint = socket.LocalEndPoint ?? _endPoint;
            _socketFilePath = socketFilePath;
            _socket = socket;

            TcpConnectionEventSource.Log.ListenerBound(_listenerId, _protocol, _endPoint);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A failure that belongs to one queued connection is skipped, and the next connection is accepted: its
    /// client reset it before the accept, or, on Linux, a network error was pending on it. A connection whose
    /// accepted socket cannot be set up is closed and skipped the same way; on macOS, for example, setting
    /// <c>TCP_NODELAY</c> fails once the client has reset the connection.
    /// </para>
    /// <para>
    /// When the process or the system has run out of descriptors or buffers, the accept is retried after a
    /// wait that starts at 5 milliseconds and doubles with each consecutive failure up to 1 second. On Unix,
    /// an accept that fails with an error .NET reports only as <see cref="SocketError.SocketError"/> is retried
    /// the same way, because that value cannot tell <c>ENOMEM</c> from the network errors <c>EPROTO</c> and
    /// <c>ENONET</c>. The schedule starts over with every call. Cancelling <paramref name="cancellationToken"/>
    /// or disposing the listener ends the wait at once. Each wait is reported by the
    /// <c>Assimalign.Cohesion.Connections.Tcp</c> event source, at most once a second per listener.
    /// </para>
    /// <para>
    /// Any other failure of the accept escapes, because it leaves the listening socket unable to accept.
    /// </para>
    /// </remarks>
    public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        await BindAsync(cancellationToken).ConfigureAwait(false);

        // The current back-off. It is local to this call, so the schedule starts over after a successful accept.
        TimeSpan backoff = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket listenerSocket;

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                listenerSocket = _socket!;
            }

            Socket socket;

            // The filters below classify errors of the accept, so the accept is all this block covers. Setting up
            // the socket it returns fails for reasons of its own, and is handled per connection further down.
            try
            {
                socket = await _acceptSocket(listenerSocket, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_isDisposed, this);
                }

                continue;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.OperationAborted)
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_isDisposed, this);
                }

                continue;
            }
            catch (SocketException exception) when (TcpAcceptErrors.IsQueuedConnectionFailure(exception.SocketErrorCode, listenerSocket))
            {
                // The failure belongs to the one queued connection the accept was taking: its client closed it
                // while it waited in the accept queue, or, on Linux, a network error was pending on it. Skip it
                // and accept the next one. Letting it escape would stop the listener, and any client could do
                // that with one reset (#1308).
                TcpConnectionEventSource.Log.AcceptSkipped(_listenerId, exception.SocketErrorCode);

                continue;
            }
            catch (SocketException exception) when (TcpAcceptErrors.IsResourceExhaustion(exception.SocketErrorCode, listenerSocket))
            {
                // The process or the system is out of descriptors or buffers. That clears once connections
                // close, so wait and retry. Letting it escape would stop the listener for good, and a client
                // that holds enough connections open could do that (#1312). Retrying at once would spin.
                backoff = TcpAcceptBackoff.NextDelay(backoff);

                if (_backoff.TryReport(Stopwatch.GetTimestamp(), out int unreportedBackoffs))
                {
                    TcpConnectionEventSource.Log.AcceptBackoff(_listenerId, exception.SocketErrorCode, backoff, unreportedBackoffs);
                }

                await WaitBeforeRetryAsync(backoff, cancellationToken).ConfigureAwait(false);

                continue;
            }

            TcpConnection? connection = null;

            try
            {
                lock (_gate)
                {
                    if (!_isDisposed)
                    {
                        TcpConnectionSettings settings = _settings[Interlocked.Increment(ref _index) % _settings.Length];

                        if (socket.LocalEndPoint is IPEndPoint)
                        {
                            socket.NoDelay = _options.NoDelay;
                        }

                        connection = new TcpConnection(socket, settings, _listenerId);
                        _connections.TryAdd(connection.Id, connection);
                    }
                }
            }
            catch (SocketException exception)
            {
                // The accepted socket could not be set up, which is that one connection's failure: on macOS,
                // setting TCP_NODELAY fails with EINVAL once the client has reset the connection. Close the socket,
                // which nothing else owns yet, and accept the next connection. Letting the error escape would stop
                // the listener over one client, as a reset before the accept did until #1308.
                socket.Dispose();
                TcpConnectionEventSource.Log.AcceptedConnectionDropped(_listenerId, exception.SocketErrorCode);

                continue;
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            if (connection is null)
            {
                // Disposed while the accept completed: the socket was never handed out, so close it here.
                socket.Dispose();
                throw new ObjectDisposedException(GetType().FullName);
            }

            connection.ConnectionClosed.Register(static state =>
            {
                (TcpConnectionListener listener, TcpConnection closed) = ((TcpConnectionListener, TcpConnection))state!;

                listener._connections.TryRemove(closed.Id, out _);

            }, (this, connection));

            return connection;
        }

        throw new OperationCanceledException(cancellationToken);
    }

    // Waits out a back-off. Cancellation and disposal both end the wait early, and the accept loop then sees
    // which of the two it was, so the wait itself never throws.
    private async ValueTask WaitBeforeRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);

        try
        {
            await Task.Delay(delay, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // On Windows an accept is an AcceptEx into a socket created before the call, and the OS can attach
    // an incoming client to that socket before the accept completes. When the accept is cancelled, or
    // the listening socket closes, at that moment, .NET reports the failure without closing the socket
    // it created, so the client stays connected to a socket nobody owns until a finalizer runs (#1093:
    // a request sent during host shutdown waited out its whole timeout). Accepting into a socket this
    // listener owns lets it close that socket on every path that does not hand it back. Unix accepts
    // with accept(2) after the connection is queued, so nothing is attached early there.
    private static async ValueTask<Socket> AcceptSocketAsync(Socket listenerSocket, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return await listenerSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        }

        Socket acceptSocket = new(listenerSocket.AddressFamily, listenerSocket.SocketType, listenerSocket.ProtocolType);
        bool accepted = false;

        try
        {
            Socket socket = await listenerSocket.AcceptAsync(acceptSocket, cancellationToken).ConfigureAwait(false);
            accepted = true;
            return socket;
        }
        finally
        {
            if (!accepted)
            {
                acceptSocket.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        Socket? socket;
        string? socketFilePath;

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            socket = _socket;
            _socket = null;
            socketFilePath = _socketFilePath;
            _socketFilePath = null;
        }

        // Outside the gate: cancelling can run an accept's continuation inline, and that continuation takes
        // the gate to observe the disposal.
        _disposeCancellation.Cancel();

        socket?.Close();
        socket?.Dispose();

        if (socket is not null)
        {
            TcpConnectionEventSource.Log.ListenerClosed(_listenerId);
        }

        // Unlink the Unix domain socket file this listener bound so the path is free for the next bind.
        // Only a filesystem-backed path that this listener created is removed (never an inherited
        // file-handle socket or an abstract-namespace socket, which have no filesystem entry).
        if (socketFilePath is not null)
        {
            UnixDomainSocketFile.Unlink(socketFilePath);
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

    private (Socket Socket, string? SocketFilePath) BindNewSocket()
    {
        if (_endPoint is UnixDomainSocketEndPoint)
        {
            // A Unix domain socket bound to a filesystem path leaves a socket file behind. Remove any
            // stale file left by a prior unclean shutdown so the bind does not fail with
            // AddressAlreadyInUse (rebind-after-crash), and remember the path so disposal can unlink it.
            string? socketFilePath = UnixDomainSocketFile.ResolvePath(_endPoint);

            if (socketFilePath is not null)
            {
                UnixDomainSocketFile.DeleteStale(socketFilePath);
            }

            Socket unixSocket = new(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
            bool isBound = false;

            try
            {
                unixSocket.Bind(_endPoint);
                isBound = true;
                unixSocket.Listen(_options.Backlog);

                return (unixSocket, socketFilePath);
            }
            catch
            {
                unixSocket.Dispose();

                if (isBound && socketFilePath is not null)
                {
                    UnixDomainSocketFile.Unlink(socketFilePath);
                }

                throw;
            }
        }

        Socket socket = new(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            if (_endPoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
            {
                socket.DualMode = true;
            }

            socket.Bind(_endPoint);
            socket.Listen(_options.Backlog);

            return (socket, null);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
