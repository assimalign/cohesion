using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// The base of the rules <see cref="RewriteOptions"/> registers: they work on the engine's own
/// <see cref="RewriteContext"/>, which caches the re-encoded query and can reject a target.
/// </summary>
/// <remarks>
/// Only the engine invokes these rules, and it always passes its own context. An application's own
/// <see cref="IRewriteRule"/> implementations work on the public <see cref="IRewriteContext"/>.
/// </remarks>
internal abstract class RewriteRule : IRewriteRule
{
    /// <summary>
    /// Applies the rule.
    /// </summary>
    /// <param name="context">The request's rule evaluation context.</param>
    public abstract void Apply(RewriteContext context);

    void IRewriteRule.Apply(IRewriteContext context) => Apply((RewriteContext)context);

    /// <summary>
    /// Rewrites the URL to an expanded target, or answers <c>400</c> when the target decodes to a path or
    /// query no request can carry.
    /// </summary>
    protected static void ApplyRewrite(RewriteContext context, RewriteTarget target, RewriteFlow flow)
    {
        if (!RewriteUrl.TryParsePath(target.Path, out HttpPath path))
        {
            context.RejectTarget();
            return;
        }

        IHttpQueryCollection? query = null;
        if (target.Query is not null && !RewriteUrl.TryParseQuery(target.Query, out query))
        {
            context.RejectTarget();
            return;
        }

        context.Rewrite(path, query, flow);
    }
}
