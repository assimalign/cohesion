using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Contributes a named, transport-neutral health snapshot to a Cohesion host.
/// </summary>
public interface IHealthContributor
{
    /// <summary>
    /// Gets the stable name used to identify this contribution in an aggregate health report.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Evaluates the current health of the contributed component.
    /// </summary>
    /// <param name="cancellationToken">Signals that the health evaluation should be abandoned.</param>
    /// <returns>The component's current health contribution.</returns>
    ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default);
}
