using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The identifier of a group: what a token's group claim carries, and what another identity
/// object refers to the group by. It is a GUID.
/// </summary>
/// <remarks>
/// <para>
/// <b>A group's identifier is derived from the name the group is created under</b> with
/// <see cref="FromName"/>, so a group defined in code has the same identifier in every
/// deployment and on every node without it being written down anywhere. A group created at run
/// time under the same name gets the same identifier, and that is what lets code take the group
/// over: once code defines the name, the directory serves the group from code, locked, in place
/// of the stored one.
/// </para>
/// <para>
/// <b>An identifier never changes.</b> A stored group may take a new name and keeps the
/// identifier it was created with. A group no code will name takes a random identifier from
/// <see cref="New"/>.
/// </para>
/// <para>
/// The identifier's text is the 36-character form with hyphens, in lowercase. An identifier is
/// not a secret. <see langword="default"/> is the empty GUID, which identifies no group: every
/// directory boundary refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityGroupId
{
    // The UUIDv5 namespace of group identifiers. It is part of the contract: a tool in any
    // language derives the same identifier from a name, and changing it changes every derived
    // identifier, which would detach every group created under a name from the code that
    // defines that name.
    private static readonly Guid _namespace = new("1dbba961-5e36-4f35-b634-d96873c00590");

    /// <summary>
    /// Derives the identifier of the group created under a name.
    /// </summary>
    /// <remarks>
    /// The identifier is the RFC 9562 version 5 UUID of the lowercase name in the group
    /// identifier namespace, <c>1dbba961-5e36-4f35-b634-d96873c00590</c>. Names that compare
    /// equal therefore derive one identifier, on every machine and in every version of the hub.
    /// </remarks>
    /// <param name="name">The name the group is created under.</param>
    /// <returns>The identifier every group created under the name has.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    public static IdentityGroupId FromName(IdentityGroupName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GroupNameRequired, nameof(name));
        }

        return new IdentityGroupId(Guid.AsDeterministicGuid(name.Value.ToLowerInvariant(), _namespace));
    }
}
