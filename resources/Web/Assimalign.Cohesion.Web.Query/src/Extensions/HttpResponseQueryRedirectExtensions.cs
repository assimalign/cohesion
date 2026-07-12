using System;

namespace Assimalign.Cohesion.Web.Query;

using Assimalign.Cohesion.Http;

/// <summary>
/// Redirect helpers that preserve the QUERY method across a redirect (RFC 10008 &#167; 2.5):
/// a redirected query re-targets with its method and content intact, and the one sanctioned
/// method switch is the explicit <c>303 See Other</c> hand-off to GET (&#167; 2.5.3).
/// </summary>
/// <remarks>
/// <para>
/// The helpers deliberately never emit <c>301 Moved Permanently</c> or <c>302 Found</c> for a
/// query: user agents historically rewrite those to GET (the legacy POST behavior RFC 9110
/// &#167; 15.4.2 / &#167; 15.4.3 notes), which would silently drop the query content. The
/// method-preserving statuses <c>307 Temporary Redirect</c> and <c>308 Permanent Redirect</c>
/// (RFC 9110 &#167; 15.4.8 / &#167; 15.4.9) forbid that rewrite, so a redirected QUERY is
/// re-issued as a QUERY with the original content.
/// </para>
/// <para>
/// The helpers write the status and <c>Location</c> field only; they attach no response body.
/// They are safe on any request method — the chosen statuses are method-agnostic — but exist so
/// that redirecting a QUERY never implies a GET downgrade.
/// </para>
/// </remarks>
public static class HttpResponseQueryRedirectExtensions
{
    extension(IHttpResponse response)
    {
        /// <summary>
        /// Redirects the query to <paramref name="location"/> preserving the request method and
        /// content: <c>307 Temporary Redirect</c>, or <c>308 Permanent Redirect</c> when
        /// <paramref name="permanent"/> is <see langword="true"/>.
        /// </summary>
        /// <param name="location">The redirect target — an absolute URI or a relative reference, emitted as the <c>Location</c> field.</param>
        /// <param name="permanent"><see langword="true"/> to emit <c>308</c>; otherwise <c>307</c>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="response"/> or <paramref name="location"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="location"/> is empty.</exception>
        public void RedirectQuery(string location, bool permanent = false)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentException.ThrowIfNullOrEmpty(location);

            response.StatusCode = permanent ? HttpStatusCode.PermanentRedirect : HttpStatusCode.RedirectKeepVerb;
            response.Headers[HttpHeaderKey.Location] = location;
        }

        /// <summary>
        /// Redirects the client to fetch <paramref name="location"/> with a GET —
        /// <c>303 See Other</c>, the one intentional method switch RFC 10008 &#167; 2.5.3
        /// sanctions for a query (for example, handing off to a stored result resource).
        /// </summary>
        /// <param name="location">The resource to retrieve with GET, emitted as the <c>Location</c> field.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="response"/> or <paramref name="location"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="location"/> is empty.</exception>
        public void RedirectQueryToGet(string location)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentException.ThrowIfNullOrEmpty(location);

            response.StatusCode = HttpStatusCode.SeeOther;
            response.Headers[HttpHeaderKey.Location] = location;
        }
    }
}
