using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The identifier of an app registration: the OAuth <c>client_id</c> a client presents, and what
/// another registration refers to it by. It is a GUID.
/// </summary>
/// <remarks>
/// <para>
/// <b>A registration defined in code derives its identifier from a name</b> with
/// <see cref="FromName"/>, so the identifier is the same in every deployment and on every node
/// without being written down anywhere. A registration created at run time under the same name
/// gets the same identifier, and that is what lets code take it over: once code defines the
/// name, the registry serves the registration from code, locked, in place of the stored one. A
/// registration no code will name takes a random identifier from <see cref="New"/>.
/// </para>
/// <para>
/// The identifier's text is the 36-character form with hyphens, in lowercase:
/// <c>bec0a3ee-3f3c-599e-bd0b-3b7194e035ba</c> is the one <c>orders-api</c> derives. It needs
/// no escaping in a URL path segment, a form body, or HTTP Basic authentication, where RFC 6749
/// section 2.3.1 would otherwise have a client encode it. An identifier is not a secret.
/// </para>
/// <para>
/// <see langword="default"/> is the empty GUID, which identifies no registration: every
/// registration boundary refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityClientId
{
    // The UUIDv5 namespace of client identifiers. It is part of the contract: a tool in any
    // language derives the same identifier from a name, and changing it changes every derived
    // identifier, which would detach every registration created under a name from the code that
    // defines that name.
    private static readonly Guid _namespace = new("14af0e0d-a974-4487-871d-ef1dd83f6ad3");

    /// <summary>
    /// Derives the identifier of the registration with a name.
    /// </summary>
    /// <remarks>
    /// The identifier is the RFC 9562 version 5 UUID of the lowercase name in the client
    /// identifier namespace, <c>14af0e0d-a974-4487-871d-ef1dd83f6ad3</c>. Names that compare
    /// equal therefore derive one identifier, on every machine and in every version of the hub.
    /// </remarks>
    /// <param name="name">The name of the registration.</param>
    /// <returns>The identifier every registration with the name has.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    public static IdentityClientId FromName(IdentityClientName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ClientNameRequired, nameof(name));
        }

        return new IdentityClientId(Guid.AsDeterministicGuid(name.Value.ToLowerInvariant(), _namespace));
    }
}
