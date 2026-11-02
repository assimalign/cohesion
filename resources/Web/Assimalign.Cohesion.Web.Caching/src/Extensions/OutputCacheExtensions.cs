using System;
using System.Linq;

using Assimalign.Cohesion.Web.Caching.Internal;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// Pipeline-builder members that place server-owned output caching in a web application.
/// </summary>
/// <remarks>
/// <para>
/// Place <c>UseOutputCache</c> after <c>UseRouting</c> — the middleware reads the endpoint
/// <c>UseRouting</c> published for its <see cref="OutputCacheMetadata"/> before deciding, then serves a
/// hit without running the endpoint — and ahead of <c>UseResponseCompression</c> and any
/// content-negotiated write, so the buffered response captures the fully-encoded bytes and the stored
/// <c>Vary</c> already carries <c>Accept-Encoding</c>/<c>Accept</c>.
/// </para>
/// <para>
/// Placed ahead of <c>UseRouting</c>, the middleware cannot see endpoint metadata when it decides:
/// only the <see cref="OutputCacheOptions.BasePolicy"/> applies, endpoint opt-ins have no effect, and a
/// response from an endpoint that carries <see cref="OutputCacheMetadata"/> is never stored, so an
/// endpoint's opt-out still holds.
/// </para>
/// <para>
/// The policies, size caps, clock, and store are configured where the middleware is registered,
/// <c>builder.Services.AddOutputCache(cache =&gt; ...)</c> (<see cref="OutputCacheBuilder"/>); this verb
/// only positions it. Composition is dependency-free and no request-time service location occurs.
/// </para>
/// </remarks>
public static class OutputCacheExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the output-cache middleware the application registered with
        /// <c>builder.Services.AddOutputCache(cache =&gt; ...)</c>, or one with the defaults when it
        /// registered none: no base policy, so only endpoints carrying <see cref="OutputCacheMetadata"/>
        /// are cached, over a new in-memory store.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Place it after <c>UseRouting</c>, so it reads the published endpoint's
        /// <see cref="OutputCacheMetadata"/>, and ahead of <c>UseResponseCompression</c>.
        /// </para>
        /// <para>
        /// When the application registered the middleware more than once, the last registration wins.
        /// Placing the verb twice places the same registered instance, so both positions share one store.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseOutputCache()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<OutputCacheMiddleware>().LastOrDefault()
                    ?? new OutputCacheBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
