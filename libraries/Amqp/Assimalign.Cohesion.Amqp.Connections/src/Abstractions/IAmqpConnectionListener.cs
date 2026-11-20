using System.Net;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Accepts AMQP connections at one local endpoint, establishing each one over a carrier connection
/// before handing it out.
/// </summary>
/// <remarks>
/// <see cref="IConnectionListener{TConnection}.AcceptAsync(System.Threading.CancellationToken)"/> returns
/// a connection that is ready to use. A failure that belongs to one inbound connection, such as a peer
/// that never sends its protocol header, ends that connection only; an exception from accept means the
/// listener itself can produce no more connections.
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
}
