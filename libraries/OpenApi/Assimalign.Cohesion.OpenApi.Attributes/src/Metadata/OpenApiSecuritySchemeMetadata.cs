namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for a security scheme, produced from an
/// <see cref="OpenApiSecuritySchemeAttribute"/>.
/// </summary>
public sealed class OpenApiSecuritySchemeMetadata
{
    /// <summary>Gets the security scheme component name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the security scheme type.</summary>
    public required SecuritySchemeType Type { get; init; }

    /// <summary>Gets the scheme description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the API key parameter name.</summary>
    public string? ParameterName { get; init; }

    /// <summary>Gets the API key location.</summary>
    public ParameterLocation? In { get; init; }

    /// <summary>Gets the HTTP authorization scheme.</summary>
    public string? Scheme { get; init; }

    /// <summary>Gets the bearer token format hint.</summary>
    public string? BearerFormat { get; init; }

    /// <summary>Gets the OpenID Connect discovery URL.</summary>
    public string? OpenIdConnectUrl { get; init; }
}
