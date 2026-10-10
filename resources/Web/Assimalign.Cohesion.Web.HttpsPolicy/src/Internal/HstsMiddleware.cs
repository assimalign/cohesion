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
/// Connection security is the <em>effective</em> typed scheme
/// (<see cref="HttpContextForwardedExtensions.EffectiveScheme"/>): the scheme the client used on the
/// outermost trusted hop when the forwarded-headers middleware resolved one, otherwise the
/// transport-derived <see cref="IHttpRequest.Scheme"/> (#763). RFC 6797 §7.2 is about the transport the
/// user agent receives the field over; behind a trusted TLS-terminating proxy that transport is the
/// proxy's TLS leg, so the policy is emitted even though the app-facing hop is plaintext. Nothing here
/// reads a header or a scheme string — without the forwarded-headers trust model the effective scheme is
/// exactly the wire scheme. The excluded-host check reads the effective host for the same reason: it is
/// the authority the policy pins in the user agent (a local proxy dialing <c>localhost</c> must not
/// suppress the policy for the public host).
/// </para>
/// <para>
/// Both reads happen after <c>next</c> returns, so the forwarded identity is visible even when this
/// middleware is registered ahead of <c>UseForwardedHeaders</c>.
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

        // RFC 6797 §7.2: an HSTS host MUST NOT emit the field over a non-secure transport. The
        // effective scheme is the client-facing one: a trusted TLS-terminating proxy's https counts.
        if (context.EffectiveScheme != HttpScheme.Https)
        {
            return;
        }

        // Never assert the policy on an excluded host (loopback by default). No exclusions is a null
        // matcher — the empty-allowlist case that HttpHostMatcher.Create rejects — so guard for it.
        if (_excludedHosts is not null && _excludedHosts.IsMatch(context.EffectiveHost))
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
