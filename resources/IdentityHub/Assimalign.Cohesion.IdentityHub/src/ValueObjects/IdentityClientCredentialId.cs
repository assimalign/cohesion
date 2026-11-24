using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The identifier of one credential of an app registration: the name a secret, certificate, or
/// federated credential is rotated, removed, and reported by.
/// </summary>
/// <remarks>
/// An identifier is 1 to 64 characters from the unreserved set of RFC 3986 section 2.3
/// (letters, digits, <c>-</c>, <c>.</c>, <c>_</c>, <c>~</c>), starting and ending with a letter
/// or digit: <c>primary</c>, <c>2026-11</c>. It is unique within its registration, compares
/// ordinally, and is never a secret: it is what an audit entry names instead of the credential's
/// material. <see langword="default"/> holds no identifier and every credential factory refuses
/// it.
/// </remarks>
public readonly partial struct IdentityClientCredentialId
{
    /// <summary>
    /// The longest credential identifier, in characters.
    /// </summary>
    public const int MaxLength = IdentityClientGrammar.MaxCredentialIdLength;

    /// <summary>
    /// Determines whether a value is a credential identifier.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="message">The reason the identifier is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the credential identifier grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityClientGrammar.IsIdentifier(value, MaxLength))
        {
            message = ErrorMessages.CredentialIdInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
