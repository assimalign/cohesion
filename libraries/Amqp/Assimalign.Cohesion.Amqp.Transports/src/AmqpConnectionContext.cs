using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an opened AMQP connection context.
/// </summary>
public abstract class AmqpConnectionContext : TransportConnectionContext, IAmqpConnectionContext
{
    /// <inheritdoc />
    public abstract AmqpProtocolHeader LocalProtocolHeader { get; }

    /// <inheritdoc />
    public abstract AmqpProtocolHeader? RemoteProtocolHeader { get; }

    /// <inheritdoc />
    public abstract ValueTask<AmqpProtocolHeader> NegotiateAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask<AmqpProtocolHeader> SwitchProtocolAsync(
        AmqpProtocolHeader protocolHeader,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract IAsyncEnumerable<AmqpFrame> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask SendAsync(AmqpFrame frame, CancellationToken cancellationToken = default);
}
