using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// The rewrite middleware: evaluates the rules in order, then either answers the exchange (a redirect, a
/// rule's own response, or a <c>400</c> for a target no request can carry), hands the rest of the pipeline a
/// request view carrying the rewritten URL, or passes the exchange on untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>The view.</b> A rewrite never mutates the transport's request. The rest of the pipeline receives a
/// <see cref="RewriteHttpContext"/> whose request has the rewritten path and query and forwards everything
/// else, so every reader of <see cref="IHttpRequest.Path"/> and <see cref="IHttpRequest.Query"/> after this
/// middleware sees the rewrite, and every component that holds the context this middleware received keeps
/// seeing the original (Web ADR 1).
/// </para>
/// <para>
/// <b>Inside a <c>Map(path)</c> branch</b> the rules see the path below the branch's prefix. A rewrite of it
/// publishes an <see cref="IWebPathBaseFeature"/> with the same path base over the rewritten path, and the
/// view's request path is the path base followed by the rewritten path. The branch's own feature is put back
/// when the rest of the pipeline returns.
/// </para>
/// <para>
/// <b>Loop protection.</b> A rule that restarts evaluation starts a new pass. More than the configured number
/// of passes is a rule set that loops, which fails the request with an
/// <see cref="InvalidOperationException"/> rather than spinning.
/// </para>
/// </remarks>
internal sealed class RewriteMiddleware : IWebApplicationMiddleware
{
    private readonly IRewriteRule[] _rules;
    private readonly int _maxPasses;

    public RewriteMiddleware(IRewriteRule[] rules, int maxPasses)
    {
        _rules = rules;
        _maxPasses = maxPasses;
    }

    /// <inheritdoc />
    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        IHttpRequest request = context.Request;

        // OPTIONS * addresses the server, not a resource: no rule applies to it.
        if (_rules.Length == 0 || request.Path.Value is "*")
        {
            return next.Invoke(context);
        }

        IWebPathBaseFeature? branch = context.Features.Get<IWebPathBaseFeature>();
        RewriteContext rewrite = new(context, branch?.PathBase ?? HttpPath.Root, branch?.Path ?? request.Path, request.Query);

        if (!Evaluate(rewrite))
        {
            Answer(context, rewrite);
            return Task.CompletedTask;
        }

        return rewrite.IsRewritten
            ? InvokeRewrittenAsync(context, branch, rewrite, next)
            : next.Invoke(context);
    }

    // Runs the rules in passes. Returns false when a rule ended the exchange.
    private bool Evaluate(RewriteContext rewrite)
    {
        for (int pass = 1; ; pass++)
        {
            if (pass > _maxPasses)
            {
                throw new InvalidOperationException(
                    $"URL rewriting did not finish within {_maxPasses} rule passes: a rule restarted rule evaluation on every pass. " +
                    $"Check the rules that restart evaluation ({nameof(RewriteFlow)}.{nameof(RewriteFlow.Restart)}), or raise {nameof(RewriteOptions)}.{nameof(RewriteOptions.MaxPasses)}.");
            }

            bool restart = false;

            for (int i = 0; i < _rules.Length && !restart; i++)
            {
                rewrite.BeginRule();
                _rules[i].Apply(rewrite);

                switch (rewrite.Outcome)
                {
                    case RewriteOutcome.Continue:
                        break;

                    case RewriteOutcome.SkipRemainingRules:
                        return true;

                    case RewriteOutcome.Restart:
                        restart = true;
                        break;

                    default:
                        // A redirect, a rule's own response, or a target no request can carry.
                        return false;
                }
            }

            if (!restart)
            {
                return true;
            }
        }
    }

    private static void Answer(IHttpContext context, RewriteContext rewrite)
    {
        IHttpResponse response = context.Response;

        switch (rewrite.Outcome)
        {
            case RewriteOutcome.Redirect:
                // Bodyless, like every Web redirect: being a 3xx, it is below the range status-code pages fill.
                response.StatusCode = rewrite.RedirectStatusCode;
                response.Headers[HttpHeaderKey.Location] = rewrite.RedirectLocation!;
                break;

            case RewriteOutcome.BadRequest:
                // The transports answer a request target that decodes to such a path the same way.
                response.StatusCode = HttpStatusCode.BadRequest;
                break;

            default:
                // EndResponse: the rule answered the exchange itself.
                break;
        }
    }

    private static async Task InvokeRewrittenAsync(IHttpContext context, IWebPathBaseFeature? branch, RewriteContext rewrite, WebApplicationMiddleware next)
    {
        HttpPath path = rewrite.Path;
        RewriteHttpContext view = new(context, branch is null ? path : Join(branch.PathBase, path), rewrite.Query);

        IHttpFeatureCollection features = context.Features;

        // A later rewrite (inside a branch, after one at the application level) keeps the first rewrite's
        // originals: they are the values the client sent.
        bool firstRewrite = features.Get<IWebRewriteFeature>() is null;
        if (firstRewrite)
        {
            features.Set<IWebRewriteFeature>(new RewriteFeature(context.Request.Path, context.Request.Query));
        }

        if (branch is not null)
        {
            features.Set<IWebPathBaseFeature>(new RewritePathBaseFeature(branch.PathBase, path));
        }

        try
        {
            await next.Invoke(view).ConfigureAwait(false);
        }
        finally
        {
            // The features describe the view, so they leave with it: the middleware before this one see the
            // request as they handed it on.
            if (branch is not null)
            {
                features.Set<IWebPathBaseFeature>(branch);
            }

            if (firstRewrite)
            {
                features.Set<IWebRewriteFeature>(null);
            }
        }
    }

    // The request path of a rewritten branch: the path base followed by the rewritten path below it.
    private static HttpPath Join(HttpPath pathBase, HttpPath path)
    {
        string prefix = pathBase.Value.TrimEnd('/');
        return prefix.Length == 0 ? path : new HttpPath(string.Concat(prefix, path.Value));
    }
}
