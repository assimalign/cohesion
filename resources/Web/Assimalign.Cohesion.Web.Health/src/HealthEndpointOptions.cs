using System;

namespace Assimalign.Cohesion.Web.Health;

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
    /// AOT-safe JSON writer (<c>application/health+json</c>), which publishes each entry's status,
    /// duration, description, tags and data, and no exception text (see
    /// <see cref="IncludeDeveloperDetails"/>).
    /// </summary>
    /// <remarks>
    /// A custom writer receives the full <see cref="HealthReport"/>, including each entry's
    /// <see cref="HealthReportEntry.Exception"/>, and decides itself what to publish.
    /// </remarks>
    public IHealthResponseWriter ResponseWriter { get; set; } = HealthCheckJsonResponseWriter.Instance;

    /// <summary>
    /// Gets or sets a value indicating whether the default JSON writer publishes exception
    /// detail. Defaults to <see langword="false"/>; enable it only in a development environment,
    /// because health endpoints are typically unauthenticated and a dependency's exception message
    /// can carry host, database or user names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default, the JSON writer writes no exception text. A check that throws or times out is
    /// described by a fixed text (<c>The check threw an exception.</c> or
    /// <c>The check timed out.</c>), and a description the check returned that contains the message
    /// of its result's exception, or of an exception in that exception's
    /// <see cref="Exception.InnerException"/> chain, is replaced with
    /// <c>The check reported an exception.</c>
    /// </para>
    /// <para>
    /// When <see langword="true"/>, the writer exposes exactly two more things per entry: the
    /// description as the check returned it, and an <c>exception</c> member holding the
    /// <see cref="Exception.Message"/> of the entry's <see cref="HealthReportEntry.Exception"/> —
    /// the exception a check threw, the <see cref="OperationCanceledException"/> of a timed-out
    /// check, or the exception a check returned in its result. The exception's type, stack trace,
    /// inner exceptions and <see cref="Exception.Data"/> are never written.
    /// </para>
    /// <para>
    /// The setting applies only to the default writer. When <see cref="ResponseWriter"/> is a
    /// custom writer, it is ignored.
    /// </para>
    /// </remarks>
    public bool IncludeDeveloperDetails { get; set; }

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

    /// <summary>
    /// Resolves the writer the endpoint uses: the configured <see cref="ResponseWriter"/>, or the
    /// default JSON writer's developer-detail instance when <see cref="IncludeDeveloperDetails"/>
    /// is set and the default writer is configured.
    /// </summary>
    /// <returns>The writer that serializes the endpoint's report.</returns>
    internal IHealthResponseWriter ResolveResponseWriter()
        => IncludeDeveloperDetails && ResponseWriter is HealthCheckJsonResponseWriter
            ? HealthCheckJsonResponseWriter.DeveloperDetails
            : ResponseWriter;
}
