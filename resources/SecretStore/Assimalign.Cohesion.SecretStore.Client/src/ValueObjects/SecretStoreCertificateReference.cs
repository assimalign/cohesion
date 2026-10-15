using Assimalign.Cohesion.SecretStore.Client.Internal;

namespace Assimalign.Cohesion.SecretStore.Client;

/// <summary>
/// A SecretStore certificate retrieval reference: exactly <c>ca/root</c>, or
/// <c>certs/</c> followed by a nonblank leaf name containing neither slash nor
/// backslash and unequal to <c>.</c> or <c>..</c>.
/// </summary>
/// <remarks>
/// SecretStore protocol references are case-sensitive storage identifiers, so equality
/// uses ordinal comparison and preserves spelling. String conversion validates before
/// a request is sent. <see langword="default"/> has no reference and is refused by the client.
/// </remarks>
public partial struct SecretStoreCertificateReference
{
    /// <summary>Determines whether a value is a supported certificate retrieval reference.</summary>
    /// <param name="value">The candidate reference.</param>
    /// <param name="message">The refusal reason, or <see langword="null"/> when valid.</param>
    /// <returns>Whether the reference has the case-sensitive SecretStore protocol form.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (!SecretStoreCertificateReferenceGrammar.IsValid(value))
        {
            message = ErrorMessages.CertificateReferenceInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
