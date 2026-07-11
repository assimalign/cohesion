using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

// The BCL message-pipeline types, not the Cohesion protocol value objects this namespace
// otherwise surfaces — the client factory rides System.Net.Http end to end.
using HttpMethod = System.Net.Http.HttpMethod;
using HttpStatusCode = System.Net.HttpStatusCode;

/// <summary>
/// The factory-owned automatic-redirect layer (RFC 9110 &#167; 15.4, RFC 10008 &#167; 2.5).
/// Follows <c>301</c>/<c>302</c>/<c>303</c>/<c>307</c>/<c>308</c> responses, re-issuing the
/// request with its method and content preserved except where the protocol sanctions a switch:
/// <c>303 See Other</c> is fulfilled with a GET (the one intended method change for QUERY,
/// RFC 10008 &#167; 2.5.3), and the historical POST&#8594;GET rewrite is preserved for POST on
/// <c>301</c>/<c>302</c>. Every other method — QUERY in particular — is never downgraded to GET.
/// </summary>
/// <remarks>
/// <para>
/// Owning the policy here (rather than on the inner <see cref="SocketsHttpHandler"/>) makes the
/// redirect semantics uniform across every handler the factory pools — including test-injected
/// ones — and testable without a wire. The factory disables the inner handler's own redirect
/// following so exactly one layer acts.
/// </para>
/// <para>
/// Mirrored safety behavior: the <c>Authorization</c> field is dropped on every hop (credentials
/// are not forwarded across an automatic redirect), an <c>https</c>&#8594;<c>http</c> downgrade is
/// never followed, a hop past the configured cap stops following and surfaces the last <c>3xx</c>,
/// and <c>300 Multiple Choices</c> is never followed automatically (choosing among alternatives
/// is the caller's decision). Preserved content is re-serialized from the same
/// <see cref="HttpContent"/> instance, so one-shot contents (e.g. <see cref="StreamContent"/>)
/// cannot ride an automatic redirect — the same constraint the BCL's built-in handling carries.
/// </para>
/// </remarks>
internal sealed class RedirectHttpMessageHandler : DelegatingHandler
{
    private readonly int _maxAutomaticRedirections;

    public RedirectHttpMessageHandler(HttpMessageHandler innerHandler, int maxAutomaticRedirections)
        : base(innerHandler)
    {
        _maxAutomaticRedirections = maxAutomaticRedirections;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        int redirectCount = 0;
        Uri? location;
        while ((location = GetRedirectTarget(request, response)) is not null)
        {
            if (++redirectCount > _maxAutomaticRedirections)
            {
                // Stop following; the caller receives the redirect it can act on itself.
                break;
            }

            HttpStatusCode statusCode = response.StatusCode;
            response.Dispose();

            request.RequestUri = location;

            // RFC 9110 §15.4 — credentials are not forwarded across an automatic redirect.
            request.Headers.Authorization = null;

            if (RequiresForceGet(statusCode, request.Method))
            {
                request.Method = HttpMethod.Get;
                request.Content = null;
                if (request.Headers.TransferEncodingChunked == true)
                {
                    request.Headers.TransferEncodingChunked = false;
                }
            }

            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static Uri? GetRedirectTarget(HttpRequestMessage request, HttpResponseMessage response)
    {
        switch ((int)response.StatusCode)
        {
            case 301 or 302 or 303 or 307 or 308:
                break;
            default:
                return null;
        }

        Uri? location = response.Headers.Location;
        Uri? requestUri = request.RequestUri;
        if (location is null || requestUri is null)
        {
            return null;
        }

        if (!location.IsAbsoluteUri)
        {
            location = new Uri(requestUri, location);
        }

        // Never step down from https to http on the caller's behalf.
        if (string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(location.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return location;
    }

    private static bool RequiresForceGet(HttpStatusCode statusCode, HttpMethod method)
        => statusCode switch
        {
            // RFC 9110 §15.4.4 — 303 See Other is fulfilled with a GET on the Location URI; for
            // QUERY this is the one sanctioned method switch (RFC 10008 §2.5.3).
            HttpStatusCode.SeeOther => method != HttpMethod.Get && method != HttpMethod.Head,

            // RFC 9110 §15.4.2 / §15.4.3 — the historical rewrite on 301/302 applies to POST
            // only. Every other method — QUERY per RFC 10008 §2.5 — keeps method and content.
            HttpStatusCode.Moved or HttpStatusCode.Found => method == HttpMethod.Post,

            _ => false,
        };
}
