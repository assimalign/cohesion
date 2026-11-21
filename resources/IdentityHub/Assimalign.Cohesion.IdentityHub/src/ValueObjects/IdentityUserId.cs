using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The identifier of a user: what a token names the user by, and what another identity object
/// refers to the user by. It is a GUID.
/// </summary>
/// <remarks>
/// <para>
/// <b>A user's identifier is derived from the name the user is created under</b> with
/// <see cref="FromName"/>, so a user defined in code has the same identifier in every deployment
/// and on every node without it being written down anywhere. A user created at run time under
/// the same name gets the same identifier, and that is what lets code take the user over: once
/// code defines the name, the directory serves the user from code, locked, in place of the
/// stored one.
/// </para>
/// <para>
/// <b>An identifier never changes.</b> A stored user may take a new name and keeps the
/// identifier it was created with, so a token issued before the change still names the same
/// user. A user no code will name takes a random identifier from <see cref="New"/>.
/// </para>
/// <para>
/// The identifier's text is the 36-character form with hyphens, in lowercase. An identifier is
/// not a secret. <see langword="default"/> is the empty GUID, which identifies no user: every
/// directory boundary refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityUserId
{
    // The UUIDv5 namespace of user identifiers. It is part of the contract: a tool in any
    // language derives the same identifier from a name, and changing it changes every derived
    // identifier, which would detach every user created under a name from the code that
    // defines that name.
    private static readonly Guid _namespace = new("5df5f6e9-93f3-40bb-b580-a144644a97af");

    /// <summary>
    /// Derives the identifier of the user created under a name.
    /// </summary>
    /// <remarks>
    /// The identifier is the RFC 9562 version 5 UUID of the lowercase name in the user
    /// identifier namespace, <c>5df5f6e9-93f3-40bb-b580-a144644a97af</c>. Names that compare
    /// equal therefore derive one identifier, on every machine and in every version of the hub.
    /// </remarks>
    /// <param name="name">The name the user is created under.</param>
    /// <returns>The identifier every user created under the name has.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    public static IdentityUserId FromName(IdentityUserName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.UserNameRequired, nameof(name));
        }

        return new IdentityUserId(Guid.AsDeterministicGuid(name.Value.ToLowerInvariant(), _namespace));
    }
}
