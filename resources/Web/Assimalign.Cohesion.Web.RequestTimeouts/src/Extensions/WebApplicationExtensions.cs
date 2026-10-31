using System;
using System.Linq;

using Assimalign.Cohesion.Web.RequestTimeouts.Internal;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// Pipeline-builder members that place request-timeout enforcement in a web application.
/// </summary>
/// <remarks>
/// Place <c>UseRequestTimeouts</c> <b>after</b> <c>UseRouting</c> and before the long-running
/// middleware it should govern: the middleware reads the endpoint <c>UseRouting</c> published to pick
/// the endpoint's policy, then governs everything downstream of it, the endpoint included.
/// The middleware is configured where it is registered, with
/// <c>builder.Services.AddRequestTimeouts(timeouts =&gt; ...)</c> (<see cref="RequestTimeoutsBuilder"/>);
/// no request-time service location occurs.
/// </remarks>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the request-timeout middleware the application registered with
        /// <c>builder.Services.AddRequestTimeouts(timeouts =&gt; ...)</c>, or one with the defaults when
        /// it registered none. The defaults apply no global timeout: only endpoints carrying
        /// <see cref="RequestTimeoutMetadata"/> (or handlers arming
        /// <see cref="IRequestTimeoutFeature.SetTimeout"/>) are governed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The global default policy, the time provider and the debugger suspension are configured where
        /// the middleware is registered (<see cref="RequestTimeoutsBuilder"/>); this verb only positions
        /// it. When the application registered the middleware more than once, the last registration wins.
        /// </para>
        /// <para>
        /// Place it after <c>UseRouting</c>. The timer starts when the exchange reaches the
        /// middleware, armed with the published endpoint's <see cref="RequestTimeoutMetadata"/> policy
        /// when it carries one, which replaces the global default, and with the global default
        /// otherwise (including for requests no route matched).
        /// </para>
        /// <para>
        /// Placed ahead of <c>UseRouting</c>, the global default still governs every request, but no
        /// endpoint is known yet: an endpoint whose metadata carries a timeout then fails with an
        /// <see cref="InvalidOperationException"/> when it is dispatched instead of running unbounded,
        /// and an endpoint that disables the timeout runs under the global default.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseRequestTimeouts()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<RequestTimeoutMiddleware>().LastOrDefault()
                    ?? new RequestTimeoutsBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
