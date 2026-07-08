namespace Assimalign.Cohesion.Web.Health;

using Assimalign.Cohesion.Health;
using Assimalign.Cohesion.Http;

/// <summary>
/// The per-request feature the health middleware attaches to
/// <see cref="IHttpContext.Features"/> once it has evaluated the checks for a health endpoint.
/// Lets other middleware observe the produced <see cref="Report"/> without re-running the checks.
/// </summary>
public interface IHttpHealthFeature : IHttpFeature
{
    /// <summary>
    /// Gets the health report produced for the current request.
    /// </summary>
    HealthReport Report { get; }
}
