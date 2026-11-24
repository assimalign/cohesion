using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The name an app registration's identifier is derived from: what code calls the application,
/// such as <c>orders-api</c>.
/// </summary>
/// <remarks>
/// <para>
/// A name is 1 to 128 characters from the unreserved set of RFC 3986 section 2.3 (letters,
/// digits, <c>-</c>, <c>.</c>, <c>_</c>, <c>~</c>), starting and ending with a letter or digit:
/// <c>orders-api</c>, <c>billing.worker</c>.
/// </para>
/// <para>
/// Names compare case-insensitively: <c>Orders-API</c> and <c>orders-api</c> are one name and
/// derive one identifier, so no two registrations differ only by the case of their name. That
/// is this hub's rule, not a specification's: the OAuth <c>client_id</c> is the identifier, and
/// the name never travels in a protocol message.
/// </para>
/// <para>
/// A name is an input to <see cref="IdentityClientId.FromName"/> and is not kept on the
/// registration, which shows a person its <see cref="IdentityClient.DisplayName"/>.
/// <see langword="default"/> holds no name and <see cref="IdentityClientId.FromName"/> refuses
/// it.
/// </para>
/// </remarks>
public readonly partial struct IdentityClientName
{
    /// <summary>
    /// The longest client name, in characters.
    /// </summary>
    public const int MaxLength = IdentityClientGrammar.MaxNameLength;

    /// <summary>
    /// Determines whether a value is a client name.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the client name grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityClientGrammar.IsIdentifier(value, MaxLength))
        {
            message = ErrorMessages.ClientNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
