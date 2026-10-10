using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// Canonicalizes the host: redirects to the <c>www.</c> form of the host, or to the form without it, keeping
/// the effective scheme, the port, the path and the query.
/// </summary>
/// <remarks>
/// <para>
/// The host is the effective one (<see cref="HttpContextForwardedExtensions.EffectiveHost"/>), the host the
/// client addressed behind a trusted proxy. <c>localhost</c>, <c>*.localhost</c> and IP literals are never
/// redirected, and when domains are configured only a request for one of them is.
/// </para>
/// <para>
/// The rule is idempotent: the target of its redirect is a host it leaves alone, so it cannot loop.
/// </para>
/// </remarks>
internal sealed class HostRedirectRule : RewriteRule
{
    private const string wwwPrefix = "www.";

    private readonly bool _toWww;
    private readonly HttpStatusCode _statusCode;
    private readonly string[] _domains;

    public HostRedirectRule(bool toWww, HttpStatusCode statusCode, string[] domains)
    {
        _toWww = toWww;
        _statusCode = statusCode;
        _domains = domains;
    }

    /// <inheritdoc />
    public override void Apply(RewriteContext context)
    {
        IHttpContext exchange = context.HttpContext;

        if (!exchange.EffectiveHost.TryGetComponents(out ReadOnlySpan<char> host, out int? port)
            || !RewriteUrl.IsSafeHostName(host)
            || IsLocalOrAddress(host)
            || !IsConfiguredDomain(host))
        {
            return;
        }

        bool hasWww = host.StartsWith(wwwPrefix, StringComparison.OrdinalIgnoreCase);

        if (_toWww == hasWww)
        {
            return;
        }

        ReadOnlySpan<char> target = _toWww ? string.Concat(wwwPrefix, host) : host[wwwPrefix.Length..];

        // "www." alone has no host to strip the prefix down to.
        if (target.IsEmpty)
        {
            return;
        }

        string scheme = exchange.EffectiveScheme == HttpScheme.Https ? "https" : "http";
        string location = RewriteUrl.BuildLocation(
            scheme,
            RewriteUrl.FormatAuthority(target, port),
            context.PathBase,
            context.Path,
            context.SerializedQuery);

        context.Redirect(location, _statusCode);
    }

    private static bool IsLocalOrAddress(ReadOnlySpan<char> host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host, out _);

    private bool IsConfiguredDomain(ReadOnlySpan<char> host)
    {
        if (_domains.Length == 0)
        {
            return true;
        }

        foreach (string domain in _domains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
