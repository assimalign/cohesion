using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HostFiltering.Internal;

namespace Assimalign.Cohesion.Web.HostFiltering;

/// <summary>
/// Configures the allowed-hosts middleware that
/// <c>builder.Services.AddHostFiltering(hosts =&gt; ...)</c> registers and
/// <see cref="HostFilteringExtensions.UseHostFiltering(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// The allowlist has no default: <see cref="HostFilteringOptions.AllowedHosts"/> must name at least one
/// host pattern (<c>*</c> accepts every non-empty host). <see cref="Build"/> compiles the allowlist into an
/// <see cref="HttpHostMatcher"/> exactly once, so an empty allowlist or an invalid pattern fails where the
/// application registers host filtering, never per request, and changing the options afterwards has no
/// effect on the middleware already built.
/// </remarks>
public sealed class HostFilteringBuilder
{
    /// <summary>
    /// Initializes a builder whose allowlist is empty, which <see cref="Build"/> refuses until the
    /// application names the hosts it serves, and whose empty-host policy rejects a hostless request.
    /// </summary>
    public HostFilteringBuilder()
    {
    }

    /// <summary>
    /// Gets the filtering options: the allowlist of host patterns and the empty-host policy.
    /// </summary>
    public HostFilteringOptions Options { get; } = new();

    /// <summary>
    /// Builds the allowed-hosts middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which answers a request whose effective host misses the allowlist, or is empty
    /// unless <see cref="HostFilteringOptions.AllowEmptyHost"/> is set, with <c>400 Bad Request</c> and
    /// short-circuits the pipeline.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The allowlist is empty (it would reject every request) or contains an invalid pattern (an empty,
    /// port-bearing, malformed, or wildcard-misusing entry).
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        HttpHostMatcher matcher = HttpHostMatcher.Create(Options.AllowedHosts);

        return new HostFilteringMiddleware(matcher, Options.AllowEmptyHost);
    }
}
