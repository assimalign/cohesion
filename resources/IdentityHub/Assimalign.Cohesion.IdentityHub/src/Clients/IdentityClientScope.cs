using System;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One scope of the API an app registration exposes: a permission another registration may
/// hold as <see cref="IdentityClientPermissionKind.Delegated"/> and exercise on behalf of a
/// signed-in user.
/// </summary>
/// <remarks>
/// A scope is immutable. Its <see cref="Name"/> is what a request asks for and what a token's
/// <c>scp</c> claim carries; its display name and description are what a person is shown.
/// </remarks>
public sealed class IdentityClientScope
{
    private IdentityClientScope(IdentityClientPermissionName name, string displayName, string? description)
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
    }

    /// <summary>
    /// Gets the name of the scope, unique among the scopes of its registration.
    /// </summary>
    public IdentityClientPermissionName Name { get; }

    /// <summary>
    /// Gets the name a person is shown for the scope.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets what the scope allows, in words a person is shown, when it is described.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Creates a scope.
    /// </summary>
    /// <param name="name">The name of the scope.</param>
    /// <param name="displayName">
    /// The name a person is shown, 1 to 128 characters with no control character, or
    /// <see langword="null"/> to show <paramref name="name"/>.
    /// </param>
    /// <param name="description">
    /// What the scope allows, at most 256 characters, or <see langword="null"/> for none.
    /// </param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is <see langword="default"/>, or <paramref name="displayName"/> or
    /// <paramref name="description"/> breaks its rule.
    /// </exception>
    public static IdentityClientScope Create(
        IdentityClientPermissionName name,
        string? displayName = null,
        string? description = null)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.PermissionNameRequired, nameof(name));
        }

        if (displayName is not null &&
            !IdentityClientGrammar.IsText(displayName, IdentityClientGrammar.MaxDisplayNameLength, allowLineBreaks: false))
        {
            throw new ArgumentException(ErrorMessages.DisplayNameInvalid, nameof(displayName));
        }

        if (description is { Length: > 0 } &&
            !IdentityClientGrammar.IsText(description, IdentityClientGrammar.MaxDescriptionLength, allowLineBreaks: true))
        {
            throw new ArgumentException(ErrorMessages.DescriptionInvalid, nameof(description));
        }

        return new IdentityClientScope(
            name,
            displayName ?? name.Value,
            description is { Length: > 0 } ? description : null);
    }

    /// <summary>
    /// Returns the name of the scope.
    /// </summary>
    /// <returns>The name of the scope.</returns>
    public override string ToString() => Name.ToString();
}
