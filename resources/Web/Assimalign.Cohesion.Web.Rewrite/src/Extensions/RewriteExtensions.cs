using System;
using System.Linq;

using Assimalign.Cohesion.Web.Rewrite.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// Pipeline-builder members that place URL rewriting and redirect rules in a web application's pipeline.
/// </summary>
/// <remarks>
/// Place <c>UseRewrite</c> ahead of <c>UseStaticFiles</c> and <c>UseRouting</c>, so both see the
/// rewritten URL, and after <c>UseForwardedHeaders</c> and <c>UseHostFiltering</c>, so the canonicalization
/// redirects read the effective scheme and a validated host (docs/resources/Web/MIDDLEWARE_ORDER.md).
/// Composition is dependency-free: the rules are built and validated where
/// <c>builder.Services.AddRewrite(rewrite =&gt; ...)</c> registers them (<see cref="RewriteBuilder"/>), and no
/// request-time service location occurs.
/// </remarks>
public static class RewriteExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the rewrite middleware the application registered with
        /// <c>builder.Services.AddRewrite(rewrite =&gt; ...)</c>. It evaluates the registered rules, in order,
        /// for every request: an internal rewrite hands the rest of the pipeline the rewritten path and query,
        /// and a redirect answers the request and ends the pipeline.
        /// </summary>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The rules and the bound on rule passes are configured where the middleware is registered
        /// (<see cref="RewriteBuilder"/>); this verb only positions it. The rules have no default, so composing
        /// the pipeline fails with an <see cref="InvalidOperationException"/> when the application registered
        /// none. When it registered more than one, the last registration wins, and placing the verb more than
        /// once, in a <c>Map(path)</c> branch included, places that same middleware each time.
        /// </para>
        /// <para>
        /// A rewrite does not change the request the middleware received. The rest of the pipeline receives a
        /// view of the exchange whose <c>Request.Path</c> and <c>Request.Query</c> are the rewritten values and
        /// whose every other member is the original's, and <see cref="IRewriteFeature"/> keeps the original
        /// values readable while that pipeline runs. Middleware placed ahead of <c>UseRewrite</c>, and the
        /// server's request telemetry, keep the original values: a component that holds the context it handed
        /// to <c>next</c> still sees the original path after <c>next</c> returns. The <c>http.route</c> the
        /// server reports is the route the rewritten path matched.
        /// </para>
        /// <para>
        /// Inside a <c>Map(path)</c> branch the rules see, and rewrite, the path below the branch's prefix; the
        /// branch's <see cref="IRouterPathBaseFeature"/> then reports the rewritten path under the same path base.
        /// Requests that the runtime's control plane and health endpoints answer ahead of the application
        /// pipeline never reach the rules.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseRewrite()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<RewriteMiddleware>().LastOrDefault()
                    ?? throw new InvalidOperationException(ErrorMessages.RewriteNotRegistered);

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
