using System.Collections.Generic;
using System.Net;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Accepts transport connections and projects them into protocol-specific HTTP connections.
/// </summary>
public interface IHttpConnectionListener : IConnectionListener<IHttpConnection>
{
    /// <summary>
    /// Gets the HTTP protocols served by this listener.
    /// </summary>
    HttpProtocol Protocols { get; }

    /// <summary>
    /// Gets the endpoints of the configured transport listeners. After binding completes, each
    /// value is the bound endpoint, including the assigned port when port zero was requested.
    /// </summary>
    IReadOnlyList<EndPoint> EndPoints { get; }
}
