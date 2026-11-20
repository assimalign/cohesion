using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections.Tcp;

using NetLite = Amqp;
using NetLiteFraming = Amqp.Framing;
using NetLiteSasl = Amqp.Sasl;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A Cohesion AMQP listener on an ephemeral TCP loopback port, which AMQPNetLite clients connect to. It offers
/// SASL PLAIN for one user, and ANONYMOUS.
/// </summary>
internal sealed class AmqpInteropBroker : IAsyncDisposable
{
    public const string ContainerId = "cohesion-broker";
    public const string PeerContainerId = "netlite-client";
    public const string HostName = "interop.test";
    public const string UserName = "interop-user";
    public const string Password = "interop-password";

    private AmqpInteropBroker(AmqpConnectionListener listener)
    {
        Listener = listener;
        Port = ((IPEndPoint)listener.EndPoint).Port;
    }

    /// <summary>
    /// Gets the listener, bound to an ephemeral loopback port.
    /// </summary>
    public AmqpConnectionListener Listener { get; }

    /// <summary>
    /// Gets the port the listener is bound to.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Binds a listener. <paramref name="configure"/> may change the options after the SASL mechanisms are added.
    /// </summary>
    public static async Task<AmqpInteropBroker> StartAsync(
        Action<AmqpConnectionListenerOptions>? configure,
        CancellationToken cancellationToken)
    {
        AmqpConnectionListenerOptions options = new()
        {
            ContainerId = ContainerId
        };

        options.SaslMechanisms.Add(AmqpSaslPlainServerMechanism.Create(ValidateAsync));
        options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        configure?.Invoke(options);

        AmqpConnectionListener listener = AmqpConnectionListener.Create(
            TcpConnectionListener.Create(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0)),
            options);

        try
        {
            await listener.BindAsync(cancellationToken);
        }
        catch
        {
            await listener.DisposeAsync();
            throw;
        }

        return new AmqpInteropBroker(listener);
    }

    /// <summary>
    /// Opens an AMQPNetLite connection to the listener and returns it with the <c>open</c> the listener answered
    /// with. A user name selects SASL PLAIN; without one the client uses SASL ANONYMOUS. <paramref name="events"/>,
    /// when given, also receives the <c>close</c> the listener answers with.
    /// </summary>
    public async Task<(NetLite.Connection Connection, NetLiteFraming.Open RemoteOpen)> ConnectPeerAsync(
        CancellationToken cancellationToken,
        string? userName = UserName,
        string? password = Password,
        Action<NetLite.ConnectionFactory>? configure = null,
        AmqpNetLiteConnectionEvents? events = null)
    {
        NetLite.ConnectionFactory factory = new();

        factory.AMQP.ContainerId = PeerContainerId;
        factory.AMQP.HostName = HostName;

        if (userName is null)
        {
            factory.SASL.Profile = NetLiteSasl.SaslProfile.Anonymous;
        }

        configure?.Invoke(factory);
        events ??= new AmqpNetLiteConnectionEvents();

        // AMQPNetLite does not pass the token to its SASL exchange, so the wait carries it too.
        NetLite.Address address = new("127.0.0.1", Port, userName, password, "/", "amqp");
        NetLite.Connection connection = await factory
            .CreateAsync(address, cancellationToken, events)
            .WaitAsync(cancellationToken);

        return (connection, await events.RemoteOpen.WaitAsync(cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        return Listener.DisposeAsync();
    }

    private static ValueTask<bool> ValidateAsync(AmqpSaslPlainCredentials credentials, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(
            string.Equals(credentials.UserName, UserName, StringComparison.Ordinal)
            && string.Equals(credentials.Password, Password, StringComparison.Ordinal));
    }
}
