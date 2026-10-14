using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.HttpsPolicy.Internal;

/// <summary>
/// The HTTP-to-HTTPS redirect guard: an insecure request is answered with a bodyless method-preserving
/// redirect (<c>307</c> by default, <c>308</c> when configured) whose <c>Location</c> is the same
/// request re-addressed to the <c>https</c> scheme and the configured HTTPS port. An already-secure
/// request passes straight through.
/// </summary>
/// <remarks>
/// <para>
/// Connection security is the <em>effective</em> typed scheme
/// (<see cref="HttpContextForwardedExtensions.EffectiveScheme"/>): the scheme the client used on the
/// outermost trusted hop when the forwarded-headers middleware resolved one, otherwise
/// <see cref="IHttpRequest.Scheme"/> — the transport-derived scheme the Web TLS surface (#763) resolves
/// from the listener's transport-security capability. Behind a TLS-terminating proxy the app-facing hop
/// is plaintext, so reading the wire scheme would redirect every request and loop; reading the effective
/// scheme lets a trusted proxy's <c>https</c> through. There is no header inspection and no
/// scheme-string sniffing here: forwarding headers are believed only by the forwarded-headers trust
/// model, and without it the effective scheme is exactly the wire scheme.
/// </para>
/// <para>
/// The <c>Location</c> is rebuilt from the request itself: the effective host (the host the client
/// addressed, not the proxy's upstream authority) with its inbound port replaced by the HTTPS port (the
/// default <c>443</c> is omitted), the decoded request path percent-encoded back per segment
/// (<see cref="HttpPath.ToUriComponent"/>), and the query rebuilt from the parsed query collection
/// (<see cref="HttpQuery.Create"/>) — the raw query string is not carried on <see cref="IHttpRequest"/>, so
/// the reconstruction re-encodes each key/value and follows the parsed collection's enumeration order
/// (see the package <c>docs/DESIGN.md</c>). The response is status + <c>Location</c> only; being a
/// <c>3xx</c>, it is below the <c>4xx</c>/<c>5xx</c> range the status-code-pages middleware acts on, so
/// no body is ever added.
/// </para>
/// <para>
/// A host the redirect cannot name is answered with <c>400</c> (RFC 9112 §3.2) instead of a redirect:
/// an empty one, one that is not a well-formed <c>host[:port]</c>, or one whose host carries a character
/// outside the unreserved set and <c>:</c>, which could change where the URL points. The request is never
/// passed on, so a malformed host cannot reach the application over the insecure connection either.
/// </para>
/// </remarks>
internal sealed class HttpsRedirectionMiddleware : IWebApplicationMiddleware
{
    private const int HttpsDefaultPort = 443;

    // A host the redirect may name: a DNS name, an IPv4 literal, or an IPv6 literal without its brackets
    // (RFC 3986 §2.3 unreserved, plus ':'). The transport only requires visible ASCII, so a '/', '@', '\',
    // '?' or '#' in Host would otherwise move the rest of the URL into the authority or out of it.
    private static readonly SearchValues<char> _hostCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~:");

    private readonly HttpStatusCode _statusCode;
    private readonly int _httpsPort;

    public HttpsRedirectionMiddleware(HttpStatusCode statusCode, int httpsPort)
    {
        _statusCode = statusCode;
        _httpsPort = httpsPort;
    }

    /// <inheritdoc />
    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Already secure: nothing to do. The scheme is the effective one — a trusted TLS-terminating
        // proxy's https counts, which is what keeps a proxied deployment from redirecting in a loop.
        if (context.EffectiveScheme == HttpScheme.Https)
        {
            return next.Invoke(context);
        }

        // Insecure: answer a bodyless, method-preserving redirect and short-circuit. Registered
        // earliest, this discards the request before any downstream middleware works on a response
        // that is about to be thrown away.
        IHttpResponse response = context.Response;

        // The effective host is the authority the client addressed; behind a proxy the wire host is the
        // proxy's upstream authority, which the client cannot reach.
        if (!TryBuildAuthority(context.EffectiveHost, _httpsPort, out string? authority))
        {
            // RFC 9112 §3.2: a Host field with an invalid value is answered with 400. Reflecting the raw
            // value instead would put unparsed request text in Location.
            response.StatusCode = HttpStatusCode.BadRequest;
            return Task.CompletedTask;
        }

        response.StatusCode = _statusCode;
        response.Headers[HttpHeaderKey.Location] = string.Concat("https://", authority, BuildPathAndQuery(context.Request));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the URL authority: the request host with its inbound port removed and replaced by the
    /// HTTPS port (the default 443 is omitted). An IPv6 literal is re-bracketed for URL use. Fails for a
    /// host that is empty, not a well-formed <c>host[:port]</c>, or carries a character outside
    /// <see cref="_hostCharacters"/>.
    /// </summary>
    private static bool TryBuildAuthority(HttpHost host, int httpsPort, [NotNullWhen(true)] out string? authority)
    {
        authority = null;

        // Split off any inbound (plaintext) port so it can be replaced by the HTTPS port.
        if (!host.TryGetComponents(out ReadOnlySpan<char> component, out _)
            || component.IsEmpty
            || component.ContainsAnyExcept(_hostCharacters))
        {
            return false;
        }

        string hostText = component.ToString();

        // The component split strips IPv6 brackets; a colon-bearing host is an IPv6 literal that must
        // be re-bracketed to sit in a URL authority.
        if (hostText.IndexOf(':') >= 0)
        {
            hostText = string.Concat("[", hostText, "]");
        }

        authority = httpsPort == HttpsDefaultPort
            ? hostText
            : string.Concat(hostText, ":", httpsPort.ToString(CultureInfo.InvariantCulture));

        return true;
    }

    /// <summary>
    /// Reconstructs the origin-form path and query. The transports hand the path over percent-decoded,
    /// so it is encoded back per segment; the query is rebuilt from the parsed collection (the raw query
    /// string is not exposed on <see cref="IHttpRequest"/>), re-encoding each key and value and following
    /// the collection's enumeration order. The asterisk-form <c>*</c> becomes the empty path.
    /// </summary>
    private static string BuildPathAndQuery(IHttpRequest request)
    {
        // RFC 9112 §3.2.4 — "*" (an OPTIONS request for the server as a whole) names no path: its
        // absolute-form is the authority with an empty path and no query. Written after the authority it
        // would become part of it ("https://example.com*").
        if (string.Equals(request.Path.Value, "*", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string path = request.Path.ToUriComponent();
        IHttpQueryCollection query = request.Query;

        if (query is null || query.Count == 0)
        {
            return path;
        }

        return string.Concat(path, "?", HttpQuery.Create(query).Value);
    }
}
