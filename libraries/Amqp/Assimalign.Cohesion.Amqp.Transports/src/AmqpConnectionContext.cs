using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an opened AMQP connection context layered over a carrier connection's duplex pipe.
/// </summary>
public abstract class AmqpConnectionContext : IAmqpConnectionContext
{
    /// <inheritdoc cref="IAmqpConnectionContext.LocalEndPoint" />
    public abstract EndPoint? LocalEndPoint { get; }

    /// <inheritdoc cref="IAmqpConnectionContext.RemoteEndPoint" />
    public abstract EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the pipe reader yielding the bytes received from the remote peer.
    /// </summary>
    public abstract PipeReader Input { get; }

    /// <summary>
    /// Gets the pipe writer accepting the bytes to send to the remote peer.
    /// </summary>
    public abstract PipeWriter Output { get; }

    /// <inheritdoc cref="IAmqpConnectionContext.LocalProtocolHeader" />
    public abstract AmqpProtocolHeader LocalProtocolHeader { get; }

    /// <inheritdoc cref="IAmqpConnectionContext.RemoteProtocolHeader" />
    public abstract AmqpProtocolHeader? RemoteProtocolHeader { get; }

    /// <inheritdoc cref="IAmqpConnectionContext.AsStream()" />
    public Stream AsStream()
    {
        return new DuplexPipeStream(this);
    }

    /// <inheritdoc cref="IAmqpConnectionContext.NegotiateAsync(CancellationToken)" />
    public abstract ValueTask<AmqpProtocolHeader> NegotiateAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IAmqpConnectionContext.SwitchProtocolAsync(AmqpProtocolHeader, CancellationToken)" />
    public abstract ValueTask<AmqpProtocolHeader> SwitchProtocolAsync(
        AmqpProtocolHeader protocolHeader,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IAmqpConnectionContext.ReceiveAsync(CancellationToken)" />
    public abstract IAsyncEnumerable<AmqpFrame> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IAmqpConnectionContext.SendAsync(AmqpFrame, CancellationToken)" />
    public abstract ValueTask SendAsync(AmqpFrame frame, CancellationToken cancellationToken = default);
}
