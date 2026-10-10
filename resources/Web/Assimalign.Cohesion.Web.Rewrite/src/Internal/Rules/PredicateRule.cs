using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// A predicate rule: when its predicate holds, it rewrites the URL to, or redirects to, a fixed target.
/// </summary>
/// <remarks>
/// The target has no pattern to capture from, so it is parsed once, at registration, and a rewrite target is
/// pre-parsed into the path and read-only query every matching request gets.
/// </remarks>
internal sealed class PredicateRule : RewriteRule
{
    private readonly Func<IRewriteContext, bool> _predicate;
    private readonly RewriteTarget _target;
    private readonly HttpPath _path;
    private readonly IHttpQueryCollection? _query;
    private readonly RewriteFlow _flow;
    private readonly HttpStatusCode? _redirectStatusCode;

    private PredicateRule(Func<IRewriteContext, bool> predicate, RewriteTarget target, RewriteFlow flow, HttpStatusCode? redirectStatusCode)
    {
        _predicate = predicate;
        _target = target;
        _flow = flow;
        _redirectStatusCode = redirectStatusCode;

        if (redirectStatusCode is null)
        {
            // The template parser rejected a path or query no request can carry, so both parse.
            RewriteUrl.TryParsePath(target.Path, out _path);

            if (target.Query is not null)
            {
                RewriteUrl.TryParseQuery(target.Query, out IHttpQueryCollection query);
                _query = query;
            }
        }
    }

    /// <summary>
    /// Creates an internal rewrite rule.
    /// </summary>
    public static PredicateRule CreateRewrite(Func<IRewriteContext, bool> predicate, string replacement, RewriteFlow flow) => new(
        predicate,
        RewriteTemplate.Parse(replacement, pattern: null, redirect: false, nameof(replacement)).Expand(null, string.Empty, 0),
        flow,
        redirectStatusCode: null);

    /// <summary>
    /// Creates a redirect rule.
    /// </summary>
    public static PredicateRule CreateRedirect(Func<IRewriteContext, bool> predicate, string replacement, HttpStatusCode statusCode) => new(
        predicate,
        RewriteTemplate.Parse(replacement, pattern: null, redirect: true, nameof(replacement)).Expand(null, string.Empty, 0),
        RewriteFlow.Continue,
        statusCode);

    /// <inheritdoc />
    public override void Apply(RewriteContext context)
    {
        if (!_predicate(context))
        {
            return;
        }

        if (_redirectStatusCode is HttpStatusCode statusCode)
        {
            context.Redirect(RewriteUrl.BuildLocation(context.PathBase, _target, _target.Query ?? context.SerializedQuery), statusCode);
            return;
        }

        context.Rewrite(_path, _query, _flow);
    }
}
