using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for a security requirement, produced from an
/// <see cref="OpenApiSecurityRequirementAttribute"/>.
/// </summary>
public sealed class OpenApiSecurityRequirementMetadata
{
    /// <summary>Gets the security scheme name.</summary>
    public required string Scheme { get; init; }

    /// <summary>Gets the required scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];
}
