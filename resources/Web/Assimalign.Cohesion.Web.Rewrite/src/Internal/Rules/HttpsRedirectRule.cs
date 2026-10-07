using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// Canonicalizes the scheme: a request whose effective scheme is not <c>https</c> is redirected to the same
/// host, path and query over <c>https</c>, on the configured HTTPS port.
/// </summary>
/// <remarks>
/// The scheme and host are the effective ones (<see cref="HttpContextForwardedExtensions"/>): behind a
/// TLS-terminating proxy that <c>UseForwardedHeaders</c> trusts, the proxy's forwarded <c>https</c> counts
/// as secure, so a proxied deployment never redirects in a loop, and the <c>Location</c> names the host the
/// client addressed. A request without a host this rule can safely echo is left alone.
/// </remarks>
internal sealed class HttpsRedirectRule : RewriteRule
{
    private const int httpsDefaultPort = 443;

    private readonly HttpStatusCode _statusCode;
    private readonly int? _port;

    public HttpsRedirectRule(HttpStatusCode statusCode, int httpsPort)
    {
        _statusCode = statusCode;
        _port = httpsPort == httpsDefaultPort ? null : httpsPort;
    }

    /// <inheritdoc />
    public override void Apply(RewriteContext context)
    {
        IHttpContext exchange = context.HttpContext;

        if (exchange.EffectiveScheme == HttpScheme.Https)
        {
            return;
        }

        if (!exchange.EffectiveHost.TryGetComponents(out ReadOnlySpan<char> host, out _) || !RewriteUrl.IsSafeHostName(host))
        {
            return;
        }

        string location = RewriteUrl.BuildLocation(
            "https",
            RewriteUrl.FormatAuthority(host, _port),
            context.PathBase,
            context.Path,
            context.SerializedQuery);

        context.Redirect(location, _statusCode);
    }
}
