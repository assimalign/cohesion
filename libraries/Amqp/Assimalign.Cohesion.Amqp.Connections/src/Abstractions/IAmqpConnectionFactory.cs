using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Establishes outbound AMQP connections over carrier connections.
/// </summary>
/// <remarks>
/// <see cref="IConnectionFactory{TConnection}.ConnectAsync(System.Net.EndPoint, System.Threading.CancellationToken)"/>
/// returns an open connection: the protocol headers, any SASL exchange and both <c>open</c> frames are
/// done. When establishment fails, the factory releases the carrier connection before the exception
/// reaches the caller.
/// </remarks>
public interface IAmqpConnectionFactory : IConnectionFactory<IAmqpConnection>
{
    /// <summary>
    /// Gets the container identifier every connection the factory opens sends in its <c>open</c>.
    /// </summary>
    string ContainerId { get; }
}
