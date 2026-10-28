using System;
using System.Linq;

namespace Assimalign.Cohesion.Web.HostFiltering;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.HostFiltering.Internal;

/// <summary>
/// Pipeline-builder extension that places allowed-hosts enforcement in the Web application middleware
/// pipeline.
/// </summary>
public static class HostFilteringExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the allowed-hosts middleware the application registered with
        /// <c>builder.Services.AddHostFiltering(hosts =&gt; ...)</c>. It validates each request's effective
        /// host against the registered allowlist, rejecting a mismatching — or, by default, empty — host
        /// with <c>400 Bad Request</c> and short-circuiting the pipeline.
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The allowlist and the empty-host policy are configured where the middleware is registered
        /// (<see cref="HostFilteringBuilder"/>); this verb only positions it. There is no default
        /// allowlist, so composing the pipeline fails with an <see cref="InvalidOperationException"/>
        /// when the application registered none. When it registered more than one, the last registration
        /// wins.
        /// </para>
        /// <para>
        /// Place this middleware at the front: a host that fails validation should be rejected before
        /// any other middleware does work for the request. The order of the <c>Use</c> verbs is the
        /// pipeline order. Ahead of it belong <c>UseForwardedHeaders</c>, when the application runs behind a trusted
        /// proxy, and the two middleware that read no client identity on the way in,
        /// <c>UseHttpLogging</c> and <c>UseSecurityHeaders</c>.
        /// </para>
        /// <para>
        /// The host validated is the <em>effective</em> host
        /// (<see cref="HttpContextForwardedExtensions.EffectiveHost"/>). Placed after
        /// <c>UseForwardedHeaders</c>, that is the host a trusted proxy forwarded — the host the
        /// client addressed and every downstream consumer reads — so allowlist the public names.
        /// Placed before it (or without it), it is the transport-resolved host. This guard never reads
        /// forwarding headers itself; which hops are believed is the forwarded-headers trust model's
        /// decision.
        /// </para>
        /// <para>
        /// Placing the middleware is what opts in: to accept every host, either do not use host
        /// filtering at all, or register the <c>*</c> pattern, which accepts any non-empty host while
        /// keeping the RFC 9112 §3.2 empty-host policy enforced.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseHostFiltering()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<HostFilteringMiddleware>().LastOrDefault()
                    ?? throw new InvalidOperationException(ErrorMessages.HostFilteringNotRegistered);

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
