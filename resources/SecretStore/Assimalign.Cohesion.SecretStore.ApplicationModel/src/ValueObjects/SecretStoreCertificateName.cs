using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>A certificate's unprefixed storage name, consisting of one nonblank segment.</summary>
/// <remarks>Names use ordinal storage identity; they are not DNS names and preserve their case.</remarks>
public partial struct SecretStoreCertificateName
{
    /// <summary>Checks a leaf name without the <c>certs/</c> reference prefix.</summary>
    /// <param name="value">The leaf name.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> on success.</param>
    /// <returns>Whether the name is a nonblank segment other than dot or dot-dot.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        bool valid = SecretStoreCommandGrammar.IsValidCertificateName(value);
        message = valid ? null : ErrorMessages.CertificateNameInvalid;
        return valid;
    }
}
