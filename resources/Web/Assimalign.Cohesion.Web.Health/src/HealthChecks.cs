using Assimalign.Cohesion.Web.Health.Internal;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// Entry point for composing health checks without a dependency-injection container.
/// </summary>
/// <remarks>
/// This factory is the container-free composition seam for applications, resources, tests, and
/// tooling. Register checks or <see cref="Assimalign.Cohesion.Hosting.Health.IHealthContributor"/>
/// instances on the returned builder, build an <see cref="IHealthCheckService"/>, and supply it
/// explicitly when mapping health endpoints.
/// </remarks>
public static class HealthChecks
{
    /// <summary>
    /// Creates a new, empty <see cref="IHealthChecksBuilder"/>.
    /// </summary>
    /// <returns>A builder ready to accept registrations.</returns>
    public static IHealthChecksBuilder CreateBuilder() => new HealthChecksBuilder();
}
