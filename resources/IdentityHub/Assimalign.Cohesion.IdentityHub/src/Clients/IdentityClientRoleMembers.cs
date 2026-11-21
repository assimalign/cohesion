using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The kinds of principal an application role may be given to. The member values are stable
/// and additive-only.
/// </summary>
[Flags]
public enum IdentityClientRoleMembers
{
    /// <summary>
    /// No principal. A role has at least one kind of member, so no role holds this value.
    /// </summary>
    None = 0,

    /// <summary>
    /// The role may be assigned to users and groups, and reaches the API in the token of a
    /// signed-in user.
    /// </summary>
    Users = 1,

    /// <summary>
    /// The role may be held by another registration as an application permission, and reaches
    /// the API in a token the application obtained as itself.
    /// </summary>
    Applications = 2,
}
