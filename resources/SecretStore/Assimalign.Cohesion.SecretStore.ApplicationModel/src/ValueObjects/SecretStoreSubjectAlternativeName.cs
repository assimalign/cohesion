using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>A certificate subject alternative name accepted as an IP address or certificate DNS name.</summary>
/// <remarks>
/// DNS names compare without case per RFC 1035 section 2.3.3. RFC 5280 section 4.2.1.6
/// specifies their SAN representation; certificate DNS validation preserves BCL IDN and wildcard support.
/// </remarks>
public partial struct SecretStoreSubjectAlternativeName
{
    /// <summary>Checks the IP or certificate DNS/IDN grammar without issuing a certificate.</summary>
    /// <param name="value">The alternative name.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> on success.</param>
    /// <returns>Whether the alternative name can be encoded for certificate issuance.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        bool valid = SecretStoreCommandGrammar.IsValidSubjectAlternativeName(value);
        message = valid ? null : ErrorMessages.SubjectAlternativeNameInvalid;
        return valid;
    }
}
