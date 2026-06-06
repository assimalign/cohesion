using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

[DebuggerDisplay("{Protocol} [{Kind}] - {_connections.Count}")]
public sealed class UdpServerTransport : ServerTransport<UdpTransportConnection>
{
    private const int DatagramBufferSize = 65_535;

    private readonly UdpServerTransportOptions _options;
    private readonly TransportPipeline<UdpTransportConnectionContext> _pipeline;
    private readonly List<UdpTransportConnection> _connections;
    private readonly Dictionary<string, UdpTransportConnection> _peerConnections;
    private readonly Lock _stateLock;

    private Socket? _listener;
    private Channel<UdpTransportConnection>? _pendingAccepts;
    private CancellationTokenSource? _listenTokenSource;
    private Task? _receiveLoopTask;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new UDP server transport.
    /// </summary>
    /// <param name="options">The UDP server options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public UdpServerTransport(UdpServerTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _pipeline = options.Pipeline;
        _connections = new List<UdpTransportConnection>();
        _peerConnections = new Dictionary<string, UdpTransportConnection>(StringComparer.Ordinal);
        _stateLock = new Lock();
    }

    /// <inheritdoc />
    public override TransportProtocol Protocol { get; } = TransportProtocol.Udp;

    /// <summary>
    /// Gets the active connections accepted by this transport.
    /// </summary>
    public IReadOnlyCollection<UdpTransportConnection> Connections => _connections.AsReadOnly();

    /// <inheritdoc />
    public override async Task<UdpTransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(UdpServerTransport));

        EnsureListenerStarted();

        try
        {
            return await _pendingAccepts!.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(UdpServerTransport));
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        Socket? listener;
        Task? receiveLoopTask;
        CancellationTokenSource? listenTokenSource;
        Channel<UdpTransportConnection>? pendingAccepts;
        UdpTransportConnection[] connections;

        lock (_stateLock)
        {
            listener = _listener;
            _listener = null;

            receiveLoopTask = _receiveLoopTask;
            _receiveLoopTask = null;

            listenTokenSource = _listenTokenSource;
            _listenTokenSource = null;

            pendingAccepts = _pendingAccepts;
            _pendingAccepts = null;

            connections = _connections.ToArray();
            _connections.Clear();
            _peerConnections.Clear();
        }

        listenTokenSource?.Cancel();
        listener?.Dispose();
        pendingAccepts?.Writer.TryComplete();

        if (receiveLoopTask is not null)
        {
            await AwaitWithoutException(receiveLoopTask).ConfigureAwait(false);
        }

        if (listenTokenSource is not null)
        {
            listenTokenSource.Dispose();
        }

        foreach (UdpTransportConnection connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a UDP server transport using a configure callback.
    /// </summary>
    /// <param name="configure">The callback used to configure options.</param>
    /// <returns>A configured <see cref="UdpServerTransport"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static UdpServerTransport Create(Action<UdpServerTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        UdpServerTransportOptions options = new UdpServerTransportOptions();

        configure(options);

        return new UdpServerTransport(options);
    }

    private Socket CreateAndBindSocket()
    {
        Socket socket = _options.EndPoint switch
        {
            UnixDomainSocketEndPoint => new Socket(
                _options.EndPoint.AddressFamily, 
                SocketType.Dgram, 
                ProtocolType.Unspecified),

            _ => new Socket(
                _options.EndPoint.AddressFamily, 
                SocketType.Dgram, 
                ProtocolType.Udp)
        };

        if (_options.EndPoint is IPEndPoint ipEndPoint && ipEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = true;
        }

        socket.Bind(_options.EndPoint);

        return socket;
    }

    private void EnsureListenerStarted()
    {
        if (_listener is not null)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_listener is not null)
            {
                return;
            }

            _listener = CreateAndBindSocket();
            _pendingAccepts = CreatePendingAcceptChannel();
            _listenTokenSource = new CancellationTokenSource();
            _receiveLoopTask = ReceiveLoopAsync(_listener, _listenTokenSource.Token);
        }

        TransportEventSource.Log.TransportInitialized(Protocol, Id);
    }

    private Channel<UdpTransportConnection> CreatePendingAcceptChannel()
    {
        if (_options.PendingAcceptQueueCapacity <= 0)
        {
            return Channel.CreateUnbounded<UdpTransportConnection>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        }

        return Channel.CreateBounded<UdpTransportConnection>(new BoundedChannelOptions(_options.PendingAcceptQueueCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    private async Task ReceiveLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        Exception? error = null;
        byte[] buffer = new byte[DatagramBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                EndPoint remoteTemplate = CreateRemoteTemplate(listener.AddressFamily);
                SocketReceiveFromResult result = await listener.ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, cancellationToken).ConfigureAwait(false);

                if (result.ReceivedBytes <= 0)
                {
                    continue;
                }

                EndPoint remoteEndPoint = CloneEndPoint(result.RemoteEndPoint);
                UdpTransportConnection connection = await GetOrCreatePeerConnectionAsync(listener, remoteEndPoint, cancellationToken).ConfigureAwait(false);

                ReadOnlyMemory<byte> datagram = buffer.AsMemory(0, result.ReceivedBytes).ToArray();
                await connection.EnqueueInboundDatagramAsync(datagram, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        {
        }
        catch (ChannelClosedException) when (cancellationToken.IsCancellationRequested || _isDisposed)
        {
        }
        catch (Exception exception)
        {
            error = exception;
            TransportEventSource.Log.TransportConnectionError(Protocol, Id, ConnectionId.New(), exception.Message);
        }
        finally
        {
            _pendingAccepts?.Writer.TryComplete(error);
        }
    }

    private async ValueTask<UdpTransportConnection> GetOrCreatePeerConnectionAsync(
        Socket listener,
        EndPoint remoteEndPoint,
        CancellationToken cancellationToken)
    {
        string peerKey = CreatePeerKey(remoteEndPoint);
        UdpTransportConnection? connection = null;
        bool created = false;

        lock (_stateLock)
        {
            if (_peerConnections.TryGetValue(peerKey, out UdpTransportConnection? existing))
            {
                return existing;
            }

            EndPoint localEndPoint = CloneEndPoint(listener.LocalEndPoint!);
            EndPoint remoteEndPointForContext = CloneEndPoint(remoteEndPoint);
            EndPoint remoteEndPointForSend = CloneEndPoint(remoteEndPoint);
            TransportPipeOptionsContext pipeOptions = _options.CreatePipeOptions();

            try
            {
                connection = new UdpTransportConnection(
                    Id,
                    _pipeline,
                    localEndPoint,
                    remoteEndPointForContext,
                    pipeOptions,
                    (buffer, token) => SendToPeerAsync(remoteEndPointForSend, buffer, token));
            }
            catch
            {
                pipeOptions.Dispose();
                throw;
            }

            connection.ConnectionAborted.Register(() =>
            {
                RemoveConnection(peerKey, connection);
            });

            _peerConnections.Add(peerKey, connection);
            _connections.Add(connection);
            created = true;
        }

        if (created)
        {
            try
            {
                await _pendingAccepts!.Writer.WriteAsync(connection!, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection!.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        return connection!;
    }

    private ValueTask<int> SendToPeerAsync(EndPoint remoteEndPoint, ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
    {
        Socket? listener;

        lock (_stateLock)
        {
            listener = _listener;
        }

        return listener is null
            ? ValueTask.FromException<int>(new ObjectDisposedException(nameof(UdpServerTransport)))
            : listener.SendToAsync(datagram, SocketFlags.None, remoteEndPoint, cancellationToken);
    }

    private void RemoveConnection(string peerKey, UdpTransportConnection connection)
    {
        lock (_stateLock)
        {
            if (_peerConnections.TryGetValue(peerKey, out UdpTransportConnection? current) && ReferenceEquals(current, connection))
            {
                _peerConnections.Remove(peerKey);
            }

            _connections.Remove(connection);
        }
    }

    private static EndPoint CloneEndPoint(EndPoint endPoint)
    {
        SocketAddress socketAddress = endPoint.Serialize();
        return endPoint.Create(socketAddress);
    }

    private static string CreatePeerKey(EndPoint endPoint)
    {
        SocketAddress socketAddress = endPoint.Serialize();
        byte[] bytes = new byte[socketAddress.Size];

        for (int index = 0; index < socketAddress.Size; index++)
        {
            bytes[index] = socketAddress[index];
        }

        return $"{endPoint.AddressFamily}:{Convert.ToHexString(bytes)}";
    }

    private static EndPoint CreateRemoteTemplate(AddressFamily family)
    {
        return family switch
        {
            AddressFamily.InterNetworkV6 => new IPEndPoint(IPAddress.IPv6Any, 0),
            AddressFamily.Unix => throw new NotSupportedException("UDP server transport does not currently support Unix domain datagram sockets."),
            _ => new IPEndPoint(IPAddress.Any, 0)
        };
    }

    private static async Task AwaitWithoutException(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
