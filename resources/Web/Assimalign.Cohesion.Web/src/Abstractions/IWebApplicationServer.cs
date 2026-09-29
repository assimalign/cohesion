using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Serves a web application's request pipeline over one or more listener endpoints.
/// </summary>
public interface IWebApplicationServer
{
    /// <summary>
    /// Binds the server's listener endpoints and starts accepting connections.
    /// </summary>
    /// <remarks>
    /// The returned task completes only after every endpoint is bound. A startup failure is
    /// propagated to the application host rather than deferred to an accept loop.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token for server startup.</param>
    /// <returns>A task that represents server startup.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops accepting connections, drains active work, and releases the listener endpoints.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token that bounds graceful draining.</param>
    /// <returns>A task that completes after the listener endpoints have been released.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
