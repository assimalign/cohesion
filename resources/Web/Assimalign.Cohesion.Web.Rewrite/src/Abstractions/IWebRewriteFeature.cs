using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// The request's path and query as they were before <c>UseRewrite</c> rewrote them. Everything after the
/// rewrite reads the rewritten values from <see cref="IHttpContext.Request"/>; this feature keeps the
/// originals readable.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite middleware publishes the feature only when a rule rewrote the request, and only while the
/// rest of the pipeline runs: it is removed when that pipeline returns, so the feature is present exactly
/// where <see cref="IHttpContext.Request"/> carries rewritten values. When rewrite middleware runs more than
/// once for a request (at the application level and again inside a <c>Map(path)</c> branch), the feature
/// keeps the values from before the first rewrite, which are the values the client sent.
/// </para>
/// <para>
/// <see cref="OriginalPath"/> is the full request path, including the prefixes of any path branch the
/// request entered. Read it, for example, to log or link to the URL the client asked for: a URL built from
/// the rewritten request, such as a sign-in redirect's return URL, carries the rewritten path.
/// </para>
/// </remarks>
public interface IWebRewriteFeature : IHttpFeature
{
    /// <summary>
    /// Gets the request path before the first rewrite: the full, percent-decoded path the client sent.
    /// </summary>
    HttpPath OriginalPath { get; }

    /// <summary>
    /// Gets the query before the first rewrite.
    /// </summary>
    IHttpQueryCollection OriginalQuery { get; }
}
