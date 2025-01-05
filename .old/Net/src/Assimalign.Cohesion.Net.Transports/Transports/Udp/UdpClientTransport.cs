
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;

public sealed class UdpClientTransport : ClientTransport
{
    private readonly UdpClientTransportOptions options;
    private readonly SocketTransportConnectionSettings settings;
    private Socket? socket;
    private bool disposed;

    private readonly List<ITransportConnection> connections = new();

    public UdpClientTransport(UdpClientTransportOptions options)
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

    public override ProtocolType ProtocolType => ProtocolType.Udp;

    public override TransportMiddlewareHandler Middleware { get; }

    public override async Task<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(UdpServerTransport));
        }
        if (socket is null)
        {
            socket = options.Endpoint switch
            {
                UnixDomainSocketEndPoint    => new Socket(options.Endpoint.AddressFamily, SocketType.Dgram, System.Net.Sockets.ProtocolType.Unspecified),
                _                           => new Socket(options.Endpoint.AddressFamily, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp)
            };
            if (options.Endpoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
            {
                socket.DualMode = true;
            }
        }
        while (true)
        {
            try
            {
                var connection = new SocketTransportConnection(settings);

                connections.Add(connection);

                connection.OnDispose = () =>
                {
                    connections.Remove(connection);
                };

                var started = !ThreadPool.UnsafeQueueUserWorkItem(connection, true);

                await Middleware.Invoke(new UdpClientTransportContext(connection));

                return connection;
            }
            catch (Exception)
            {

            }
        }
    }

    public override void Dispose()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(UdpServerTransport));
        }
        if (socket is not null)
        {
            socket.Dispose();
            disposed = true;
        }
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static UdpClientTransport Create(Action<UdpClientTransportOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new UdpClientTransportOptions();

        configure.Invoke(options);

        return new UdpClientTransport(options);
    }
}
