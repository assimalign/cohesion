using System;
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
public sealed class TcpClientTransport : ClientTransport<TcpTransportConnection>, ITransportPipelineBuilder
{
    private readonly TcpClientTransportOptions _options;
    private readonly SocketTransportConnectionSettings _settings;
    private readonly TransportTrace _trace;
    private TransportPipeline? _pipeline;
    private Socket? _socket;
    private bool _isDisposed;

    private readonly List<TcpTransportConnection> _connections;
    private readonly List<Func<TransportMiddleware, TransportMiddleware>> _middleware;

    public TcpClientTransport(TcpClientTransportOptions? options)
    {
        _options = ThrowHelper.ThrowIfNull(options);
        _settings = SocketTransportConnectionSettings.GetIOQueueSettings(
            1,
            options.UnsafePreferInLineScheduling,
            options.WaitForDataBeforeAllocatingBuffer,
            options.MaxReadBufferSize,
            options.MaxWriteBufferSize,
            options.Trace)[0];
        _trace = options.Trace;
        _connections = new List<TcpTransportConnection>();
        _middleware = new List<Func<TransportMiddleware, TransportMiddleware>>();
    }

    public override ProtocolType Protocol => ProtocolType.Tcp;

    /// <summary>
    /// The number of connections that are open.
    /// </summary>
    public IReadOnlyCollection<TcpTransportConnection> Connections => _connections.AsReadOnly();

    public override async Task<TcpTransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
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
            if (_socket.LocalEndPoint is IPEndPoint)
            {
                _socket.NoDelay = _options.NoDelay;
            }
        }
        if (!_socket.Connected)
        {
            await _socket.ConnectAsync(_options.EndPoint);

            _settings.Socket = _socket;
        }
        while (true)
        {
            try
            {
                var socketConnection = new SocketTransportConnection(_settings)
                {
                    Protocol = ProtocolType.Tcp
                };

                var connection = new TcpTransportConnection(socketConnection);

                var started = !ThreadPool.UnsafeQueueUserWorkItem(connection, true);

                _connections.Add(connection);

                socketConnection.OnDispose = () =>
                {
                    _connections.Remove(connection);
                };

                var task = _pipeline?.ExecuteAsync(new TcpTransportContext(socketConnection), cancellationToken);

                if (task is not null)
                {
                    await task;
                }

                return connection;
            }
            catch (Exception)
            {
                _socket.Dispose();
                _isDisposed = true;
            }
        }
    }

    public override void Dispose()
    {
        if (_socket is not null && _socket.Connected)
        {
            _socket.Close();
        }
        if (_socket is not null && !_isDisposed)
        {
            _socket.Dispose();
        }
    }

    public TcpClientTransport Use(Func<TcpTransportContext, TransportMiddleware, Task> middleware)
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
    public static TcpClientTransport Create(Action<TcpClientTransportOptions> configure)
    {
        var options = new TcpClientTransportOptions();

        ThrowHelper.ThrowIfNull(configure).Invoke(options);

        return new TcpClientTransport(options);
    }
}