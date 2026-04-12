using System.Collections.Generic;
using System.Net;

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
    /// 
    /// </summary>
    IDictionary<string, object?> Items { get; }
}