using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Udp.Internal;

/// <summary>
/// A message-oriented UDP datagram connection over a bound (server) or connected (client) socket.
/// </summary>
/// <remarks>
/// Datagrams are sent and received directly on the socket, one message per call, preserving
/// message boundaries. The connection owns the socket and disposes it when disposed.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("UdpDatagramConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class UdpDatagramConnection : DatagramConnection
{
    private readonly Socket _socket;
    private readonly EndPoint _localEndPoint;
    private readonly EndPoint? _remoteEndPoint;
    private readonly EndPoint _receiveTemplate;

    private readonly ConnectionId _id = ConnectionId.New();
    private readonly CancellationTokenSource _closed = new();
    private int _state = (int)ConnectionState.Open;
    private int _disposed;

    /// <summary>
    /// Creates a new UDP datagram connection over the supplied socket.
    /// </summary>
    /// <param name="socket">A bound (server) or connected (client) UDP socket. The connection takes ownership.</param>
    /// <param name="localEndPoint">The local endpoint the socket is bound to.</param>
    /// <param name="remoteEndPoint">The connected peer for a client socket, or <see langword="null"/> for a server socket.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> or <paramref name="localEndPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when the socket's address family is not IPv4 or IPv6.</exception>
    public UdpDatagramConnection(Socket socket, EndPoint localEndPoint, EndPoint? remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(localEndPoint);

        _socket = socket;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
        _receiveTemplate = socket.AddressFamily switch
        {
            AddressFamily.InterNetwork => new IPEndPoint(IPAddress.Any, 0),
            AddressFamily.InterNetworkV6 => new IPEndPoint(IPAddress.IPv6Any, 0),
            _ => throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DatagramSocketAddressFamilyUnsupported, socket.AddressFamily))
        };

        UdpConnectionEventSource.Log.ConnectionOpened(_id, localEndPoint, remoteEndPoint);
    }

    /// <inheritdoc />
    public override ConnectionId Id => _id;

    /// <inheritdoc />
    public override ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _closed.Token;

    /// <inheritdoc />
    public override void Abort(Exception? reason = null) => Close(ConnectionState.Aborted);

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint => _localEndPoint;

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint => _remoteEndPoint;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Udp,
        ConnectionDelivery.Datagram,
        IsReliable: false,
        IsOrdered: false,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    /// <inheritdoc />
    public override async ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        SocketReceiveFromResult result = await _socket
            .ReceiveFromAsync(buffer, SocketFlags.None, _receiveTemplate, cancellationToken)
            .ConfigureAwait(false);

        return new DatagramReceiveResult(result.ReceivedBytes, result.RemoteEndPoint);
    }

    /// <inheritdoc />
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> payload, EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_remoteEndPoint is not null)
        {
            // A connected UDP socket (client role) must use send(), not sendto(): passing an
            // explicit destination to sendto() on a connected socket fails with EISCONN
            // ("Socket is already connected") on macOS/BSD, though Linux and Windows tolerate it.
            // A connected socket always targets its bound peer, so the destination is implicit.
            await _socket
                .SendAsync(payload, SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _socket
                .SendToAsync(payload, SocketFlags.None, remoteEndPoint, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        Close(ConnectionState.Closed);
        return ValueTask.CompletedTask;
    }

    private void Close(ConnectionState state)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _state, (int)state);
            _socket.Dispose();
            UdpConnectionEventSource.Log.ConnectionClosed(_id);
        }
        try
        {
            _closed.Cancel();
        }
        catch (AggregateException)
        {
            // Observer failures cannot prevent transport teardown.
        }
    }

    private sealed class DebugView
    {
        private readonly UdpDatagramConnection _target;

        public DebugView(UdpDatagramConnection value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public CancellationToken ConnectionClosed => _target.ConnectionClosed;
        public EndPoint? LocalEndPoint => _target.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public global::Assimalign.Cohesion.Connections.IConnectionFeatureCollection? InnerFeatures => _target.InnerFeatures;
    }
}
