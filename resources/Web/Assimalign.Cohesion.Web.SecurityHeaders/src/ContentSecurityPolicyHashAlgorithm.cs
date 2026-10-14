namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// The digest algorithms a Content Security Policy hash source may name (W3C Content Security Policy
/// Level 3, "hash-algorithm").
/// </summary>
public enum ContentSecurityPolicyHashAlgorithm
{
    /// <summary>SHA-256, serialized as <c>sha256</c>.</summary>
    Sha256,

    /// <summary>SHA-384, serialized as <c>sha384</c>.</summary>
    Sha384,

    /// <summary>SHA-512, serialized as <c>sha512</c>.</summary>
    Sha512,
}
