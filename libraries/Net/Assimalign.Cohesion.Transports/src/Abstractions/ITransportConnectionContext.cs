using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Transports;

public interface ITransportConnectionContext
{
    /// <summary>
    /// The local endpoint the connection is bound to.
    /// </summary>
    EndPoint LocalEndPoint { get; }

    /// <summary>
    /// The remote endpoint the connection is bound to.
    /// </summary>
    EndPoint RemoteEndPoint { get; }

    /// <summary>
    /// A pipe to send and receive data from either client or server.
    /// </summary>
    ITransportConnectionPipe Pipe { get; }

    /// <summary>
    /// Gets a cancellation token that will be triggered when the connection is closed. This acts as a lifetime for the 
    /// connection and can be used to trigger cleanup of resources associated with the connection.
    /// </summary>
    CancellationToken ConnectionCancelled { get; }

    /// <summary>
    /// 
    /// </summary>
    IDictionary<string, object?> Items { get; }
}