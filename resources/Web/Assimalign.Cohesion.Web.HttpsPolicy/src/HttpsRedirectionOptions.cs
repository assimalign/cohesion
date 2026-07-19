using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.HttpsPolicy;

/// <summary>
/// Configuration for HTTP-to-HTTPS redirection, applied by the middleware <c>UseHttpsRedirection</c>
/// registers. An insecure request is answered with a method-preserving redirect to the same host and
/// request target on the <c>https</c> scheme and the configured HTTPS port.
/// </summary>
/// <remarks>
/// <para>
/// The options are captured once at builder time — the middleware resolves nothing per request. The
/// redirect target is rebuilt from the request itself: the <c>https</c> scheme, the request's own host
/// (its port replaced by <see cref="HttpsPort"/>), and the request path and query preserved. Both the
/// redirect status and the HTTPS port are validated when the verb is called, so a misconfiguration
/// surfaces at startup rather than as per-request behavior.
/// </para>
/// </remarks>
public sealed class HttpsRedirectionOptions
{
    /// <summary>
    /// Gets or sets the redirect status code. Defaults to <c>307 Temporary Redirect</c>
    /// (<see cref="HttpStatusCode.RedirectKeepVerb"/>); set it to <c>308 Permanent Redirect</c>
    /// (<see cref="HttpStatusCode.PermanentRedirect"/>) to signal a permanent move. Both statuses
    /// preserve the request method and body across the redirect (RFC 9110 §15.4.8 / §15.4.9); no
    /// other status is permitted and any other value is rejected when the verb is called.
    /// </summary>
    /// <remarks>
    /// The method-preserving statuses are chosen deliberately: the historical <c>301</c>/<c>302</c>
    /// redirects are rewritten to <c>GET</c> by user agents, which would silently drop the method and
    /// body of a non-<c>GET</c> request being upgraded to HTTPS.
    /// </remarks>
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.RedirectKeepVerb;

    /// <summary>
    /// Gets or sets the HTTPS port the redirect targets. Defaults to <c>443</c>, the scheme default,
    /// which is omitted from the redirect <c>Location</c>; any other port is emitted explicitly. Must
    /// be in the range 1–65535.
    /// </summary>
    /// <remarks>
    /// The port is an explicit setting because a feature-package middleware cannot see the server's
    /// endpoint bindings without referencing the hosting module (which the resource hosting-isolation
    /// rule forbids). Set it to the port the HTTPS listener actually binds.
    /// </remarks>
    public int HttpsPort { get; set; } = 443;
}
