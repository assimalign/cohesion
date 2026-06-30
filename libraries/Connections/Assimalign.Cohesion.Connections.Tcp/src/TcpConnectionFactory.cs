using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp;

/// <summary>
/// Establishes outbound, reliable, ordered single-stream TCP connections to a remote endpoint.
/// </summary>
/// <remarks>
/// Each call to <see cref="ConnectionFactory.ConnectAsync(EndPoint, CancellationToken)"/> connects a
/// fresh socket and returns a live <see cref="TcpConnection"/> whose IO loops are already running.
/// </remarks>
public sealed class TcpConnectionFactory : ConnectionFactory
{
    private readonly TcpConnectionFactoryOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpConnectionFactory"/> class with default options.
    /// </summary>
    public TcpConnectionFactory()
        : this(TcpConnectionFactoryOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpConnectionFactory"/> class.
    /// </summary>
    /// <param name="options">The socket-tuning options for outbound connections.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public TcpConnectionFactory(TcpConnectionFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Tcp,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    /// <inheritdoc />
    public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        Socket socket = endPoint switch
        {
            UnixDomainSocketEndPoint => new Socket(
                endPoint.AddressFamily,
                SocketType.Stream,
                ProtocolType.Unspecified),
            /*
                We're passing "ownsHandle: true" here even though we don't necessarily
                own the handle because Socket.Dispose will clean-up everything safely.
                If the handle was already closed or disposed then the socket will
                be torn down gracefully, and if the caller never cleans up their handle
                then we'll do it for them.
            */
            FileHandleEndPoint fileHandle => new Socket(new SafeSocketHandle((IntPtr)fileHandle.FileHandle, ownsHandle: true)),
            _ => new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        };

        if (endPoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
        {
            socket.DualMode = true;
        }

        SocketPipeOptionsContext pipeOptions = SocketPipeOptionsFactory.CreateSocketPipeOptions(
            _options.MaxReadBufferSize,
            _options.MaxWriteBufferSize,
            _options.UnsafePreferInLineScheduling);

        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken);

            if (socket.LocalEndPoint is IPEndPoint)
            {
                socket.NoDelay = _options.NoDelay;
            }

            TcpConnectionSettings settings = new()
            {
                PipeOptions = pipeOptions,
                WaitForDataBeforeAllocatingBuffer = _options.WaitForDataBeforeAllocatingBuffer
            };

            // The pipe options context is created per connection here, so its lifetime is bound
            // to the connection as an owned resource.
            // Factory-dialed connections carry no listener id; client connections are identified
            // by their ConnectionId and remote endpoint.
            return new TcpConnection(socket, settings, default, pipeOptions);
        }
        catch
        {
            pipeOptions.Dispose();
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a new <see cref="TcpConnectionFactory"/> configured by the supplied delegate.
    /// </summary>
    /// <param name="configure">A delegate used to configure the factory options.</param>
    /// <returns>A new <see cref="TcpConnectionFactory"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static TcpConnectionFactory Create(Action<TcpConnectionFactoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        TcpConnectionFactoryOptions options = new();
        configure.Invoke(options);

        return new TcpConnectionFactory(options);
    }
}
