using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The name a user signs in with, such as <c>ada</c> or <c>ada@contoso.example</c>. No two users
/// of a directory hold the same name.
/// </summary>
/// <remarks>
/// <para>
/// A name is 1 to 128 characters: ASCII letters, digits, <c>-</c>, <c>.</c>, <c>_</c>,
/// <c>~</c>, <c>+</c>, and at most one <c>@</c>, starting and ending with a letter or digit. It
/// may have the shape of an email address and is not one: the hub never sends mail to it.
/// </para>
/// <para>
/// Names compare case-insensitively: <c>Ada@Contoso.example</c> and <c>ada@contoso.example</c>
/// are one name, hold one place in the directory, and derive one identifier with
/// <see cref="IdentityUserId.FromName"/>. That is this hub's rule, not a specification's.
/// </para>
/// <para>
/// A name is not an identifier. A stored user may take a new name and keeps its
/// <see cref="IdentityUser.Id"/>. <see langword="default"/> holds no name and every directory
/// boundary refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityUserName
{
    /// <summary>
    /// The longest user name, in characters.
    /// </summary>
    public const int MaxLength = IdentityDirectoryGrammar.MaxNameLength;

    /// <summary>
    /// Determines whether a value is a user name.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the user name grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityDirectoryGrammar.IsUserName(value))
        {
            message = ErrorMessages.UserNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
