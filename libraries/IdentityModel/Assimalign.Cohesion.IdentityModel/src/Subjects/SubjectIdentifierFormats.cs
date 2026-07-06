namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Defines well-known subject identifier formats. SAML-defined formats use the exact OASIS
/// URIs so SAML-sourced identifiers round-trip losslessly; OpenID Connect subject types use
/// their spec tokens because OIDC defines no format URIs. Format strings compare ordinally.
/// </summary>
public static class SubjectIdentifierFormats
{
    /// <summary>
    /// The unspecified format (SAML 1.1 URI, carried forward by SAML 2.0 Core §8.3.1). This
    /// is the normal form for identifiers with no declared format.
    /// </summary>
    public const string Unspecified = "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified";

    /// <summary>
    /// The email address format (SAML 1.1 URI, carried forward by SAML 2.0 Core §8.3.2).
    /// </summary>
    public const string EmailAddress = "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress";

    /// <summary>
    /// The X.509 subject name format (SAML 1.1 URI).
    /// </summary>
    public const string X509SubjectName = "urn:oasis:names:tc:SAML:1.1:nameid-format:X509SubjectName";

    /// <summary>
    /// The Windows domain qualified name format (SAML 1.1 URI).
    /// </summary>
    public const string WindowsDomainQualifiedName = "urn:oasis:names:tc:SAML:1.1:nameid-format:WindowsDomainQualifiedName";

    /// <summary>
    /// The Kerberos principal name format (SAML 2.0 URI).
    /// </summary>
    public const string KerberosPrincipalName = "urn:oasis:names:tc:SAML:2.0:nameid-format:kerberos";

    /// <summary>
    /// The entity identifier format for system entities such as providers (SAML 2.0 URI).
    /// </summary>
    public const string EntityIdentifier = "urn:oasis:names:tc:SAML:2.0:nameid-format:entity";

    /// <summary>
    /// The persistent pseudonymous identifier format (SAML 2.0 URI).
    /// </summary>
    public const string Persistent = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";

    /// <summary>
    /// The transient identifier format (SAML 2.0 URI).
    /// </summary>
    public const string Transient = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient";

    /// <summary>
    /// The OpenID Connect public subject type: the same subject value is issued to every
    /// client.
    /// </summary>
    public const string Public = "public";

    /// <summary>
    /// The OpenID Connect pairwise subject type: a distinct subject value is issued per
    /// sector. Pairwise identifiers should carry the sector on
    /// <see cref="SubjectIdentifier.RelyingPartyQualifier" />.
    /// </summary>
    public const string Pairwise = "pairwise";

    /// <summary>
    /// An OAuth 2.0 client identifier. Used for application principals and for actor
    /// entries (for example an RFC 8693 <c>act</c> claim that identifies the acting party
    /// only by <c>client_id</c>).
    /// </summary>
    public const string ClientIdentifier = "client_id";
}
