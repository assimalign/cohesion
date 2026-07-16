using System;

namespace Assimalign.Cohesion.Web.HostFiltering;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.HostFiltering.Internal;

/// <summary>
/// Pipeline-builder extensions that wire allowed-hosts enforcement into the Web application
/// middleware pipeline.
/// </summary>
public static class HostFilteringExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that validates each request's transport-resolved host against the
        /// configured allowlist, rejecting a mismatching — or, by default, empty — host with
        /// <c>400 Bad Request</c> and short-circuiting the pipeline.
        /// </summary>
        /// <param name="configure">Configures the allowlist and the empty-host policy.</param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// The configured allowlist is empty (it would reject every request) or contains an
        /// invalid pattern (a port-bearing, malformed, or wildcard-misusing entry).
        /// </exception>
        /// <remarks>
        /// <para>
        /// Register this middleware <em>first</em>: a host that fails validation should be
        /// rejected before any other middleware observes the request. Registration order is
        /// the pipeline order.
        /// </para>
        /// <para>
        /// The allowlist compiles into an <see cref="HttpHostMatcher"/> exactly once, inside
        /// this call — configuration errors throw here, at builder time, never at request
        /// time. Calling the method is what opts in: to accept every host, either do not
        /// register the middleware, or pass the <c>*</c> pattern, which accepts any
        /// non-empty host while keeping the RFC 9112 §3.2 empty-host policy enforced.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseHostFiltering(Action<HostFilteringOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            HostFilteringOptions options = new();
            configure.Invoke(options);

            HttpHostMatcher matcher = HttpHostMatcher.Create(options.AllowedHosts);

            return builder.Use(new HostFilteringMiddleware(matcher, options.AllowEmptyHost));
        }
    }
}
