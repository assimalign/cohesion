using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// Canonicalizes the case of the path: redirects a path with an uppercase letter to its lowercase form
/// (invariant culture), keeping the query as it is.
/// </summary>
/// <remarks>
/// Inside a <c>Map(path)</c> branch only the path below the branch's prefix is lowercased. The rule is
/// idempotent: the target of its redirect is a path it leaves alone, so it cannot loop.
/// </remarks>
internal sealed class LowercaseRedirectRule : RewriteRule
{
    private readonly HttpStatusCode _statusCode;

    public LowercaseRedirectRule(HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
    }

    /// <inheritdoc />
    public override void Apply(RewriteContext context)
    {
        string path = context.Path.Value;
        string lowercase = path.ToLowerInvariant();

        if (string.Equals(path, lowercase, StringComparison.Ordinal))
        {
            return;
        }

        context.Redirect(
            RewriteUrl.BuildLocation(null, null, context.PathBase, new HttpPath(lowercase), context.SerializedQuery),
            _statusCode);
    }
}
