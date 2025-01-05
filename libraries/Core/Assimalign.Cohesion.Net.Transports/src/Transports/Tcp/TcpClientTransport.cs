using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Buffers;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;


public sealed class TcpClientTransport : ClientTransport
{
    private readonly TcpClientTransportOptions options;
    private readonly SocketTransportConnectionSettings settings;
    private Socket? socket;
    private bool isDisposed;

    private readonly List<ITransportConnection> connections = new();

    public TcpClientTransport(TcpClientTransportOptions? options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        this.options = options;
        this.settings = SocketTransportConnectionSettings.GetIOQueueSettings(
            1,
            options.UnsafePreferInLineScheduling,
            options.WaitForDataBeforeAllocatingBuffer,
            options.MaxReadBufferSize,
            options.MaxWriteBufferSize,
            options.OnTrace)[0];

        this.Middleware = options.Middleware;
    }
    public override ProtocolType ProtocolType => ProtocolType.Tcp;
    public override TransportMiddlewareHandler Middleware { get; }
    public override async Task<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (socket is null)
        {
            socket = options.EndPoint switch
            {
                UnixDomainSocketEndPoint        => new Socket(options.EndPoint.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified),
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
                _                               => new Socket(options.EndPoint.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
            };
            if (options.EndPoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
            {
                socket.DualMode = true;
            }
            if (socket.LocalEndPoint is IPEndPoint)
            {
                socket.NoDelay = options.NoDelay;
            }
        }
        if (!socket.Connected)
        {
            await socket.ConnectAsync(options.EndPoint);

            settings.Socket = socket;
        }
        while (true)
        {
            try
            {
                var connection = new SocketTransportConnection(settings);
                var started = !ThreadPool.UnsafeQueueUserWorkItem(connection, true);

                connections.Add(connection);

                connection.OnDispose = () =>
                {
                    connections.Remove(connection);
                };

                await Middleware.Invoke(new TcpClientTransportContext(connection));

                return connection;
            }
            catch (Exception)
            {
                socket.Dispose();
                isDisposed = true;
            }
        }
    }

    public override void Dispose()
    {
        if (socket is not null && socket.Connected)
        {
            socket.Close();
        }
        if (socket is not null && !isDisposed)
        {
            socket.Dispose();
        }
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static TcpClientTransport Create(Action<TcpClientTransportOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new TcpClientTransportOptions();

        configure.Invoke(options);

        return new TcpClientTransport(options);
    }
}