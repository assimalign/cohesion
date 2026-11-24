using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Proxy;

/// <summary>
/// One rule of the forward proxy: destinations, by host and port, that its clients may reach.
/// </summary>
/// <remarks>
/// <para>
/// The forward proxy allows a request when at least one rule matches its target's host and port,
/// and answers 403 Forbidden otherwise: no rule, no access.
/// </para>
/// <para>
/// A destination allowed only through a wildcard pattern (<c>*.example.com</c> or <c>*</c>) may not
/// resolve to a loopback, link-local or unspecified address, so a name its owner points at the
/// proxy's own host or at a cloud metadata service (<c>169.254.169.254</c>) cannot reach it. The
/// address is checked when the proxy connects, so it is the address the proxy uses. A destination an
/// exact pattern allows is reached wherever it resolves: the operator named it.
/// </para>
/// <para>
/// A rule is an immutable value. The options and the <c>forward-proxy</c> setting copy its lists when
/// they take it, so changing a list afterwards has no effect.
/// </para>
/// </remarks>
public sealed record ForwardProxyRule
{
    /// <summary>
    /// Gets the host patterns the target's host must match: an exact host (<c>api.example.com</c>,
    /// <c>10.0.0.5</c>, <c>[::1]</c>), a wildcard subdomain (<c>*.example.com</c>, which does not match
    /// <c>example.com</c> itself), or <c>*</c> for any host. At least one is required.
    /// </summary>
    /// <remarks>
    /// The host matched is the request's target: the authority of an absolute-form request
    /// (<c>GET http://example.com/ HTTP/1.1</c>) or of a <c>CONNECT</c>. A forwarded host never
    /// changes where the forward proxy connects.
    /// </remarks>
    public required IReadOnlyList<string> Hosts { get; init; }

    /// <summary>
    /// Gets the ports the target may use, each from 1 to 65535. Empty, the default, allows port 80
    /// for an <c>http</c> request and port 443 for an <c>https</c> request and a <c>CONNECT</c>.
    /// </summary>
    public IReadOnlyList<int> Ports { get; init; } = [];
}
