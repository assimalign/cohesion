using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;

public sealed class TcpServerTransport : ServerTransport
{
    private readonly TcpServerTransportOptions options;
    private readonly SocketTransportConnectionSettings[] settings;
    private readonly int count;
    private long index; // long to prevent overflow
    private Socket? listener;

    private readonly List<ITransportConnection> connections = new();

    public TcpServerTransport() : this(new()) { }

    public TcpServerTransport(TcpServerTransportOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        this.options = options;
        this.count = options.IOQueueCount > 0 ? options.IOQueueCount : 1;
        this.settings = SocketTransportConnectionSettings.GetIOQueueSettings(
            count,
            options.UnsafePreferInLineScheduling,
            options.WaitForDataBeforeAllocatingBuffer,
            options.MaxReadBufferSize,
            options.MaxWriteBufferSize,
            options.OnTrace);

        this.Middleware = options.Middleware;
    }

    public IReadOnlyCollection<ITransportConnection> Connections => this.connections.AsReadOnly();
    public override ProtocolType ProtocolType => ProtocolType.Tcp;
    public override TransportMiddlewareHandler Middleware { get; }
    public override async Task<ITransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        if (listener is null)
        {
            listener = options.EndPoint switch
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
                listener.DualMode = true;
            }
            listener.Bind(options.EndPoint);
            listener.Listen(options.Backlog);
        }
        while (true)
        {
            try
            {
                var socket = await listener.AcceptAsync(cancellationToken);
                var settings = this.settings[Interlocked.Increment(ref index) % count];

                if (socket.LocalEndPoint is IPEndPoint)
                {
                    socket.NoDelay = options.NoDelay;
                }

                settings.Socket = socket;

                var connection =  new SocketTransportConnection(settings);

                if (!ThreadPool.UnsafeQueueUserWorkItem(connection, false))
                {
                    throw new Exception();
                }

                connections.Add(connection);

                connection.OnDispose = () =>
                {
                    connections.Remove(connection);
                };

                await Middleware.Invoke(new TcpServerTransportContext(connection));

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
        listener?.Close();
        listener?.Dispose();
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static TcpServerTransport Create(Action<TcpServerTransportOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new TcpServerTransportOptions();

        configure.Invoke(options);

        return new TcpServerTransport(options);
    }
}