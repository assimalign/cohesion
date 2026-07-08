using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Health;

/// <summary>
/// Runs the registered health checks and aggregates their results into a
/// <see cref="HealthReport"/>.
/// </summary>
/// <remarks>
/// The service is immutable — its check set is fixed when it is built from an
/// <see cref="IHealthChecksBuilder"/>. It is safe to invoke concurrently. Callers select a
/// subset (for example a readiness or liveness slice) with the <paramref name="predicate"/>.
/// </remarks>
public interface IHealthCheckService
{
    /// <summary>
    /// Runs the registered checks that match <paramref name="predicate"/> and returns the
    /// aggregate report.
    /// </summary>
    /// <param name="predicate">
    /// An optional filter over the registrations. When <see langword="null"/>, every registered
    /// check runs. A predicate that matches no checks yields an empty, <see cref="HealthStatus.Healthy"/> report.
    /// </param>
    /// <param name="cancellationToken">Abandons the evaluation (host shutdown, request abort).</param>
    /// <returns>The aggregate <see cref="HealthReport"/>.</returns>
    ValueTask<HealthReport> CheckHealthAsync(
        Func<HealthCheckRegistration, bool>? predicate = null,
        CancellationToken cancellationToken = default);
}
