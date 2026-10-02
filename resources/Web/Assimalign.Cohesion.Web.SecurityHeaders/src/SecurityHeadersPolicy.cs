using System;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// The security headers a response carries. A new instance holds the safe defaults:
/// <c>X-Content-Type-Options: nosniff</c>, clickjacking protection
/// (<c>Content-Security-Policy: frame-ancestors 'none'</c> and <c>X-Frame-Options: DENY</c>), and
/// <c>Referrer-Policy: strict-origin-when-cross-origin</c>. Every other header is opt-in.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseSecurityHeaders</c> hands a new instance to its configuration callback and captures a copy of
/// it, so a reference kept by the callback cannot change the pipeline afterward. Endpoint metadata
/// either replaces the policy (<see cref="SecurityHeadersMetadata(SecurityHeadersPolicy)"/>, which also
/// captures a copy) or adjusts a copy of the pipeline's policy
/// (<see cref="SecurityHeadersMetadata(Action{SecurityHeadersPolicy})"/>).
/// </para>
/// <para>
/// Setting a property to <see langword="null"/> (or <see langword="false"/>) means the policy emits no
/// such header. It does not remove a header something else set: the middleware never touches a field it
/// does not emit, and never overwrites one the application set unless
/// <see cref="OverwriteExistingHeaders"/> is <see langword="true"/>.
/// </para>
/// </remarks>
public sealed class SecurityHeadersPolicy
{
    /// <summary>
    /// Creates a policy with the safe defaults.
    /// </summary>
    public SecurityHeadersPolicy()
    {
    }

    /// <summary>
    /// Creates a copy of <paramref name="source"/>. Every member is an immutable value, so the copy is
    /// independent of the source.
    /// </summary>
    internal SecurityHeadersPolicy(SecurityHeadersPolicy source)
    {
        ContentTypeOptions = source.ContentTypeOptions;
        Framing = source.Framing;
        ReferrerPolicy = source.ReferrerPolicy;
        ContentSecurityPolicy = source.ContentSecurityPolicy;
        ContentSecurityPolicyReportOnly = source.ContentSecurityPolicyReportOnly;
        CrossOriginOpenerPolicy = source.CrossOriginOpenerPolicy;
        CrossOriginEmbedderPolicy = source.CrossOriginEmbedderPolicy;
        CrossOriginResourcePolicy = source.CrossOriginResourcePolicy;
        PermissionsPolicy = source.PermissionsPolicy;
        OverwriteExistingHeaders = source.OverwriteExistingHeaders;
    }

    /// <summary>
    /// Gets or sets whether <c>X-Content-Type-Options: nosniff</c> is emitted, which stops user agents from
    /// guessing a response's media type (Fetch Standard, "X-Content-Type-Options"). Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool ContentTypeOptions { get; set; } = true;

    /// <summary>
    /// Gets or sets the clickjacking protection: the <c>frame-ancestors</c> directive and the matching
    /// <c>X-Frame-Options</c> field. Defaults to <see cref="FramingPolicy.Deny"/>; <see langword="null"/>
    /// emits neither.
    /// </summary>
    public FramingPolicy? Framing { get; set; } = FramingPolicy.Deny;

    /// <summary>
    /// Gets or sets the <c>Referrer-Policy</c> field. Defaults to <c>strict-origin-when-cross-origin</c>;
    /// <see langword="null"/> emits no field.
    /// </summary>
    public ReferrerPolicy? ReferrerPolicy { get; set; } = global::Assimalign.Cohesion.Web.SecurityHeaders.ReferrerPolicy.StrictOriginWhenCrossOrigin;

    /// <summary>
    /// Gets or sets the enforced Content Security Policy, emitted as <c>Content-Security-Policy</c> with
    /// the <see cref="Framing"/> directive appended. Defaults to <see langword="null"/> (opt-in), in which
    /// case the field carries the <c>frame-ancestors</c> directive alone.
    /// </summary>
    public ContentSecurityPolicy? ContentSecurityPolicy { get; set; }

    /// <summary>
    /// Gets or sets a Content Security Policy the user agent reports violations of without enforcing it,
    /// emitted as <c>Content-Security-Policy-Report-Only</c>. Use it to trial a policy before enforcing
    /// it. Defaults to <see langword="null"/> (opt-in).
    /// </summary>
    /// <remarks>
    /// The report-only policy never carries the <see cref="Framing"/> directive, so clickjacking
    /// protection stays enforced while a policy is trialed.
    /// </remarks>
    public ContentSecurityPolicy? ContentSecurityPolicyReportOnly { get; set; }

    /// <summary>
    /// Gets or sets the <c>Cross-Origin-Opener-Policy</c> field. Defaults to <see langword="null"/> (opt-in).
    /// </summary>
    public CrossOriginOpenerPolicy? CrossOriginOpenerPolicy { get; set; }

    /// <summary>
    /// Gets or sets the <c>Cross-Origin-Embedder-Policy</c> field. Defaults to <see langword="null"/> (opt-in).
    /// </summary>
    public CrossOriginEmbedderPolicy? CrossOriginEmbedderPolicy { get; set; }

    /// <summary>
    /// Gets or sets the <c>Cross-Origin-Resource-Policy</c> field. Defaults to <see langword="null"/> (opt-in).
    /// </summary>
    public CrossOriginResourcePolicy? CrossOriginResourcePolicy { get; set; }

    /// <summary>
    /// Gets or sets the <c>Permissions-Policy</c> field. Defaults to <see langword="null"/> (opt-in).
    /// </summary>
    public PermissionsPolicy? PermissionsPolicy { get; set; }

    /// <summary>
    /// Gets or sets whether the policy replaces a field the application already set on the response.
    /// Defaults to <see langword="false"/>: a field present when the headers are staged is left as it is.
    /// </summary>
    /// <remarks>
    /// With the default, the application also owns framing once it has set <c>X-Frame-Options</c> or an
    /// enforced <c>Content-Security-Policy</c> that has a <c>frame-ancestors</c> directive: the middleware
    /// then emits neither framing field, so its default cannot contradict the application's choice.
    /// </remarks>
    public bool OverwriteExistingHeaders { get; set; }
}
