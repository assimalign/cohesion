using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Identifies a resource: the id every instance of it shares. The id is derived from the resource's
/// provider and name.
/// </summary>
public readonly partial struct ResourceId
{
    // The UUIDv5 namespace of resource ids. It is part of the wire contract: a gateway in any
    // language derives the same id, and changing it changes every resource id.
    private static readonly Guid _namespace = new("e10d0c39-063e-43de-ac58-32a831744cbd");

    /// <summary>Derives the id of a resource from its provider and name.</summary>
    /// <remarks>
    /// The id is the RFC 9562 version 5 UUID of <c>{provider}/{name}</c> in the resource namespace,
    /// with the provider name in lowercase. The provider is part of the input because a resource
    /// name is unique only among one gateway's resources of the same provider; across gateways, the
    /// gateway's id qualifies the resource id.
    /// </remarks>
    /// <param name="provider">The resource's provider.</param>
    /// <param name="name">The resource's name.</param>
    /// <returns>The resource id.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>.
    /// </exception>
    public static ResourceId FromName(ProviderName provider, ResourceName name)
    {
        if (provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(provider));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
        }

        return Guid.AsDeterministicGuid(string.Concat(provider.Value.ToLowerInvariant(), "/", name.Value), _namespace);
    }
}
