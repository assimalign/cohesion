using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// Canonicalizes the trailing slash: redirects a path to its form with a trailing slash, or to its form
/// without one, keeping the query.
/// </summary>
/// <remarks>
/// <para>
/// The root path (<c>/</c>, or the root of a <c>Map(path)</c> branch) is never redirected. Adding a slash
/// skips a path whose last segment contains a <c>.</c>, which names a file (<c>/app.js</c>) rather than a
/// directory. Removing it strips every trailing slash, so <c>/docs//</c> becomes <c>/docs</c> in one hop.
/// </para>
/// <para>
/// The rule is idempotent: the target of its redirect is a path it leaves alone, so it cannot loop.
/// </para>
/// </remarks>
internal sealed class TrailingSlashRedirectRule : RewriteRule
{
    private readonly bool _append;
    private readonly HttpStatusCode _statusCode;

    public TrailingSlashRedirectRule(bool append, HttpStatusCode statusCode)
    {
        _append = append;
        _statusCode = statusCode;
    }

    /// <inheritdoc />
    public override void Apply(RewriteContext context)
    {
        string path = context.Path.Value;

        if (path.Length <= 1)
        {
            return;
        }

        string target;

        if (_append)
        {
            if (path[^1] == '/' || path.IndexOf('.', path.LastIndexOf('/') + 1) >= 0)
            {
                return;
            }

            target = path + "/";
        }
        else
        {
            if (path[^1] != '/')
            {
                return;
            }

            target = path.TrimEnd('/');

            if (target.Length == 0)
            {
                return;
            }
        }

        context.Redirect(
            RewriteUrl.BuildLocation(null, null, context.PathBase, new HttpPath(target), context.SerializedQuery),
            _statusCode);
    }
}
