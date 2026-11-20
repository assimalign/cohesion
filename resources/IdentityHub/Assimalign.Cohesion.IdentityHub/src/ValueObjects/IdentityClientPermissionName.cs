using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The name of a permission an app registration exposes: the value of a scope or of an
/// application role, as it appears in a request's <c>scope</c> parameter and in a token's
/// <c>scp</c> or <c>roles</c> claim.
/// </summary>
/// <remarks>
/// A name is a scope token of RFC 6749 section 3.3, 1 to 128 characters: visible ASCII without
/// the space, the double quote, and the backslash (<c>orders.read</c>,
/// <c>Orders.ReadWrite.All</c>, <c>https://orders.example/read</c>). The space is refused
/// because a request lists scopes separated by spaces. Names compare ordinally and are unique
/// among the scopes, and among the roles, of one registration.
/// <see langword="default"/> holds no name and every registration boundary refuses it.
/// </remarks>
public readonly partial struct IdentityClientPermissionName
{
    /// <summary>
    /// The longest permission name, in characters.
    /// </summary>
    public const int MaxLength = IdentityClientGrammar.MaxPermissionNameLength;

    /// <summary>
    /// Determines whether a value is a permission name.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the scope token grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityClientGrammar.IsPermissionName(value))
        {
            message = ErrorMessages.PermissionNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
