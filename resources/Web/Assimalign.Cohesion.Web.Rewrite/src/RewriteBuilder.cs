using System.Linq;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Rewrite.Internal;

namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// Configures the URL rewriting and redirect middleware that
/// <c>builder.Services.AddRewrite(rewrite =&gt; ...)</c> registers and
/// <see cref="RewriteExtensions.UseRewrite(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <para>
/// The rules are added to <see cref="Options"/>, in evaluation order, and each is built and validated as it
/// is added, so a rule with an invalid pattern, target, status, flow or match target fails where the
/// application registers the rewrite, never per request.
/// </para>
/// <para>
/// <see cref="Build"/> copies the rules and the pass bound into the middleware, so changing the options
/// afterwards has no effect on the middleware already built.
/// </para>
/// </remarks>
public sealed class RewriteBuilder
{
    /// <summary>
    /// Initializes a builder with no rules and the default bound of <c>10</c> rule passes per request.
    /// </summary>
    public RewriteBuilder()
    {
    }

    /// <summary>
    /// Gets the rewrite options: the rules, in the order they are added, and the bound on rule passes.
    /// </summary>
    public RewriteOptions Options { get; } = new();

    /// <summary>
    /// Builds the rewrite middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which evaluates the rules in order for every request: an internal rewrite hands the
    /// rest of the pipeline the rewritten path and query, and a redirect answers the request and ends the
    /// pipeline. With no rules it passes every request through.
    /// </returns>
    public IWebApplicationMiddleware Build()
    {
        return new RewriteMiddleware(Options.Rules.ToArray(), Options.MaxPasses);
    }
}
