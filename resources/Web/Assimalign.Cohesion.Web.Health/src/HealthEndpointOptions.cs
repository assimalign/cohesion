using System;

namespace Assimalign.Cohesion.Web.Health;

using Assimalign.Cohesion.Health;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Health.Internal;

/// <summary>
/// Configures a mapped health endpoint: which checks it runs, how it maps aggregate status to an
/// HTTP status code, whether the response may be cached, and how the report is serialized.
/// </summary>
public sealed class HealthEndpointOptions
{
    /// <summary>
    /// Gets or sets the predicate selecting which registered checks the endpoint runs. When
    /// <see langword="null"/> (the default), every registered check runs. The
    /// <c>MapReadinessCheck</c>/<c>MapLivenessCheck</c> helpers default this to the
    /// <see cref="HealthTags.Ready"/>/<see cref="HealthTags.Live"/> slice.
    /// </summary>
    public Func<HealthCheckRegistration, bool>? Predicate { get; set; }

    /// <summary>
    /// Gets or sets the writer that serializes the report to the response body. Defaults to the
    /// AOT-safe JSON writer (<c>application/health+json</c>).
    /// </summary>
    public IHealthResponseWriter ResponseWriter { get; set; } = HealthCheckJsonResponseWriter.Instance;

    /// <summary>
    /// Gets or sets the status code returned when the aggregate status is
    /// <see cref="HealthStatus.Healthy"/>. Defaults to <c>200 OK</c>.
    /// </summary>
    public HttpStatusCode HealthyStatusCode { get; set; } = HttpStatusCode.Ok;

    /// <summary>
    /// Gets or sets the status code returned when the aggregate status is
    /// <see cref="HealthStatus.Degraded"/>. Defaults to <c>200 OK</c> — a degraded instance is
    /// still serving.
    /// </summary>
    public HttpStatusCode DegradedStatusCode { get; set; } = HttpStatusCode.Ok;

    /// <summary>
    /// Gets or sets the status code returned when the aggregate status is
    /// <see cref="HealthStatus.Unhealthy"/>. Defaults to <c>503 Service Unavailable</c> — the
    /// convention Kubernetes probes gate on.
    /// </summary>
    public HttpStatusCode UnhealthyStatusCode { get; set; } = HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// Gets or sets a value indicating whether the endpoint may be cached. When
    /// <see langword="false"/> (the default), a <c>Cache-Control: no-store, no-cache</c> header is
    /// emitted so probes always observe fresh status.
    /// </summary>
    public bool AllowCachingResponses { get; set; }

    /// <summary>
    /// Maps an aggregate <see cref="HealthStatus"/> to the configured HTTP status code.
    /// </summary>
    /// <param name="status">The aggregate status.</param>
    /// <returns>The HTTP status code for that status.</returns>
    internal HttpStatusCode StatusCodeFor(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => HealthyStatusCode,
        HealthStatus.Degraded => DegradedStatusCode,
        _ => UnhealthyStatusCode,
    };
}
