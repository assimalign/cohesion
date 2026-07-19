using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.HttpsPolicy;

/// <summary>
/// Configuration for HTTP Strict Transport Security (RFC 6797), applied by the middleware
/// <c>UseHsts</c> registers. The middleware emits a <c>Strict-Transport-Security</c> response field —
/// composed once from these options — on secure responses only, and never on an excluded host.
/// </summary>
/// <remarks>
/// <para>
/// The options are captured once at builder time and the header value is composed once; the middleware
/// resolves nothing per request. Because HSTS instructs a user agent to refuse the plaintext scheme for
/// the whole <see cref="MaxAge"/> window, the excluded-hosts default keeps the policy off loopback
/// authorities where a developer commonly serves plaintext.
/// </para>
/// </remarks>
public sealed class HstsOptions
{
    /// <summary>
    /// Gets or sets the <c>max-age</c> the policy is cached for — how long a user agent will refuse the
    /// plaintext scheme after seeing the header. Defaults to 365 days, the value HSTS deployment
    /// guidance (and the browser preload lists) treat as a production baseline. A value of
    /// <see cref="TimeSpan.Zero"/> emits <c>max-age=0</c>, which tells a user agent to delete a stored
    /// policy (RFC 6797 §6.1.1); a negative value is rejected when the verb is called.
    /// </summary>
    /// <remarks>
    /// The value is emitted as whole seconds (fractional seconds are truncated). A long window is a
    /// commitment: if HTTPS later has to be withdrawn, clients that saw the header stay locked to it
    /// until it expires — start with a short window when first rolling HSTS out.
    /// </remarks>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Gets or sets a value indicating whether the <c>includeSubDomains</c> directive is emitted,
    /// extending the policy to every subdomain of the host. Defaults to <see langword="false"/>.
    /// </summary>
    public bool IncludeSubDomains { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the <c>preload</c> directive is emitted, marking the
    /// host as a candidate for the browser preload lists. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Preload submission requires a <see cref="MaxAge"/> of at least one year and
    /// <see cref="IncludeSubDomains"/> enabled; this package emits the directive faithfully but does not
    /// enforce those submission rules, since <c>preload</c> is not itself an RFC 6797 directive.
    /// </remarks>
    public bool Preload { get; set; }

    /// <summary>
    /// Gets the hosts the policy is never emitted for. Defaults to the loopback authorities
    /// <c>localhost</c>, <c>127.0.0.1</c>, and <c>[::1]</c>. Entries follow the
    /// <c>Assimalign.Cohesion.Http.HttpHostMatcher</c> grammar: exact hosts, <c>*.suffix</c> wildcard
    /// subdomains, or <c>*</c> for match-any; matching is case-insensitive and ignores the request
    /// port. Clear the list to emit the policy on every secure host.
    /// </summary>
    public IList<string> ExcludedHosts { get; } = new List<string>
    {
        "localhost",
        "127.0.0.1",
        "[::1]",
    };
}
