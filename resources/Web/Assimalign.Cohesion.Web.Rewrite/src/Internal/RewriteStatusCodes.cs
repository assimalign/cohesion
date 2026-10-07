using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// The redirect statuses a rule may answer with.
/// </summary>
internal static class RewriteStatusCodes
{
    /// <summary>
    /// Throws unless <paramref name="statusCode"/> is <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.
    /// </summary>
    /// <remarks>
    /// <c>303 See Other</c> answers a request with a different resource rather than moving it, and the other
    /// <c>3xx</c> statuses are not redirects a client follows, so neither is a rewrite redirect.
    /// </remarks>
    public static void ValidateRedirect(HttpStatusCode statusCode, string parameterName)
    {
        if (statusCode.Value is not (301 or 302 or 307 or 308))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                statusCode.Value,
                "A redirect status must be 301, 302, 307 or 308.");
        }
    }
}
