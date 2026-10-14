using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// The per-exchange feature the security-headers middleware installs on
/// <see cref="IHttpContext.Features"/>. It carries the Content Security Policy nonce for the response,
/// which a handler stamps on the inline <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> elements a
/// nonce-based policy allows.
/// </summary>
/// <remarks>
/// <para>
/// Resolve it with <c>context.Features.Get&lt;ISecurityHeadersFeature&gt;()</c>. When
/// <c>UseSecurityHeaders</c> is not registered ahead of the handler, the lookup returns
/// <see langword="null"/>.
/// </para>
/// <para>
/// The middleware substitutes <see cref="Nonce"/> into every nonce source
/// (<see cref="ContentSecurityPolicySourceListBuilder.Nonce"/>) of the response's
/// <c>Content-Security-Policy</c> and <c>Content-Security-Policy-Report-Only</c> fields, so the value a
/// handler writes into its markup is the value the policy allows. A nested <c>UseSecurityHeaders</c>
/// reuses the feature an outer registration installed, so one exchange has one nonce.
/// </para>
/// <para>
/// An application may install its own implementation before the middleware runs, for example a test
/// that needs a fixed nonce. The middleware then reads the nonce from that implementation and rejects
/// a value that is not a CSP <c>base64-value</c> with an <see cref="System.InvalidOperationException"/>
/// rather than write it into a header.
/// </para>
/// </remarks>
public interface ISecurityHeadersFeature : IHttpFeature
{
    /// <summary>
    /// Gets the Content Security Policy nonce for the current exchange: 128 bits from a cryptographically
    /// secure random number generator, base64-encoded (W3C Content Security Policy Level 3, "nonce-source").
    /// </summary>
    /// <remarks>
    /// The default implementation generates the value on first read and returns the same value for the
    /// rest of the exchange. A policy without a nonce source never generates one unless a handler reads
    /// this property. Write it verbatim into the element's <c>nonce</c> attribute, for example
    /// <c>&lt;script nonce="…"&gt;</c>; never echo it anywhere an attacker can read it back from the page.
    /// </remarks>
    string Nonce { get; }
}
