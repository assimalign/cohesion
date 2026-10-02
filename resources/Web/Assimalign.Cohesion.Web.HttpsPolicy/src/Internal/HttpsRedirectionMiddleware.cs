using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
/// default <c>443</c> is omitted), the request path preserved verbatim, and the query reconstructed from
/// the parsed query collection — the raw query string is not carried on <see cref="IHttpRequest"/>, so
/// the reconstruction re-encodes each key/value and follows the parsed collection's enumeration order
/// (see the package <c>docs/DESIGN.md</c>). The response is status + <c>Location</c> only; being a
/// <c>3xx</c>, it is below the <c>4xx</c>/<c>5xx</c> range the status-code-pages middleware acts on, so
/// no body is ever added.
/// </para>
/// </remarks>
internal sealed class HttpsRedirectionMiddleware : IWebApplicationMiddleware
{
    private const int HttpsDefaultPort = 443;

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
        response.StatusCode = _statusCode;
        response.Headers[HttpHeaderKey.Location] = BuildLocation(context, _httpsPort);

        return Task.CompletedTask;
    }

    private static string BuildLocation(IHttpContext context, int httpsPort)
    {
        // The effective host is the authority the client addressed; behind a proxy the wire host is the
        // proxy's upstream authority, which the client cannot reach.
        string authority = BuildAuthority(context.EffectiveHost, httpsPort);
        string pathAndQuery = BuildPathAndQuery(context.Request);

        return string.Concat("https://", authority, pathAndQuery);
    }

    /// <summary>
    /// Builds the URL authority: the request host with its inbound port removed and replaced by the
    /// HTTPS port (the default 443 is omitted). An IPv6 literal is re-bracketed for URL use.
    /// </summary>
    private static string BuildAuthority(HttpHost host, int httpsPort)
    {
        // Split off any inbound (plaintext) port so it can be replaced by the HTTPS port.
        if (!host.TryGetComponents(out ReadOnlySpan<char> component, out _) || component.IsEmpty)
        {
            // Not a well-formed host[:port] (or hostless): fall back to the raw value rather than
            // manufacture an authority. A malformed authority is an upstream concern.
            return host.Value;
        }

        string hostText = component.ToString();

        // The component split strips IPv6 brackets; a colon-bearing host is an IPv6 literal that must
        // be re-bracketed to sit in a URL authority.
        if (hostText.IndexOf(':') >= 0)
        {
            hostText = string.Concat("[", hostText, "]");
        }

        return httpsPort == HttpsDefaultPort
            ? hostText
            : string.Concat(hostText, ":", httpsPort.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Reconstructs the origin-form path and query. The path is preserved verbatim; the query is
    /// rebuilt from the parsed collection (the raw query string is not exposed on
    /// <see cref="IHttpRequest"/>), re-encoding each key and value and following the collection's
    /// enumeration order.
    /// </summary>
    private static string BuildPathAndQuery(IHttpRequest request)
    {
        string path = request.Path.Value;
        IHttpQueryCollection query = request.Query;

        if (query is null || query.Count == 0)
        {
            return path;
        }

        StringBuilder builder = new(path.Length + 16);
        builder.Append(path);

        char separator = '?';
        foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> entry in query)
        {
            builder.Append(separator);
            builder.Append(Uri.EscapeDataString(entry.Key.Value));

            string value = entry.Value.Value;
            if (value.Length != 0)
            {
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(value));
            }

            separator = '&';
        }

        return builder.ToString();
    }
}
