using System;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One role of the API an app registration exposes: a permission that is given to users, or
/// that another registration may hold as <see cref="IdentityClientPermissionKind.Application"/>
/// and exercise as itself.
/// </summary>
/// <remarks>
/// A role is immutable. Its <see cref="Name"/> is what a token's <c>roles</c> claim carries,
/// and <see cref="Members"/> says which kinds of principal may be given it.
/// </remarks>
public sealed class IdentityClientRole
{
    private IdentityClientRole(
        IdentityClientPermissionName name,
        IdentityClientRoleMembers members,
        string displayName,
        string? description)
    {
        Name = name;
        Members = members;
        DisplayName = displayName;
        Description = description;
    }

    /// <summary>
    /// Gets the name of the role, unique among the roles of its registration.
    /// </summary>
    public IdentityClientPermissionName Name { get; }

    /// <summary>
    /// Gets the kinds of principal the role may be given to.
    /// </summary>
    public IdentityClientRoleMembers Members { get; }

    /// <summary>
    /// Gets the name a person is shown for the role.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets what the role allows, in words a person is shown, when it is described.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Creates a role.
    /// </summary>
    /// <param name="name">The name of the role.</param>
    /// <param name="members">The kinds of principal the role may be given to: at least one.</param>
    /// <param name="displayName">
    /// The name a person is shown, 1 to 128 characters with no control character, or
    /// <see langword="null"/> to show <paramref name="name"/>.
    /// </param>
    /// <param name="description">
    /// What the role allows, at most 256 characters, or <see langword="null"/> for none.
    /// </param>
    /// <returns>The role.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is <see langword="default"/>, or <paramref name="displayName"/> or
    /// <paramref name="description"/> breaks its rule.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="members"/> names no kind of principal, or one that is not defined.
    /// </exception>
    public static IdentityClientRole Create(
        IdentityClientPermissionName name,
        IdentityClientRoleMembers members,
        string? displayName = null,
        string? description = null)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.PermissionNameRequired, nameof(name));
        }

        const IdentityClientRoleMembers all = IdentityClientRoleMembers.Users | IdentityClientRoleMembers.Applications;
        if (members is IdentityClientRoleMembers.None || (members & ~all) is not 0)
        {
            throw new ArgumentOutOfRangeException(nameof(members), members, ErrorMessages.RoleMembersInvalid);
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

        return new IdentityClientRole(
            name,
            members,
            displayName ?? name.Value,
            description is { Length: > 0 } ? description : null);
    }

    /// <summary>
    /// Returns the name of the role.
    /// </summary>
    /// <returns>The name of the role.</returns>
    public override string ToString() => Name.ToString();
}
