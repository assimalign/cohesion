namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// The <c>Cross-Origin-Opener-Policy</c> response field values (HTML Standard, "Cross-origin opener
/// policies"): whether a top-level document shares its browsing context group with the cross-origin
/// documents it opens or that open it.
/// </summary>
/// <remarks>
/// Opt-in: the default security-headers policy emits no field. <see cref="SameOrigin"/> together with
/// <c>Cross-Origin-Embedder-Policy: require-corp</c> (or <c>credentialless</c>) makes the document
/// cross-origin isolated, which unlocks <c>SharedArrayBuffer</c> and high-resolution timers.
/// </remarks>
public enum CrossOriginOpenerPolicy
{
    /// <summary><c>unsafe-none</c>: no isolation; the document shares its group with any opener or popup.</summary>
    UnsafeNone,

    /// <summary>
    /// <c>same-origin-allow-popups</c>: isolate from cross-origin openers, but keep references to the
    /// popups this document opens (for example an OAuth or payment popup).
    /// </summary>
    SameOriginAllowPopups,

    /// <summary><c>same-origin</c>: share the browsing context group only with same-origin documents.</summary>
    SameOrigin,

    /// <summary>
    /// <c>noopener-allow-popups</c>: always open in a new browsing context group, even from a same-origin
    /// opener, while still allowing this document's own popups to keep a reference to it.
    /// </summary>
    NoopenerAllowPopups,
}
