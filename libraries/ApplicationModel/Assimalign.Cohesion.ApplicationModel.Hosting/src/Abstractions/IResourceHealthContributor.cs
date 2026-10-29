using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// Contributes a named, transport-neutral health result to a resource instance.
/// </summary>
public interface IResourceHealthContributor
{
    /// <summary>
    /// Gets the stable name that identifies this contribution in a health report.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Evaluates the current health of the contributed component.
    /// </summary>
    /// <param name="cancellationToken">Signals that the evaluation should be abandoned.</param>
    /// <returns>The component's current health.</returns>
    ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default);
}
