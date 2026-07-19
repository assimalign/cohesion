using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.HttpsPolicy.Internal;

/// <summary>
/// The HTTP Strict Transport Security emitter (RFC 6797): stamps the <c>Strict-Transport-Security</c>
/// field — composed once at registration — onto secure responses, and only those. The policy is never
/// emitted over a plaintext transport (RFC 6797 §7.2) nor on an excluded host (loopback by default).
/// </summary>
/// <remarks>
/// <para>
/// Connection security is read from <see cref="IHttpRequest.Scheme"/> — the transport-derived typed
/// scheme resolved from the listener's transport-security capability (#763) — not from any header or
/// scheme string.
/// </para>
/// <para>
/// The field is applied <em>after</em> the pipeline unwinds (post-<c>next</c>). That is deliberate: the
/// #881 exception boundary clears response headers when it renders a fresh error response for a faulted
/// request, so a header set before <c>next</c> would be wiped; applying it afterward means a reset error
/// response served over TLS still carries the policy. The one response that cannot receive it is one
/// whose head has already been committed to the wire (a started/streamed response) — detected through
/// <see cref="IHttpHeaderCollection.IsReadOnly"/> and skipped rather than faulted, since a committed head
/// can carry no new field regardless of when it is set.
/// </para>
/// </remarks>
internal sealed class HstsMiddleware : IWebApplicationMiddleware
{
    private readonly string _headerValue;
    private readonly HttpHostMatcher? _excludedHosts;

    public HstsMiddleware(string headerValue, HttpHostMatcher? excludedHosts)
    {
        _headerValue = headerValue;
        _excludedHosts = excludedHosts;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await next.Invoke(context).ConfigureAwait(false);

        // RFC 6797 §7.2: an HSTS host MUST NOT emit the field over a non-secure transport.
        if (context.Request.Scheme != HttpScheme.Https)
        {
            return;
        }

        // Never assert the policy on an excluded host (loopback by default). No exclusions is a null
        // matcher — the empty-allowlist case that HttpHostMatcher.Create rejects — so guard for it.
        if (_excludedHosts is not null && _excludedHosts.IsMatch(context.Request.Host))
        {
            return;
        }

        IHttpHeaderCollection headers = context.Response.Headers;
        if (!headers.IsReadOnly)
        {
            headers[HttpHeaderKey.StrictTransportSecurity] = _headerValue;
        }
    }
}
