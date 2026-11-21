using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The name of a group, such as <c>orders-admins</c>. No two groups of a directory hold the same
/// name.
/// </summary>
/// <remarks>
/// <para>
/// A name is 1 to 128 characters from the unreserved set of RFC 3986 section 2.3 (letters,
/// digits, <c>-</c>, <c>.</c>, <c>_</c>, <c>~</c>), starting and ending with a letter or digit,
/// so it needs no escaping in a URL path segment or as one value of a claim.
/// </para>
/// <para>
/// Names compare case-insensitively: <c>Orders-Admins</c> and <c>orders-admins</c> are one name,
/// hold one place in the directory, and derive one identifier with
/// <see cref="IdentityGroupId.FromName"/>. That is this hub's rule, not a specification's.
/// </para>
/// <para>
/// A name is not an identifier. A stored group may take a new name and keeps its
/// <see cref="IdentityGroup.Id"/>. <see langword="default"/> holds no name and every directory
/// boundary refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityGroupName
{
    /// <summary>
    /// The longest group name, in characters.
    /// </summary>
    public const int MaxLength = IdentityDirectoryGrammar.MaxNameLength;

    /// <summary>
    /// Determines whether a value is a group name.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the group name grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityDirectoryGrammar.IsGroupName(value))
        {
            message = ErrorMessages.GroupNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
