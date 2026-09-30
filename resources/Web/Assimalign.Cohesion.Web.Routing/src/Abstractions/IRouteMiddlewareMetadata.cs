namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Endpoint metadata that takes effect only when a specific middleware processes it, and that
/// routing therefore refuses to leave unprocessed.
/// </summary>
/// <remarks>
/// <para>
/// Endpoint policies are applied by middleware that run between <c>UseRouting</c> and the endpoint:
/// a rate limit by <c>UseRateLimiting</c>, a request timeout by <c>UseRequestTimeouts</c>. A policy
/// whose middleware is missing, or registered ahead of <c>UseRouting</c> where no endpoint is known
/// yet, would silently not apply. Metadata whose silent absence weakens a safety property implements
/// this interface to name the middleware that honors it, and that middleware acknowledges the
/// endpoint with <see cref="HttpContextRoutingExtensions"/>' <c>AcknowledgeEndpointMiddleware</c>.
/// When the endpoint is dispatched, every metadata item that names a middleware its request never
/// acknowledged fails the request with an <see cref="System.InvalidOperationException"/> naming the
/// endpoint and the middleware, instead of running the endpoint without its policy.
/// </para>
/// <para>
/// Metadata that only tunes optional behavior (output caching, access logging) does not implement
/// this interface: running the endpoint without it loses nothing a caller relies on.
/// </para>
/// </remarks>
public interface IRouteMiddlewareMetadata
{
    /// <summary>
    /// Gets the pipeline verb of the middleware that must process this metadata before the endpoint
    /// runs (for example <c>UseRateLimiting</c>), or <see langword="null"/> when this instance places
    /// no requirement (for example, metadata that disables the policy).
    /// </summary>
    string? RequiredMiddleware { get; }
}
