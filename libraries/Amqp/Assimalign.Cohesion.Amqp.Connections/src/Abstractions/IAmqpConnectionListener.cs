using System.Net;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Accepts AMQP connections at one local endpoint, establishing each one over a carrier connection
/// before handing it out.
/// </summary>
/// <remarks>
/// <see cref="IConnectionListener{TConnection}.AcceptAsync(System.Threading.CancellationToken)"/> returns
/// an open connection: the protocol headers, any SASL exchange and both <c>open</c> frames are done. A
/// failure that belongs to one inbound connection, such as a peer that never sends its protocol header,
/// ends that connection only; an exception from accept means the listener itself can produce no more
/// connections.
/// </remarks>
public interface IAmqpConnectionListener : IConnectionListener<IAmqpConnection>
{
    /// <summary>
    /// Gets the carrier listener's configured local endpoint, or its actual bound endpoint after binding completes.
    /// </summary>
    EndPoint EndPoint { get; }

    /// <summary>
    /// Gets the capabilities of the connections this listener produces.
    /// </summary>
    ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the container identifier every connection the listener accepts sends in its <c>open</c>.
    /// </summary>
    string ContainerId { get; }
}
