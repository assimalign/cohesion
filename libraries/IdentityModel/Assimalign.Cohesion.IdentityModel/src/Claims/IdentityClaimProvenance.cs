namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Records where a normalized claim or attribute came from. Normalization into the canonical
/// model never erases the source: the original protocol identifiers survive here so auditing,
/// interop, and provider-migration scenarios keep their source meaning.
/// </summary>
public sealed class IdentityClaimProvenance
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityClaimProvenance" /> class.
    /// </summary>
    /// <param name="protocol">The protocol the claim was sourced from.</param>
    /// <param name="originalType">The original wire-level claim or attribute name before canonical mapping.</param>
    /// <param name="originalIssuer">The original asserting party the claim was sourced from.</param>
    /// <param name="originalValueType">The original wire-level value type (for example a SAML <c>xsi:type</c>).</param>
    /// <param name="originalNameFormat">The original attribute name format (for example a SAML attribute <c>NameFormat</c> URI).</param>
    /// <param name="originalFriendlyName">The original human-readable attribute name (for example a SAML attribute <c>FriendlyName</c>).</param>
    public IdentityClaimProvenance(
        AuthenticationProtocol protocol,
        string? originalType = null,
        string? originalIssuer = null,
        string? originalValueType = null,
        string? originalNameFormat = null,
        string? originalFriendlyName = null)
    {
        Protocol = protocol;
        OriginalType = originalType;
        OriginalIssuer = originalIssuer;
        OriginalValueType = originalValueType;
        OriginalNameFormat = originalNameFormat;
        OriginalFriendlyName = originalFriendlyName;
    }

    /// <summary>
    /// Gets the protocol the claim was sourced from.
    /// </summary>
    public AuthenticationProtocol Protocol { get; }

    /// <summary>
    /// Gets the original wire-level claim or attribute name before canonical mapping.
    /// </summary>
    public string? OriginalType { get; }

    /// <summary>
    /// Gets the original asserting party the claim was sourced from.
    /// </summary>
    public string? OriginalIssuer { get; }

    /// <summary>
    /// Gets the original wire-level value type (for example a SAML <c>xsi:type</c>).
    /// </summary>
    public string? OriginalValueType { get; }

    /// <summary>
    /// Gets the original attribute name format (for example a SAML attribute
    /// <c>NameFormat</c> URI). This is deliberately distinct from
    /// <see cref="OriginalValueType" /> because both occur together on real attributes.
    /// </summary>
    public string? OriginalNameFormat { get; }

    /// <summary>
    /// Gets the original human-readable attribute name (for example a SAML attribute
    /// <c>FriendlyName</c>).
    /// </summary>
    public string? OriginalFriendlyName { get; }
}
