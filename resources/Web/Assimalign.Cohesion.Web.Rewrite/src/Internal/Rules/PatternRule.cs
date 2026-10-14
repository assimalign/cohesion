using System.Text.RegularExpressions;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// A regular-expression rule: when its pattern matches the path (or the path and query), it rewrites the
/// URL to, or redirects to, its target with the captures substituted.
/// </summary>
internal sealed class PatternRule : RewriteRule
{
    private readonly Regex _pattern;
    private readonly RewriteTemplate _template;
    private readonly RewriteMatchTarget _target;
    private readonly RewriteFlow _flow;
    private readonly HttpStatusCode? _redirectStatusCode;

    private PatternRule(Regex pattern, RewriteTemplate template, RewriteMatchTarget target, RewriteFlow flow, HttpStatusCode? redirectStatusCode)
    {
        _pattern = pattern;
        _template = template;
        _target = target;
        _flow = flow;
        _redirectStatusCode = redirectStatusCode;
    }

    /// <summary>
    /// Creates an internal rewrite rule.
    /// </summary>
    public static PatternRule CreateRewrite(Regex pattern, string replacement, RewriteFlow flow, RewriteMatchTarget target) => new(
        pattern,
        RewriteTemplate.Parse(replacement, pattern, redirect: false, nameof(replacement)),
        target,
        flow,
        redirectStatusCode: null);

    /// <summary>
    /// Creates a redirect rule.
    /// </summary>
    public static PatternRule CreateRedirect(Regex pattern, string replacement, HttpStatusCode statusCode, RewriteMatchTarget target) => new(
        pattern,
        RewriteTemplate.Parse(replacement, pattern, redirect: true, nameof(replacement)),
        target,
        RewriteFlow.Continue,
        statusCode);

    /// <inheritdoc />
    public override void Apply(RewriteContext context)
    {
        string input = context.GetMatchInput(_target, out int pathLength);
        Match match = _pattern.Match(input);

        if (!match.Success)
        {
            return;
        }

        RewriteTarget target = _template.Expand(match, input, pathLength);

        if (_redirectStatusCode is HttpStatusCode statusCode)
        {
            context.Redirect(RewriteUrl.BuildLocation(context.PathBase, target, target.Query ?? context.SerializedQuery), statusCode);
            return;
        }

        ApplyRewrite(context, target, _flow);
    }
}
