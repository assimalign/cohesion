using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections.Tcp;

using NetLite = Amqp;
using NetLiteListener = Amqp.Listener;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An AMQPNetLite <c>ContainerHost</c> on a TCP loopback port, which Cohesion clients connect to. It offers SASL
/// PLAIN for one user, and ANONYMOUS; the test registers the message processors and sources its links attach to.
/// </summary>
internal sealed class AmqpNetLiteHost : IDisposable
{
    public const string ContainerId = "netlite-host";
    public const string ClientContainerId = "cohesion-client";
    public const string HostName = "interop.test";
    public const string UserName = "interop-user";
    public const string Password = "interop-password";

    // AMQPNetLite cannot bind port 0 and report the port it got, so the host takes a port the operating system
    // just handed out and retries when another process takes it first.
    private const int maxBindAttempts = 5;

    private AmqpNetLiteHost(NetLiteListener.ContainerHost host, int port)
    {
        Host = host;
        EndPoint = new IPEndPoint(IPAddress.Loopback, port);
    }

    /// <summary>
    /// Gets the peer's container host.
    /// </summary>
    public NetLiteListener.ContainerHost Host { get; }

    /// <summary>
    /// Gets the loopback end point the host listens on.
    /// </summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>
    /// Opens a host whose listener negotiates at most <paramref name="maxFrameSize"/> bytes per frame.
    /// </summary>
    public static AmqpNetLiteHost Start(int maxFrameSize = 64 * 1024)
    {
        for (int attempt = 1; ; attempt++)
        {
            int port = ReserveLoopbackPort();
            NetLiteListener.ContainerHost host = new(new NetLite.Address("127.0.0.1", port, null, null, "/", "amqp"));
            NetLiteListener.ConnectionListener listener = host.Listeners[0];

            listener.AMQP.ContainerId = ContainerId;
            listener.AMQP.MaxFrameSize = maxFrameSize;
            listener.SASL.EnablePlainMechanism(UserName, Password);
            listener.SASL.EnableAnonymousMechanism = true;

            try
            {
                host.Open();

                return new AmqpNetLiteHost(host, port);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < maxBindAttempts)
            {
                host.Close();
            }
        }
    }

    /// <summary>
    /// Opens a Cohesion connection to the host with the given SASL mechanism.
    /// </summary>
    public ValueTask<IAmqpConnection> ConnectAsync(
        IAmqpSaslClientMechanism? saslMechanism,
        CancellationToken cancellationToken,
        uint maxFrameSize = 64 * 1024)
    {
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(
            new TcpConnectionFactory(),
            new AmqpConnectionFactoryOptions
            {
                ContainerId = ClientContainerId,
                HostName = HostName,
                MaxFrameSize = maxFrameSize,
                SaslMechanism = saslMechanism
            });

        return factory.ConnectAsync(EndPoint, cancellationToken);
    }

    /// <summary>
    /// Opens a Cohesion connection that authenticates with SASL PLAIN as the host's user.
    /// </summary>
    public ValueTask<IAmqpConnection> ConnectAsync(CancellationToken cancellationToken, uint maxFrameSize = 64 * 1024)
    {
        return ConnectAsync(AmqpSaslPlainClientMechanism.Create(UserName, Password), cancellationToken, maxFrameSize);
    }

    public void Dispose()
    {
        Host.Close();
    }

    private static int ReserveLoopbackPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
