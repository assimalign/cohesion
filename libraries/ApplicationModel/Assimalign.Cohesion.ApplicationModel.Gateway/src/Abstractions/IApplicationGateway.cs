using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Runs one gateway's application: deploys what its model declares onto an infrastructure provider,
/// joins its parent gateway when it has one, and manages its resources while it runs.
/// </summary>
/// <remarks>
/// An <see cref="IApplicationGatewayBuilder"/> builds one with its model, so a host can run it
/// without arguments.
/// </remarks>
public interface IApplicationGateway
{
    /// <summary>
    /// Gets the model the gateway runs.
    /// </summary>
    IApplicationGatewayContext Context { get; }


    Task StartAsync(CancellationToken cancellationToken = default);


    Task StopAsync(CancellationToken cancellationToken = default);
}
