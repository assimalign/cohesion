namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// The <c>Referrer-Policy</c> response field values (W3C Referrer Policy, §3): how much of the document's
/// URL a user agent sends in the <c>Referer</c> request field when the page navigates or fetches.
/// </summary>
/// <remarks>
/// The default security-headers policy emits <see cref="StrictOriginWhenCrossOrigin"/>, which is also
/// the browsers' own default; stating it explicitly protects older user agents whose default leaked the
/// full URL. Set <see cref="SecurityHeadersPolicy.ReferrerPolicy"/> to <see langword="null"/> to emit no
/// field.
/// </remarks>
public enum ReferrerPolicy
{
    /// <summary><c>no-referrer</c>: never send a <c>Referer</c> field.</summary>
    NoReferrer,

    /// <summary>
    /// <c>no-referrer-when-downgrade</c>: send the full URL, except from a secure page to a
    /// non-secure destination.
    /// </summary>
    NoReferrerWhenDowngrade,

    /// <summary><c>origin</c>: send only the origin, to every destination.</summary>
    Origin,

    /// <summary>
    /// <c>origin-when-cross-origin</c>: send the full URL to the same origin and only the origin to
    /// other origins.
    /// </summary>
    OriginWhenCrossOrigin,

    /// <summary><c>same-origin</c>: send the full URL to the same origin and nothing to other origins.</summary>
    SameOrigin,

    /// <summary>
    /// <c>strict-origin</c>: send only the origin, and nothing from a secure page to a non-secure
    /// destination.
    /// </summary>
    StrictOrigin,

    /// <summary>
    /// <c>strict-origin-when-cross-origin</c>: send the full URL to the same origin, only the origin to
    /// other origins, and nothing from a secure page to a non-secure destination. The default.
    /// </summary>
    StrictOriginWhenCrossOrigin,

    /// <summary>
    /// <c>unsafe-url</c>: send the full URL to every destination, including from a secure page to a
    /// non-secure one. Leaks paths and query strings; avoid it.
    /// </summary>
    UnsafeUrl,
}
