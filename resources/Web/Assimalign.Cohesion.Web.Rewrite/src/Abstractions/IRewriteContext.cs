using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// The state an <see cref="IRewriteRule"/> works on: the URL as the earlier rules left it, the exchange, and
/// the actions a rule can take.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path"/> and <see cref="Query"/> start as the request's effective path (the path below the
/// prefix inside a <c>Map(path)</c> branch, the request path otherwise) and query, and change as rules
/// rewrite them. <see cref="HttpContext"/> is the exchange as the rewrite middleware received it: read its
/// method, headers, features and effective scheme and host there, but not its path and query, which no
/// rule changes.
/// </para>
/// <para>
/// An action applies once the rule returns. <see cref="Rewrite"/> and <see cref="SkipRemainingRules"/> may be
/// called more than once, and the last call decides what happens next. <see cref="Redirect"/> and
/// <see cref="EndResponse"/> end the exchange: no later action is accepted, no further rule runs, and the
/// rest of the pipeline does not run.
/// </para>
/// </remarks>
public interface IRewriteContext
{
    /// <summary>
    /// Gets the exchange, as the rewrite middleware received it.
    /// </summary>
    /// <remarks>
    /// Its <see cref="IHttpContext.Request"/> keeps the path and query the middleware received; the values
    /// the rules see are <see cref="Path"/> and <see cref="Query"/>. A rule that answers the request itself
    /// writes the response here and calls <see cref="EndResponse"/>.
    /// </remarks>
    IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets the prefixes of the <c>Map(path)</c> branches the middleware runs in, or <see cref="HttpPath.Root"/>
    /// outside any branch. Rules match and rewrite the path below it, and a redirect rule's target path is
    /// relative to it.
    /// </summary>
    HttpPath PathBase { get; }

    /// <summary>
    /// Gets the path the rules see: the percent-decoded effective path of the request, as rewritten by the
    /// rules that ran before this one.
    /// </summary>
    HttpPath Path { get; }

    /// <summary>
    /// Gets the query the rules see, as rewritten by the rules that ran before this one.
    /// </summary>
    IHttpQueryCollection Query { get; }

    /// <summary>
    /// Rewrites the URL the later rules, and the rest of the pipeline, see.
    /// </summary>
    /// <param name="path">
    /// The new path, percent-decoded and starting with <c>/</c>. Inside a <c>Map(path)</c> branch it is the
    /// path below <see cref="PathBase"/>.
    /// </param>
    /// <param name="query">The new query, or <see langword="null"/> to keep the current one.</param>
    /// <param name="flow">What the rule engine does next. Defaults to <see cref="RewriteFlow.Continue"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not an origin-form path that starts with <c>/</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flow"/> is not a defined <see cref="RewriteFlow"/> value.</exception>
    /// <exception cref="InvalidOperationException">The rule already redirected or ended the response.</exception>
    void Rewrite(HttpPath path, IHttpQueryCollection? query = null, RewriteFlow flow = RewriteFlow.Continue);

    /// <summary>
    /// Ends rule evaluation and the exchange with a bodyless redirect.
    /// </summary>
    /// <param name="location">
    /// The <c>Location</c> header value, written as given: an absolute URL or an absolute path, already
    /// percent-encoded. It is not made relative to <see cref="PathBase"/>.
    /// </param>
    /// <param name="statusCode">The redirect status: <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="location"/> is <see langword="null"/>, empty or contains a control character or a space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</exception>
    /// <exception cref="InvalidOperationException">The rule already redirected or ended the response.</exception>
    void Redirect(string location, HttpStatusCode statusCode);

    /// <summary>
    /// Ends rule evaluation without ending the exchange: no further rule runs, and the pipeline continues
    /// with the URL as rewritten so far.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rule already redirected or ended the response.</exception>
    void SkipRemainingRules();

    /// <summary>
    /// Ends rule evaluation and the exchange: the rule answered the request itself through
    /// <see cref="HttpContext"/>, and the rest of the pipeline does not run.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rule already redirected or ended the response.</exception>
    void EndResponse();
}
