using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies a gateway. The id is derived from the gateway's name.
/// </summary>
public readonly partial struct GatewayId
{
    // The UUIDv5 namespace of gateway ids. It is part of the wire contract: a gateway in any
    // language derives the same id, and changing it changes every gateway id.
    private static readonly Guid _namespace = new("205ec510-232d-4b57-8b6f-11bae6d7e919");

    /// <summary>Derives the id of the gateway with the specified name.</summary>
    /// <remarks>The id is the RFC 9562 version 5 UUID of the name in the gateway namespace.</remarks>
    /// <param name="name">The gateway name.</param>
    /// <returns>The gateway id.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    public static GatewayId FromName(GatewayName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(name));
        }

        return Guid.AsDeterministicGuid(name.Value, _namespace);
    }
}
