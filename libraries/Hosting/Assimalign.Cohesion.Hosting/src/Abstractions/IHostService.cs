using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A managed host service.
/// </summary>
public interface IHostService
{
    /// <summary>
    /// A unique identifier for the host service.
    /// </summary>
    ServiceId Id { get; }

    /// <summary>
    /// Starts the host service
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the host service.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}