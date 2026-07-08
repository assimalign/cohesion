using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Health;

/// <summary>
/// A sink that receives the periodic <see cref="HealthReport"/> produced by the health-check
/// publisher.
/// </summary>
/// <remarks>
/// <para>
/// Publishers are how health flows <em>out</em> of a process. The canonical consumer is the
/// orchestration plane: a publisher maps the aggregate <see cref="HealthReport.Status"/> onto
/// the ApplicationModel <c>ResourceLifecycle</c> (<see cref="HealthStatus.Healthy"/> →
/// <c>Running</c>, <see cref="HealthStatus.Degraded"/> → <c>Degraded</c>,
/// <see cref="HealthStatus.Unhealthy"/> → <c>Degraded</c>/failed) so the control plane learns
/// a resource is unwell without the health library taking a reverse dependency on it. Other
/// publishers might emit metrics or write a log line.
/// </para>
/// <para>
/// The periodic driver lives in <c>Assimalign.Cohesion.Health.Hosting</c> as an
/// <c>IHostService</c>. A publisher that throws is isolated: its failure is swallowed so it
/// neither kills the publish loop nor blocks sibling publishers.
/// </para>
/// </remarks>
public interface IHealthPublisher
{
    /// <summary>
    /// Publishes the latest health report.
    /// </summary>
    /// <param name="report">The aggregate report produced by the current probe cycle.</param>
    /// <param name="cancellationToken">Signaled when the host is shutting down.</param>
    /// <returns>A task that completes when the report has been published.</returns>
    ValueTask PublishAsync(HealthReport report, CancellationToken cancellationToken = default);
}
