using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IAmqpConnection"/>, sharing the
/// <see cref="DuplexConnection"/> lifecycle and lazy feature plumbing.
/// </summary>
/// <remarks>
/// <see cref="AmqpConnectionListener"/> and <see cref="AmqpConnectionFactory"/> produce connections of
/// their own. Derive from this class for another source of AMQP connections, such as a test double or a
/// carrier those types do not cover.
/// </remarks>
public abstract class AmqpConnection : DuplexConnection, IAmqpConnection
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
