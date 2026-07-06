namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the declared use restriction of a <see cref="ProtocolKey" />.
/// </summary>
/// <remarks>
/// <see cref="Unspecified" /> deliberately means "no restriction declared" — both SAML
/// (absent <c>use</c> attribute) and JWK (absent <c>use</c> member) define absence as
/// applicability to any purpose, so the descriptive model preserves that wire semantic
/// rather than importing the family's fail-closed <c>Unknown</c> reading. Whether an
/// unrestricted key is actually trusted for a given operation is downstream validator
/// policy, not data. Use the <see cref="ProtocolKey.CanSign" /> and
/// <see cref="ProtocolKey.CanEncrypt" /> helpers instead of comparing against
/// <see cref="Signing" /> or <see cref="Encryption" /> directly, so unspecified keys are
/// not accidentally filtered out.
/// </remarks>
public enum ProtocolKeyUse
{
    /// <summary>
    /// No use restriction was declared; the key is applicable to any purpose per SAML
    /// absent-<c>use</c> and JWK absent-<c>use</c> semantics.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The key is declared for signing and signature verification.
    /// </summary>
    Signing = 1,

    /// <summary>
    /// The key is declared for encryption.
    /// </summary>
    Encryption = 2
}
