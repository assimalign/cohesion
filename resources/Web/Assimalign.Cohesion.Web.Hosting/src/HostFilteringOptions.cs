using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Hosting;

/// <summary>
/// Builder-time configuration for allowed-hosts enforcement: an allowlist of host patterns the
/// request's effective host must match, applied as the request pipeline's first-position
/// middleware. A defense against Host-header injection (cache poisoning, password-reset
/// poisoning, absolute-URL generation against an attacker-chosen host).
/// </summary>
/// <remarks>
/// <para>
/// Host filtering is opt-in: while <see cref="AllowedHosts"/> is empty (the default) no
/// filtering middleware is installed and every host is accepted. Adding one or more patterns
/// compiles the allowlist into a matcher once, when the pipeline is built; requests whose
/// transport-resolved host does not match are rejected with <c>400 Bad Request</c> before any
/// other middleware runs.
/// </para>
/// <para>
/// Patterns follow the <c>Assimalign.Cohesion.Http</c> host-matcher grammar: exact hosts
/// (<c>example.com</c>, <c>127.0.0.1</c>, <c>[::1]</c> or <c>::1</c>), wildcard subdomains
/// (<c>*.example.com</c> — any depth, apex excluded), or <c>*</c> for match-any. Matching is
/// case-insensitive and ignores the request's port; a pattern carrying a port fails at
/// pipeline build rather than silently never matching.
/// </para>
/// </remarks>
public sealed class HostFilteringOptions
{
    /// <summary>
    /// Gets the allowlist of host patterns. Empty by default, which disables host filtering
    /// entirely (match-any). Invalid patterns fail when the pipeline is built.
    /// </summary>
    public IList<string> AllowedHosts { get; } = new List<string>();

    /// <summary>
    /// Gets or sets a value indicating whether a request with an empty or missing effective
    /// host is allowed through when host filtering is active. Defaults to
    /// <see langword="false"/>: RFC 9112 §3.2 requires an HTTP/1.1 request to carry a
    /// <c>Host</c> header (and HTTP/2 / HTTP/3 requests carry <c>:authority</c>), so a
    /// hostless request cannot be validated and is rejected with <c>400 Bad Request</c>.
    /// Set to <see langword="true"/> only when legacy HTTP/1.0-style clients that send no
    /// <c>Host</c> header must be served.
    /// </summary>
    public bool AllowEmptyHost { get; set; }
}
