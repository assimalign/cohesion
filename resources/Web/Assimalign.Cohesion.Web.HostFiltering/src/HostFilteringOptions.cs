using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.HostFiltering;

/// <summary>
/// Configuration for allowed-hosts enforcement: an allowlist of host patterns the request's
/// effective host must match, applied by the middleware <c>UseHostFiltering</c> registers.
/// A defense against Host-header injection (cache poisoning, password-reset poisoning,
/// absolute-URL generation against an attacker-chosen host).
/// </summary>
/// <remarks>
/// <para>
/// The allowlist is compiled into an <c>Assimalign.Cohesion.Http.HttpHostMatcher</c> exactly
/// once, when <c>UseHostFiltering</c> is called (builder time); requests whose
/// transport-resolved host does not match are rejected with <c>400 Bad Request</c> before any
/// later middleware runs. Invalid patterns — and an empty allowlist, which would silently
/// reject every request — throw from the registration call, never from a request.
/// </para>
/// <para>
/// Patterns follow the <c>HttpHostMatcher</c> grammar: exact hosts (<c>example.com</c>,
/// <c>127.0.0.1</c>, <c>[::1]</c> or <c>::1</c>), wildcard subdomains (<c>*.example.com</c> —
/// any depth, apex excluded), or <c>*</c> for match-any. Matching is case-insensitive and
/// ignores the request's port; a pattern carrying a port fails at registration rather than
/// silently never matching.
/// </para>
/// </remarks>
public sealed class HostFilteringOptions
{
    /// <summary>
    /// Gets the allowlist of host patterns. At least one pattern is required — to accept
    /// every host, pass <c>*</c> explicitly (which keeps the empty-host policy enforced) or
    /// do not register the middleware at all.
    /// </summary>
    public IList<string> AllowedHosts { get; } = new List<string>();

    /// <summary>
    /// Gets or sets a value indicating whether a request with an empty or missing effective
    /// host is allowed through. Defaults to <see langword="false"/>: RFC 9112 §3.2 requires
    /// an HTTP/1.1 request to carry a <c>Host</c> header (and HTTP/2 / HTTP/3 requests carry
    /// <c>:authority</c>), so a hostless request cannot be validated and is rejected with
    /// <c>400 Bad Request</c>. Set to <see langword="true"/> only when legacy
    /// HTTP/1.0-style clients that send no <c>Host</c> header must be served.
    /// </summary>
    public bool AllowEmptyHost { get; set; }
}
