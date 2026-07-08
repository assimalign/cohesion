using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Health;

using Assimalign.Cohesion.Health;
using Assimalign.Cohesion.Http;

/// <summary>
/// Serializes a <see cref="HealthReport"/> to the HTTP response body. The default writer emits
/// hand-written JSON via <see cref="System.Text.Json.Utf8JsonWriter"/>; supply a custom writer
/// through <see cref="HealthEndpointOptions.ResponseWriter"/> to change the format.
/// </summary>
/// <remarks>
/// The middleware sets the response status code before invoking the writer, so a writer is
/// responsible only for the response body and any content headers (such as <c>Content-Type</c>).
/// Implementations must be AOT-safe — no reflection-based serializers.
/// </remarks>
public interface IHealthResponseWriter
{
    /// <summary>
    /// Writes <paramref name="report"/> to the response body of <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The current exchange whose response the report is written to.</param>
    /// <param name="report">The health report to serialize.</param>
    /// <param name="cancellationToken">Signals that the exchange is being abandoned.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    ValueTask WriteAsync(IHttpContext context, HealthReport report, CancellationToken cancellationToken = default);
}
