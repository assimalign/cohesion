using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Establishes outbound AMQP connections over carrier connections.
/// </summary>
/// <remarks>
/// <see cref="IConnectionFactory{TConnection}.ConnectAsync(System.Net.EndPoint, System.Threading.CancellationToken)"/>
/// returns a connection that is ready to use. When establishment fails, the factory releases the carrier
/// connection before the exception reaches the caller.
/// </remarks>
public interface IAmqpConnectionFactory : IConnectionFactory<IAmqpConnection>
{
}
