using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.RateLimiting.Internal;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// Configures the rate-limiting middleware that
/// <c>builder.Services.AddRateLimiting(rateLimiting =&gt; ...)</c> registers and
/// <see cref="RateLimitingExtensions.UseRateLimiting(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <para>
/// Build the policies here, once: the global limiter, the named policies endpoints reference through
/// <see cref="RateLimitingMetadata"/>, the rejection status, and the <c>OnRejected</c> and
/// <c>OnDecision</c> hooks. The middleware reads them on every request, so <see cref="Build"/> hands it a
/// snapshot of <see cref="Options"/>: a change made afterwards, a later <c>AddPolicy</c> included,
/// reaches only a later <see cref="Build"/>, never the middleware already registered.
/// </para>
/// <para>
/// The snapshot copies references, not limiters. A <see cref="RateLimitingPolicy"/> keeps its one
/// limiter, and therefore its one set of permits, wherever it is used.
/// </para>
/// </remarks>
public sealed class RateLimitingBuilder
{
    /// <summary>
    /// Initializes a builder with the defaults: no global policy, no named policy, a <c>429</c>
    /// rejection status, and no hooks. Built as is, the middleware limits only endpoints whose
    /// <see cref="RateLimitingMetadata"/> carries an inline <see cref="RateLimitingPolicy"/>.
    /// </summary>
    public RateLimitingBuilder()
    {
    }

    /// <summary>
    /// Gets the middleware's options: the global policy, the named policies, the rejection status, and
    /// the <c>OnRejected</c> and <c>OnDecision</c> hooks.
    /// </summary>
    public RateLimitingOptions Options { get; } = new();

    /// <summary>
    /// Builds the rate-limiting middleware from a snapshot of <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which acquires the global policy's lease for every request and then the policy of
    /// the endpoint <c>UseRouting</c> published, and answers a rejection with the configured status and a
    /// <c>Retry-After</c> header from the lease metadata.
    /// </returns>
    public IWebApplicationMiddleware Build()
    {
        return new RateLimitingMiddleware(Options.Snapshot());
    }
}
