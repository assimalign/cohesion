namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents a single normalized claim about an identity subject.
/// </summary>
/// <remarks>
/// Multi-value data (for example SAML multi-value attributes or OIDC array claims that
/// represent collections) is canonically represented as multiple claims sharing one
/// <see cref="Type" />; <see cref="IdentityValueKind.Array" /> values are reserved for
/// genuinely structured single values. See <see cref="IIdentityClaimCollection" /> for the
/// lookup semantics this enables.
/// </remarks>
public interface IIdentityClaim
{
    /// <summary>
    /// Gets the canonical claim type. The canonical vocabulary uses the IANA-registered JWT
    /// short names (see <see cref="IdentityClaimTypes" />); original protocol names survive
    /// in <see cref="Provenance" />.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Gets the typed claim value.
    /// </summary>
    IdentityClaimValue Value { get; }

    /// <summary>
    /// Gets the party that asserted this claim. For claims aggregated from a third-party
    /// claims provider (for example OpenID Connect aggregated claims), this is the resolved
    /// third-party issuer; unresolved claim-source references never enter the canonical
    /// collection as claims.
    /// </summary>
    string? Issuer { get; }

    /// <summary>
    /// Gets the protocol provenance of the claim, when it was normalized from wire data.
    /// </summary>
    IdentityClaimProvenance? Provenance { get; }
}
