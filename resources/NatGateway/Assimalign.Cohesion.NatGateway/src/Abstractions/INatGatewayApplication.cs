using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.NatGateway;

/// <summary>
/// Represents a NAT gateway application and its host lifecycle.
/// </summary>
public interface INatGatewayApplication : IHost
{
    /// <summary>
    /// Runs the application until shutdown is requested.
    /// </summary>
    /// <param name="cancellationToken">The token that requests application shutdown.</param>
    /// <returns>A task that completes after the application has stopped.</returns>
    /// <exception cref="System.ObjectDisposedException">The application has been disposed.</exception>
    Task RunAsync(CancellationToken cancellationToken = default);
}
