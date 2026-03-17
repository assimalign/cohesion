using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

[DebuggerDisplay("{Protocol} [{Kind}] - {_connections.Count}")]
public sealed class TcpServerTransport : ServerTransport<TcpTransportConnection>
{
    private readonly TcpServerTransportOptions _options;
    private readonly SocketTransportConnectionSettings[] _settings;
    private TransportPipeline _pipeline;
    private readonly int _count;
    private long _index; // long to prevent overflow
    private Socket? _socket;

    private readonly List<TcpTransportConnection> _connections;

    public TcpServerTransport() : this(TcpServerTransportOptions.Default)
    {
    }

    public TcpServerTransport(TcpServerTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _settings = options.CreateConnectionSettings();
        _count = _settings.Length;
        _connections = new List<TcpTransportConnection>();
        _pipeline = options.BuildPipeline();
    }

    /// <summary>
    /// 
    /// </summary>
    public override TransportProtocol Protocol { get; } = TransportProtocol.Tcp;

    /// <summary>
    /// The number of connections that are open.
    /// </summary>
    public IReadOnlyCollection<TcpTransportConnection> Connections => _connections.AsReadOnly();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<TcpTransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null)
        {
            _socket = _options.EndPoint switch
            {
                UnixDomainSocketEndPoint => new Socket(
                    _options.EndPoint.AddressFamily,
                    SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified),
                /* 
                    We're passing "ownsHandle: true" here even though we don't necessarily
                    own the handle because Socket.Dispose will clean-up everything safely.
                    If the handle was already closed or disposed then the socket will
                    be torn down gracefully, and if the caller never cleans up their handle
                    then we'll do it for them.

                    If we don't do this then we run the risk of Kestrel hanging because the
                    the underlying socket is never closed and the transport manager can hang
                    when it attempts to stop.
                */
                FileHandleEndPoint fileHandle => new Socket(new SafeSocketHandle((IntPtr)fileHandle.FileHandle, ownsHandle: true)),

                _ => new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
            };
            if (_options.EndPoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
            {
                _socket.DualMode = true;
            }
            _socket.Bind(_options.EndPoint);
            _socket.Listen(_options.Backlog);

            TransportEventSource.Log.TransportInitialized(Protocol, Id);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var socket = await _socket.AcceptAsync(cancellationToken);
                var settings = _settings[Interlocked.Increment(ref _index) % _count];

                if (socket.LocalEndPoint is IPEndPoint)
                {
                    socket.NoDelay = _options.NoDelay;
                }

                settings.Socket = socket;

                var context = new SocketTransportConnectionContext(settings)
                {
                    TransportId = Id,
                };

                var connection = new TcpTransportConnection(
                    context,
                    _pipeline);

                context.OnOpen = () =>
                {
                    _connections.Add(connection);
                };

                context.OnDispose = () =>
                {
                    _connections.Remove(connection);
                };

                return connection;
            }
            catch (ObjectDisposedException)
            {
                // return null;
                continue;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.OperationAborted)
            {
                // return null;
                continue; // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
            }
            catch (SocketException)
            {
                throw;
            }
            catch (Exception)
            {
                throw;
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>
    /// 
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        _socket?.Close();
        _socket?.Dispose();

        foreach (TcpTransportConnection connection in _connections.ToArray())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();

        foreach (SocketTransportConnectionSettings settings in _settings)
        {
            settings.PipeOptions.Dispose();
        }

    }

    /// <summary>
    /// Creates a new server transport.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static TcpServerTransport Create(Action<TcpServerTransportOptions> configure)
    {
        TcpServerTransportOptions options = new TcpServerTransportOptions();

        ArgumentNullException.ThrowIfNull(configure);

        configure.Invoke(options);

        return new TcpServerTransport(options);
    }
}
