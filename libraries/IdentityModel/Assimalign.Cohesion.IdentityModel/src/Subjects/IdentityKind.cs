namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents the kind of principal an identity subject describes.
/// </summary>
/// <remarks>
/// The member values are stable and additive-only. <see cref="Unknown" /> is the default and
/// is a legitimate value: protocol data such as an OAuth 2.0 token-exchange <c>act</c> claim
/// or a SAML delegation entry identifies an actor without declaring whether it is a user or
/// an application, and normalizers must not fabricate a kind in that case.
/// </remarks>
public enum IdentityKind
{
    /// <summary>
    /// The kind of principal is not known.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The identity represents a user principal.
    /// </summary>
    User = 1,

    /// <summary>
    /// The identity represents an application principal.
    /// </summary>
    Application = 2
}
