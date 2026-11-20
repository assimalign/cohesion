using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The identifier of an app registration: the OAuth <c>client_id</c> a client presents, and the
/// name another registration uses to refer to it.
/// </summary>
/// <remarks>
/// <para>
/// An identifier is 1 to 128 characters from the unreserved set of RFC 3986 section 2.3
/// (letters, digits, <c>-</c>, <c>.</c>, <c>_</c>, <c>~</c>), starting and ending with a letter
/// or digit: <c>orders-api</c>, <c>billing.worker</c>, or a GUID. It therefore needs no
/// escaping in a URL path segment, a form body, or HTTP Basic authentication, where RFC 6749
/// section 2.3.1 would otherwise have a client encode it.
/// </para>
/// <para>
/// Identifiers compare ordinally, as RFC 6749 treats a client identifier as an exact string.
/// A registration defined in code names its own identifier, because the identifier has to be
/// the same in every deployment. An identifier is not a secret.
/// <see langword="default"/> holds no identifier and every registration boundary refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityClientId
{
    /// <summary>
    /// The longest client identifier, in characters.
    /// </summary>
    public const int MaxLength = IdentityClientGrammar.MaxIdLength;

    /// <summary>
    /// Determines whether a value is a client identifier.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="message">The reason the identifier is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the client identifier grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityClientGrammar.IsIdentifier(value, MaxLength))
        {
            message = ErrorMessages.ClientIdInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
