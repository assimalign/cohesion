using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// A single health check. Implementations probe one dependency or invariant and return a
/// <see cref="HealthCheckResult"/> describing its condition.
/// </summary>
/// <remarks>
/// A check should be cheap, side-effect free, and honor <paramref name="cancellationToken"/>
/// so the host can bound how long a probe cycle runs. A check that throws is treated as a
/// failure and reported with the registration's
/// <see cref="HealthCheckRegistration.FailureStatus"/> — implementations do not need to catch
/// their own exceptions.
/// </remarks>
public interface IHealthCheck
{
    /// <summary>
    /// Runs the check.
    /// </summary>
    /// <param name="context">The context carrying the check's <see cref="HealthCheckRegistration"/>.</param>
    /// <param name="cancellationToken">Signals that the probe cycle is being abandoned (host shutdown, per-check timeout).</param>
    /// <returns>A <see cref="HealthCheckResult"/> describing the outcome.</returns>
    ValueTask<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken = default);
}
