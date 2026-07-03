using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Defines a security scheme that can be used by the operations. See the "Security Scheme Object" section
/// of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiSecurityScheme : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets the type of the security scheme. Required by the specification.</summary>
    public SecuritySchemeType Type { get; set; }

    /// <summary>Gets or sets a description for the security scheme. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the name of the header, query, or cookie parameter. Required for <see cref="SecuritySchemeType.ApiKey"/>.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the location of the API key. Required for <see cref="SecuritySchemeType.ApiKey"/>; must be query, header, or cookie.</summary>
    public ParameterLocation? In { get; set; }

    /// <summary>Gets or sets the HTTP Authorization scheme name. Required for <see cref="SecuritySchemeType.Http"/>.</summary>
    public string? Scheme { get; set; }

    /// <summary>Gets or sets a hint identifying how the bearer token is formatted. Used with <see cref="SecuritySchemeType.Http"/>.</summary>
    public string? BearerFormat { get; set; }

    /// <summary>Gets or sets the OAuth flow configurations. Required for <see cref="SecuritySchemeType.OAuth2"/>.</summary>
    public OpenApiOAuthFlows? Flows { get; set; }

    /// <summary>Gets or sets the URL of the OAuth2 authorization server metadata document (RFC 8414). Used with <see cref="SecuritySchemeType.OAuth2"/> (OpenAPI 3.2+).</summary>
    public string? OAuth2MetadataUrl { get; set; }

    /// <summary>Gets or sets the OpenID Connect discovery URI. Required for <see cref="SecuritySchemeType.OpenIdConnect"/>.</summary>
    public string? OpenIdConnectUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether the security scheme is deprecated (OpenAPI 3.2+).</summary>
    public bool Deprecated { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
