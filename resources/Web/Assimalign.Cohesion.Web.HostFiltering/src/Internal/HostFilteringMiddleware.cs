using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.HostFiltering.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

/// <summary>
/// The allowed-hosts guard: rejects a request whose effective host does not match the
/// configured allowlist, answering <c>400 Bad Request</c> with an empty body and never
/// invoking the rest of the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The host validated is <see cref="HttpContextForwardedExtensions.EffectiveHost"/>: the host
/// a trusted proxy forwarded when the forwarded-headers middleware ran first and accepted a
/// hop, otherwise the transport-resolved <c>IHttpRequest.Host</c> (HTTP/1.1
/// request-target/<c>Host</c> precedence, HTTP/2 / HTTP/3 <c>:authority</c>). That is the host
/// every downstream consumer — redirects, absolute-URL generation, cache keys — reads, so it is
/// the one the allowlist must bound. Forwarding headers are never read here: a forwarded host
/// is only believed through the forwarded-headers trust model.
/// </para>
/// <para>
/// The middleware performs no parsing of its own at request time: the transports (or the
/// forwarded-headers resolution) already produce a typed <see cref="HttpHost"/>, and the
/// allowlist is precompiled into an <see cref="HttpHostMatcher"/> when <c>UseHostFiltering</c>
/// registers this middleware. Each request costs one feature lookup, one component split, and a
/// handful of span comparisons.
/// </para>
/// <para>
/// This middleware <em>validates</em> the request host; it does not <em>select</em> behavior
/// by host — that is Web routing's job (host-constrained routes). See this package's
/// <c>docs/DESIGN.md</c> for the composition, the registration-order contract, and the
/// ordering interaction with forwarded-headers processing.
/// </para>
/// </remarks>
internal sealed class HostFilteringMiddleware : IWebApplicationMiddleware
{
    private readonly HttpHostMatcher _matcher;
    private readonly bool _allowEmptyHost;

    public HostFilteringMiddleware(HttpHostMatcher matcher, bool allowEmptyHost)
    {
        _matcher = matcher;
        _allowEmptyHost = allowEmptyHost;
    }

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        // The effective host: forwarded by a trusted proxy when UseForwardedHeaders ran first,
        // otherwise the transport-resolved host. Without the forwarded-headers middleware the two
        // are the same value.
        HttpHost host = context.EffectiveHost;

        if (string.IsNullOrWhiteSpace(host.Value))
        {
            // The empty/missing-Host policy (RFC 9112 §3.2): a hostless request cannot be
            // validated against the allowlist, so it passes only when explicitly permitted.
            if (_allowEmptyHost)
            {
                return next.Invoke(context);
            }
        }
        else if (_matcher.IsMatch(host))
        {
            return next.Invoke(context);
        }

        // Reject and short-circuit: 400 with an empty body (the HTTP/1.1 writer synthesizes
        // Content-Length: 0). Rendering a richer problem payload is the application's choice
        // via its own error handling; the guard itself stays dependency-free.
        context.Response.StatusCode = HttpStatusCode.BadRequest;
        return Task.CompletedTask;
    }
}
