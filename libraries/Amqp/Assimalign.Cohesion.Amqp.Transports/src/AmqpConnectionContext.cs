using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an opened AMQP connection context.
/// </summary>
public abstract class AmqpConnectionContext : IAmqpConnectionContext
{
    /// <inheritdoc />
    public abstract AmqpProtocolHeader LocalProtocolHeader { get; }

    /// <inheritdoc />
    public abstract AmqpProtocolHeader? RemoteProtocolHeader { get; }

    /// <inheritdoc />
    public abstract EndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public abstract EndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public abstract ITransportConnectionPipe Pipe { get; }

    /// <inheritdoc />
    public abstract IDictionary<string, object?> Items { get; }

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
