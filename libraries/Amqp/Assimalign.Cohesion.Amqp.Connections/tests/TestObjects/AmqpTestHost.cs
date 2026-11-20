using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Connects an <see cref="AmqpConnectionFactory"/> to an <see cref="AmqpConnectionListener"/> over one
/// in-memory carrier pair, so a test drives both ends of a real AMQP connection.
/// </summary>
internal sealed class AmqpTestHost : IAsyncDisposable
{
    public static readonly EndPoint EndPoint = new DnsEndPoint("amqp.test", 5672);

    private AmqpTestHost(
        AmqpConnectionListener listener,
        AmqpConnectionFactory factory,
        IAmqpConnection client,
        IAmqpConnection server,
        TestConnection clientCarrier,
        TestConnection serverCarrier)
    {
        Listener = listener;
        Factory = factory;
        Client = client;
        Server = server;
        ClientCarrier = clientCarrier;
        ServerCarrier = serverCarrier;
    }

    public AmqpConnectionListener Listener { get; }

    public AmqpConnectionFactory Factory { get; }

    /// <summary>
    /// Gets the connection the factory opened.
    /// </summary>
    public IAmqpConnection Client { get; }

    /// <summary>
    /// Gets the connection the listener accepted.
    /// </summary>
    public IAmqpConnection Server { get; }

    public TestConnection ClientCarrier { get; }

    public TestConnection ServerCarrier { get; }

    /// <summary>
    /// Opens one AMQP connection between a new factory and a new listener.
    /// </summary>
    public static async Task<AmqpTestHost> StartAsync(
        AmqpConnectionListenerOptions? listenerOptions = null,
        AmqpConnectionFactoryOptions? factoryOptions = null,
        CancellationToken cancellationToken = default)
    {
        (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair();
        TestConnectionListener carrierListener = new();
        TestConnectionFactory carrierFactory = new();

        carrierListener.Enqueue(serverCarrier);
        carrierFactory.Enqueue(clientCarrier);

        AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener, listenerOptions);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory, factoryOptions);

        Task<IAmqpConnection> accept = listener.AcceptAsync(cancellationToken).AsTask();
        IAmqpConnection client = await factory.ConnectAsync(EndPoint, cancellationToken);
        IAmqpConnection server = await accept;

        return new AmqpTestHost(listener, factory, client, server, clientCarrier, serverCarrier);
    }

    /// <summary>
    /// Begins a session from the client and accepts it on the server.
    /// </summary>
    public async Task<(IAmqpSession Client, IAmqpSession Server)> BeginSessionAsync(
        AmqpSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Task<IAmqpSession> accept = Server.AcceptSessionAsync(cancellationToken).AsTask();
        IAmqpSession client = await Client.BeginSessionAsync(options, cancellationToken);
        IAmqpSession server = await accept;

        return (client, server);
    }

    /// <summary>
    /// Attaches a sender on the client session and accepts it as a receiver on the server session.
    /// </summary>
    public static async Task<(IAmqpSender Sender, IAmqpReceiver Receiver)> AttachAsync(
        IAmqpSession senderSession,
        IAmqpSession receiverSession,
        AmqpSenderOptions? senderOptions = null,
        AmqpReceiverOptions? receiverOptions = null,
        CancellationToken cancellationToken = default)
    {
        senderOptions ??= new AmqpSenderOptions
        {
            Name = "test-link",
            Source = new AmqpSource { Address = "client" },
            Target = new AmqpTarget { Address = "queue" }
        };

        Task<IAmqpReceiver> accept = AcceptReceiverAsync(receiverSession, receiverOptions, cancellationToken);
        IAmqpSender sender = await senderSession.AttachSenderAsync(senderOptions, cancellationToken);
        IAmqpReceiver receiver = await accept;

        return (sender, receiver);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
    }

    private static async Task<IAmqpReceiver> AcceptReceiverAsync(
        IAmqpSession session,
        AmqpReceiverOptions? options,
        CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        return await request.AcceptReceiverAsync(options, cancellationToken);
    }
}
