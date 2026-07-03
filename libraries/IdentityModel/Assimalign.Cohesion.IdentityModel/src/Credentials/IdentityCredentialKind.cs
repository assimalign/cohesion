namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents the kind of credential an <see cref="IdentityCredential" /> describes. The
/// member values are stable and additive-only.
/// </summary>
public enum IdentityCredentialKind
{
    /// <summary>
    /// The credential kind is not known.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A password or other memorized secret.
    /// </summary>
    Password = 1,

    /// <summary>
    /// An X.509 certificate.
    /// </summary>
    Certificate = 2,

    /// <summary>
    /// A symmetric or asymmetric key (for example an API key or a client secret key).
    /// </summary>
    Key = 3,

    /// <summary>
    /// A token or assertion issued by a federated provider.
    /// </summary>
    FederatedToken = 4,

    /// <summary>
    /// A one-time code (for example an OTP or a recovery code).
    /// </summary>
    OneTimeCode = 5,

    /// <summary>
    /// A WebAuthn / FIDO2 passkey.
    /// </summary>
    Passkey = 6
}
