// Ignore Spelling: awaiter
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;

public sealed class UdpServerTransport : ServerTransport
{
    private readonly UdpServerTransportOptions options;
    private readonly SocketTransportConnectionSettings[] settings;
    private readonly int count;
    private int index;
    private Socket? socket;
    private bool isDisposed;


    private readonly List<ITransportConnection> connections = new();

    public UdpServerTransport(UdpServerTransportOptions options)
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

    public IReadOnlyCollection<ITransportConnection> Connections => this.connections;
    public override ProtocolType ProtocolType => ProtocolType.Udp;
    public override TransportMiddlewareHandler Middleware { get; }
    public override async Task<ITransportConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        if (isDisposed)
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
            socket.Bind(options.Endpoint);
        }
        while (true)
        {
            try
            {
                var settings = this.settings[Interlocked.Increment(ref index) % count];
                var connection = new SocketTransportConnection(settings);

                connections.Add(connection);

                connection.OnDispose = () =>
                {
                    connections.Remove(connection);
                };

                var started = !ThreadPool.UnsafeQueueUserWorkItem(connection, true);

                await Middleware.Invoke(new UdpServerTransportContext(connection));

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
        if (isDisposed)
        {
            throw new ObjectDisposedException(nameof(UdpServerTransport));
        }
        if (socket is not null)
        {
            socket.Dispose();
            isDisposed = true;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static UdpServerTransport Create(Action<UdpServerTransportOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new UdpServerTransportOptions();

        configure.Invoke(options);

        return new UdpServerTransport(options);
    }
}
