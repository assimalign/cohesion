using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;

namespace Assimalign.Cohesion.Amqp.Connections.AotSample;

/// <summary>
/// One AMQP connection over TCP loopback: a listener on an ephemeral port that authenticates with SASL PLAIN, the
/// connection it accepted, and the factory connection that reached it.
/// </summary>
internal sealed class AmqpAotLoopback : IAsyncDisposable
{
    public const string BrokerContainerId = "aot-broker";
    public const string ClientContainerId = "aot-client";
    public const string HostName = "localhost";
    public const string UserName = "aot-user";
    public const string Password = "aot-password";

    // Small enough that a modest message is split across many frames.
    public const uint MaxFrameSize = 4096;

    private AmqpAotLoopback(AmqpConnectionListener listener, IAmqpConnection client, IAmqpConnection server)
    {
        Listener = listener;
        Client = client;
        Server = server;
    }

    /// <summary>
    /// Gets the listener, bound to an ephemeral loopback port.
    /// </summary>
    public AmqpConnectionListener Listener { get; }

    /// <summary>
    /// Gets the connection the factory opened.
    /// </summary>
    public IAmqpConnection Client { get; }

    /// <summary>
    /// Gets the connection the listener accepted.
    /// </summary>
    public IAmqpConnection Server { get; }

    /// <summary>
    /// Binds a listener and opens one connection to it.
    /// </summary>
    public static async Task<AmqpAotLoopback> StartAsync(CancellationToken cancellationToken)
    {
        AmqpConnectionListenerOptions options = new()
        {
            ContainerId = BrokerContainerId,
            MaxFrameSize = MaxFrameSize
        };

        options.SaslMechanisms.Add(AmqpSaslPlainServerMechanism.Create(ValidateAsync));

        AmqpConnectionListener listener = AmqpConnectionListener.Create(
            TcpConnectionListener.Create(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0)),
            options);

        try
        {
            await listener.BindAsync(cancellationToken);

            Task<IAmqpConnection> accept = listener.AcceptAsync(cancellationToken).AsTask();
            IAmqpConnection client = await ConnectAsync(listener, Password, cancellationToken);
            IAmqpConnection server = await accept;

            return new AmqpAotLoopback(listener, client, server);
        }
        catch
        {
            await listener.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens a new connection to the listener with the given password.
    /// </summary>
    public static ValueTask<IAmqpConnection> ConnectAsync(AmqpConnectionListener listener, string password, CancellationToken cancellationToken)
    {
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(
            new TcpConnectionFactory(),
            new AmqpConnectionFactoryOptions
            {
                ContainerId = ClientContainerId,
                HostName = HostName,
                MaxFrameSize = MaxFrameSize,
                SaslMechanism = AmqpSaslPlainClientMechanism.Create(UserName, password)
            });

        return factory.ConnectAsync(listener.EndPoint, cancellationToken);
    }

    /// <summary>
    /// Begins a session from the client and accepts it on the server.
    /// </summary>
    public async Task<(IAmqpSession Client, IAmqpSession Server)> BeginSessionAsync(CancellationToken cancellationToken)
    {
        Task<IAmqpSession> accept = Server.AcceptSessionAsync(cancellationToken).AsTask();
        IAmqpSession client = await Client.BeginSessionAsync(cancellationToken: cancellationToken);
        IAmqpSession server = await accept;

        return (client, server);
    }

    /// <summary>
    /// Attaches a sender on the client session to the given address and accepts it as a receiver on the server
    /// session.
    /// </summary>
    public static async Task<(IAmqpSender Sender, IAmqpReceiver Receiver)> AttachAsync(
        IAmqpSession client,
        IAmqpSession server,
        string address,
        AmqpSenderSettleMode settleMode,
        CancellationToken cancellationToken)
    {
        Task<IAmqpReceiver> accept = AcceptReceiverAsync(server, cancellationToken);
        IAmqpSender sender = await client.AttachSenderAsync(
            new AmqpSenderOptions
            {
                Name = address + "-sender",
                Source = new AmqpSource { Address = ClientContainerId },
                Target = new AmqpTarget { Address = address },
                SenderSettleMode = settleMode
            },
            cancellationToken);
        IAmqpReceiver receiver = await accept;

        return (sender, receiver);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
    }

    private static async Task<IAmqpReceiver> AcceptReceiverAsync(IAmqpSession session, CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        return await request.AcceptReceiverAsync(cancellationToken: cancellationToken);
    }

    private static ValueTask<bool> ValidateAsync(AmqpSaslPlainCredentials credentials, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(
            string.Equals(credentials.UserName, UserName, StringComparison.Ordinal)
            && string.Equals(credentials.Password, Password, StringComparison.Ordinal));
    }
}
