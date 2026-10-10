using System;
using System.Linq;

using Assimalign.Cohesion.Web.Rewrite.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// Pipeline-builder members that add URL rewriting and redirect rules to a web application.
/// </summary>
/// <remarks>
/// Register <c>UseRewrite</c> ahead of <c>UseStaticFiles</c> and <c>UseRouting</c>, so both see the
/// rewritten URL, and after <c>UseForwardedHeaders</c> and <c>UseHostFiltering</c>, so the canonicalization
/// redirects read the effective scheme and a validated host (docs/resources/Web/MIDDLEWARE_ORDER.md).
/// Composition is dependency-free: the rules are built and validated when <c>UseRewrite</c> is called, and no
/// request-time service location occurs.
/// </remarks>
public static class RewriteExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that evaluates the rules <paramref name="configure"/> registers, in order, for every
        /// request: an internal rewrite hands the rest of the pipeline the rewritten path and query, and a
        /// redirect answers the request and ends the pipeline.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A rewrite does not change the request the middleware received. The rest of the pipeline receives a
        /// view of the exchange whose <c>Request.Path</c> and <c>Request.Query</c> are the rewritten values and
        /// whose every other member is the original's, and <see cref="IWebRewriteFeature"/> keeps the original
        /// values readable while that pipeline runs. Middleware registered ahead of <c>UseRewrite</c>, and the
        /// server's request telemetry, keep the original values: a component that holds the context it handed
        /// to <c>next</c> still sees the original path after <c>next</c> returns. The <c>http.route</c> the
        /// server reports is the route the rewritten path matched.
        /// </para>
        /// <para>
        /// Inside a <c>Map(path)</c> branch the rules see, and rewrite, the path below the branch's prefix; the
        /// branch's <see cref="IWebPathBaseFeature"/> then reports the rewritten path under the same path base.
        /// Requests that the runtime's control plane and health endpoints answer ahead of the application
        /// pipeline never reach the rules.
        /// </para>
        /// </remarks>
        /// <param name="configure">Registers the rules and sets the bound on rule passes.</param>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="configure"/> registers a rule with an invalid pattern or target.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="configure"/> registers a rule with an invalid status, flow or match target.</exception>
        public IWebApplicationPipelineBuilder UseRewrite(Action<RewriteOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            RewriteOptions options = new();
            configure(options);

            return builder.Use(new RewriteMiddleware(options.Rules.ToArray(), options.MaxPasses));
        }
    }
}
