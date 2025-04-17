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
public sealed class TcpServerTransport : ServerTransport<TcpTransportConnection>, ITransportPipelineBuilder
{
    private readonly TcpServerTransportOptions _options;
    private readonly SocketTransportConnectionSettings[] _settings;
    private readonly TransportTrace? _trace;
    private TransportPipeline? _pipeline;
    private readonly int _count;
    private long _index; // long to prevent overflow
    private Socket? _socket;

    private readonly List<TcpTransportConnection> _connections;
    private readonly List<Func<TransportMiddleware, TransportMiddleware>> _middleware;

    #region Constructors

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
        _connections = new List<TcpTransportConnection>();
        _middleware = new List<Func<TransportMiddleware, TransportMiddleware>>();
    }

    #endregion

    #region Properties

    /// <summary>
    /// 
    /// </summary>
    public override ProtocolType Protocol => ProtocolType.Tcp;

    /// <summary>
    /// The number of connections that are open.
    /// </summary>
    public IReadOnlyCollection<TcpTransportConnection> Connections => _connections.AsReadOnly();

    #endregion

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<TcpTransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        if (_pipeline is null)
        {
            _pipeline = (TransportPipeline)(this as ITransportPipelineBuilder).Build();
        }
        if (_socket is null)
        {
            _socket = _options.EndPoint switch
            {
                UnixDomainSocketEndPoint => new Socket(_options.EndPoint.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified),
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
        }

        while (true)
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

                var task = _pipeline?.ExecuteAsync(
                    new TcpTransportContext(socketConnection),
                    cancellationToken);

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

    /// <summary>
    /// 
    /// </summary>
    public override void Dispose()
    {
        _socket?.Close();
        _socket?.Dispose();
    }


    public TcpServerTransport Use(Func<TcpTransportContext, TransportMiddleware, Task> middleware)
    {
        ThrowHelper.ThrowIfNull(middleware);

        Func<TcpTransportContext, TransportMiddleware, Task> middleware2 = middleware;

        (this as ITransportPipelineBuilder).Use((TransportMiddleware next) => (ITransportContext c) =>
        {
            if (c is TcpTransportContext context)
            {
                return middleware2.Invoke(context, next);
            }

            return Task.CompletedTask;
        });

        return this;
    }

    ITransportPipelineBuilder ITransportPipelineBuilder.Use(Func<TransportMiddleware, TransportMiddleware> middleware)
    {
        ThrowHelper.ThrowIfNull(middleware);

        _middleware.Add(middleware);

        return this;
    }

    ITransportPipeline ITransportPipelineBuilder.Build()
    {
        var middleware = new TransportMiddleware(context =>
        {
            return Task.CompletedTask;
        });

        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            middleware = _middleware[i].Invoke(middleware);
        }

        return new TransportPipeline(middleware);
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static TcpServerTransport Create(Action<TcpServerTransportOptions> configure)
    {
        var options = new TcpServerTransportOptions();

        ThrowHelper.ThrowIfNull(configure).Invoke(options);

        return new TcpServerTransport(options);
    }
}