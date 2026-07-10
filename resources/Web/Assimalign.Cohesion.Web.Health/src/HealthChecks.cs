using Assimalign.Cohesion.Web.Health.Internal;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// Entry point for composing health checks without a dependency-injection container.
/// </summary>
/// <remarks>
/// Hosted applications normally compose checks through the <c>AddHealthChecks</c> DI extension
/// in <c>Assimalign.Cohesion.Web.Health.Hosting</c>. This factory is the container-free seam for
/// tests, tooling, and lightweight hosts that want an <see cref="IHealthCheckService"/> without
/// pulling in the hosting stack.
/// </remarks>
public static class HealthChecks
{
    /// <summary>
    /// Creates a new, empty <see cref="IHealthChecksBuilder"/>.
    /// </summary>
    /// <returns>A builder ready to accept registrations.</returns>
    public static IHealthChecksBuilder CreateBuilder() => new HealthChecksBuilder();
}
