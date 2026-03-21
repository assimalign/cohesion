using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

/// <summary>
/// Accepts transport connections and projects them into protocol-specific HTTP connections.
/// </summary>
public interface IHttpConnectionListener : ITransport
{
    /// <summary>
    /// Accepts the next available HTTP connection from the configured transports.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the accept operation.</param>
    /// <returns>The next accepted HTTP connection.</returns>
    Task<IHttpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default);
}
