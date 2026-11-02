using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies a resource provider. The id is derived from the provider's name.
/// </summary>
public readonly partial struct ProviderId
{
    // The UUIDv5 namespace of provider ids. It is part of the wire contract: a gateway in any
    // language derives the same id, and changing it changes every provider id.
    private static readonly Guid _namespace = new("7a1afa1e-6690-4108-8c87-dc141c045fda");

    /// <summary>Derives the id of the provider with the specified name.</summary>
    /// <remarks>
    /// The id is the RFC 9562 version 5 UUID of the lowercase name in the provider namespace, so
    /// names that compare equal derive one id.
    /// </remarks>
    /// <param name="name">The provider name.</param>
    /// <returns>The provider id.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    public static ProviderId FromName(ProviderName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(name));
        }

        return Guid.AsDeterministicGuid(name.Value.ToLowerInvariant(), _namespace);
    }
}
