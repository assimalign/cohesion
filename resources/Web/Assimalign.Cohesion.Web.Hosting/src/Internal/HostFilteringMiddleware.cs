using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

/// <summary>
/// The first-position allowed-hosts guard: rejects a request whose transport-resolved host
/// (HTTP/1.1 request-target/<c>Host</c> precedence, HTTP/2 / HTTP/3 <c>:authority</c>) does
/// not match the configured allowlist, answering <c>400 Bad Request</c> with an empty body
/// and never invoking the rest of the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The middleware performs no parsing of its own at request time: the transports already
/// resolve the effective host with the correct per-version precedence onto
/// <c>IHttpRequest.Host</c>, and the allowlist is precompiled into an
/// <see cref="IHttpHostMatcher"/> when the pipeline is built. Each request costs one component
/// split and a handful of span comparisons.
/// </para>
/// <para>
/// This middleware <em>validates</em> the request host; it does not <em>select</em> behavior
/// by host — that is Web routing's job (host-constrained routes). See the Web.Hosting
/// <c>docs/DESIGN.md</c> for the composition and for the ordering interaction with
/// forwarded-headers processing.
/// </para>
/// </remarks>
internal sealed class HostFilteringMiddleware : IWebApplicationMiddleware
{
    private readonly IHttpHostMatcher _matcher;
    private readonly bool _allowEmptyHost;

    public HostFilteringMiddleware(IHttpHostMatcher matcher, bool allowEmptyHost)
    {
        _matcher = matcher;
        _allowEmptyHost = allowEmptyHost;
    }

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        HttpHost host = context.Request.Host;

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
