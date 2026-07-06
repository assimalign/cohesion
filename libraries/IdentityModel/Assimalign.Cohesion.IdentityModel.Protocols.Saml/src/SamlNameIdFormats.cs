namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Defines the SAML 2.0 NameID format URIs, for discoverability under the SAML namespace.
/// These forward to <see cref="SubjectIdentifierFormats" /> so there is exactly one literal
/// per URI — the canonical <see cref="SubjectIdentifier.Format" /> participates in equality,
/// so a second copy of these strings would be a drift hazard.
/// </summary>
public static class SamlNameIdFormats
{
    /// <summary>The unspecified NameID format.</summary>
    public const string Unspecified = SubjectIdentifierFormats.Unspecified;

    /// <summary>The email address NameID format.</summary>
    public const string EmailAddress = SubjectIdentifierFormats.EmailAddress;

    /// <summary>The X.509 subject name NameID format.</summary>
    public const string X509SubjectName = SubjectIdentifierFormats.X509SubjectName;

    /// <summary>The Windows domain qualified name NameID format.</summary>
    public const string WindowsDomainQualifiedName = SubjectIdentifierFormats.WindowsDomainQualifiedName;

    /// <summary>The Kerberos principal name NameID format.</summary>
    public const string Kerberos = SubjectIdentifierFormats.KerberosPrincipalName;

    /// <summary>The entity identifier NameID format (for system entities such as providers).</summary>
    public const string Entity = SubjectIdentifierFormats.EntityIdentifier;

    /// <summary>The persistent pseudonymous NameID format.</summary>
    public const string Persistent = SubjectIdentifierFormats.Persistent;

    /// <summary>The transient NameID format.</summary>
    public const string Transient = SubjectIdentifierFormats.Transient;
}
