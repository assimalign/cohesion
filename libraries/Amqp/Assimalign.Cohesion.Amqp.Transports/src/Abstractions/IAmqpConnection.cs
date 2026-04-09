using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an AMQP connection layered on top of a lower-level carrier transport connection.
/// </summary>
public interface IAmqpConnection : ITransportConnection
{
    /// <summary>
    /// Opens the AMQP connection context used to negotiate the protocol header and exchange frames.
    /// </summary>
    /// <returns>The opened AMQP connection context.</returns>
    IAmqpConnectionContext Open();

    /// <summary>
    /// Opens the AMQP connection context used to negotiate the protocol header and exchange frames.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the open operation.</param>
    /// <returns>The opened AMQP connection context.</returns>
    ValueTask<IAmqpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);
}
