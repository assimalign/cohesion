using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiSecurityScheme"/>.
/// </summary>
public interface IOpenApiSecuritySchemeBuilder
{
    /// <summary>Sets the scheme description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSecuritySchemeBuilder Description(string description);

    /// <summary>Configures the scheme as an API key.</summary>
    /// <param name="name">The parameter name carrying the key.</param>
    /// <param name="location">The key location (query, header, or cookie).</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSecuritySchemeBuilder ApiKey(string name, ParameterLocation location);

    /// <summary>Configures the scheme as HTTP authentication.</summary>
    /// <param name="scheme">The HTTP authorization scheme, for example <c>bearer</c>.</param>
    /// <param name="bearerFormat">An optional bearer token format hint.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSecuritySchemeBuilder Http(string scheme, string? bearerFormat = null);

    /// <summary>Configures the scheme as OpenID Connect.</summary>
    /// <param name="openIdConnectUrl">The discovery document URL.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSecuritySchemeBuilder OpenIdConnect(string openIdConnectUrl);

    /// <summary>Configures the scheme as mutual TLS (OpenAPI 3.1+).</summary>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSecuritySchemeBuilder MutualTls();

    /// <summary>Adds an OAuth2 authorization code flow.</summary>
    /// <param name="authorizationUrl">The authorization URL.</param>
    /// <param name="tokenUrl">The token URL.</param>
    /// <param name="scopes">The available scopes as name/description pairs.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSecuritySchemeBuilder OAuth2AuthorizationCode(string authorizationUrl, string tokenUrl, params (string Name, string Description)[] scopes);

    /// <summary>Adds an OAuth2 client credentials flow.</summary>
    /// <param name="tokenUrl">The token URL.</param>
    /// <param name="scopes">The available scopes as name/description pairs.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiSecuritySchemeBuilder OAuth2ClientCredentials(string tokenUrl, params (string Name, string Description)[] scopes);
}
