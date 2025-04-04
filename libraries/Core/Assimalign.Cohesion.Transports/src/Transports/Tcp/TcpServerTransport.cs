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

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports.Internal;

[DebuggerDisplay("{Protocol} [{Kind}] - {_connections.Count}")]
public sealed class TcpServerTransport : ServerTransport<TcpTransportConnection>
{
    private readonly TcpServerTransportOptions _options;
    private readonly SocketTransportConnectionSettings[] _settings;
    private readonly TransportTrace? _trace;
    private readonly TransportMiddleware? _middleware;
    private readonly int _count;
    private long _index; // long to prevent overflow
    private Socket? _listener;

    private readonly List<TcpTransportConnection> _connections = new();

    public TcpServerTransport() : this(TcpServerTransportOptions.Default)
    {
    }

    public TcpServerTransport(TcpServerTransportOptions options)
    {
        _options = ThrowHelper.ThrowIfNull(options);
        _count = options.IOQueueCount > 0 ? options.IOQueueCount : 1;
        _trace = options.Trace;
        _settings = SocketTransportConnectionSettings.GetIOQueueSettings(
            _count,
            options.UnsafePreferInLineScheduling,
            options.WaitForDataBeforeAllocatingBuffer,
            options.MaxReadBufferSize,
            options.MaxWriteBufferSize,
            _trace);
        _middleware = options.Middleware;
    }

    public override ProtocolType Protocol => ProtocolType.Tcp;

    /// <summary>
    /// The number of connections that are open.
    /// </summary>
    public IReadOnlyCollection<TcpTransportConnection> Connections => _connections.AsReadOnly();
    
    public override async Task<TcpTransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null)
        {
            _listener = _options.EndPoint switch
            {
                UnixDomainSocketEndPoint        => new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified),
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
                FileHandleEndPoint fileHandle   => new Socket(new SafeSocketHandle((IntPtr)fileHandle.FileHandle, ownsHandle: true)),
                _                               => new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
            };
            if (_options.EndPoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
            {
                _listener.DualMode = true;
            }
            _listener.Bind(_options.EndPoint);
            _listener.Listen(_options.Backlog);
        }

        while (true)
        {
            try
            {
                var socket = await _listener.AcceptAsync(cancellationToken);
                var settings = this._settings[Interlocked.Increment(ref _index) % _count];

                if (socket.LocalEndPoint is IPEndPoint)
                {
                    socket.NoDelay = _options.NoDelay;
                }

                settings.Socket = socket;

                var socketConnection = new SocketTransportConnection(settings)
                {
                    Protocol = ProtocolType.Tcp
                };

                var connection = new TcpTransportConnection(socketConnection);

                if (!ThreadPool.UnsafeQueueUserWorkItem(connection, false))
                {
                    throw new Exception();
                }

                _connections.Add(connection);

                socketConnection.OnDispose = () =>
                {
                    _connections.Remove(connection);
                };

                var task = _middleware?.Invoke(new TcpTransportContext(socketConnection));

                if (task is not null)
                {
                    await task;
                }

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

            }
            catch (Exception)
            {

            }
        }
    }
    public override void Dispose()
    {
        _listener?.Close();
        _listener?.Dispose();
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static TcpServerTransportBuilder CreateBuilder(Action<TcpServerTransportOptions> configure)
    {
        ThrowHelper.ThrowIfNull(configure);

        return new TcpServerTransportBuilder(middleware =>
        {
            var options = new TcpServerTransportOptions();

            configure.Invoke(options);

            options.Middleware = middleware;

            return new TcpServerTransport(options);
        });
    }
}