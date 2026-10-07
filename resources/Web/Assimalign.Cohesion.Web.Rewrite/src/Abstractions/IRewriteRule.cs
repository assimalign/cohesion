namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// A rewrite rule: one step of the ordered rule list <c>UseRewrite</c> evaluates for every request.
/// </summary>
/// <remarks>
/// <para>
/// A rule inspects the URL through <see cref="IRewriteContext.Path"/> and <see cref="IRewriteContext.Query"/>,
/// which reflect the rewrites of the rules before it, and acts through the context: it rewrites the URL,
/// redirects, ends the response, stops rule evaluation, or does nothing, in which case the next rule runs.
/// </para>
/// <para>
/// Rules are registered once, when the pipeline is composed, and shared by every request, so an
/// implementation keeps no per-request state of its own. Evaluation is synchronous: a rule decides from the
/// request it is given, without I/O.
/// </para>
/// </remarks>
public interface IRewriteRule
{
    /// <summary>
    /// Applies the rule to the request as the rules before it left it.
    /// </summary>
    /// <param name="context">The rule evaluation context of the current request.</param>
    void Apply(IRewriteContext context);
}
