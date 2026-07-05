using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiSecurityScheme"/>.
/// </summary>
public sealed class OpenApiSecuritySchemeBuilder
{
    private readonly OpenApiSecurityScheme _scheme = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiSecuritySchemeBuilder"/> class.
    /// </summary>
    /// <param name="version">The OpenAPI specification version the builder targets.</param>
    public OpenApiSecuritySchemeBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>
    /// Builds the configured <see cref="OpenApiSecurityScheme"/>.
    /// </summary>
    /// <returns>The constructed <see cref="OpenApiSecurityScheme"/>.</returns>
    public OpenApiSecurityScheme Build() => _scheme;

    /// <summary>Sets the scheme description.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSecuritySchemeBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _scheme.Description = description;
        return this;
    }

    /// <summary>Configures the scheme as an API key.</summary>
    /// <param name="name">The parameter name carrying the key.</param>
    /// <param name="location">The key location (query, header, or cookie).</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSecuritySchemeBuilder ApiKey(string name, ParameterLocation location)
    {
        ArgumentNullException.ThrowIfNull(name);
        _scheme.Type = SecuritySchemeType.ApiKey;
        _scheme.Name = name;
        _scheme.In = location;
        return this;
    }

    /// <summary>Configures the scheme as HTTP authentication.</summary>
    /// <param name="scheme">The HTTP authorization scheme, for example <c>bearer</c>.</param>
    /// <param name="bearerFormat">An optional bearer token format hint.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSecuritySchemeBuilder Http(string scheme, string? bearerFormat = null)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        _scheme.Type = SecuritySchemeType.Http;
        _scheme.Scheme = scheme;
        _scheme.BearerFormat = bearerFormat;
        return this;
    }

    /// <summary>Configures the scheme as OpenID Connect.</summary>
    /// <param name="openIdConnectUrl">The discovery document URL.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSecuritySchemeBuilder OpenIdConnect(string openIdConnectUrl)
    {
        ArgumentNullException.ThrowIfNull(openIdConnectUrl);
        _scheme.Type = SecuritySchemeType.OpenIdConnect;
        _scheme.OpenIdConnectUrl = openIdConnectUrl;
        return this;
    }

    /// <summary>Configures the scheme as mutual TLS (OpenAPI 3.1+).</summary>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSecuritySchemeBuilder MutualTls()
    {
        OpenApiBuildGuard.Require(_version, OpenApiFeature.MutualTlsSecurityScheme, "The 'mutualTLS' security scheme type");
        _scheme.Type = SecuritySchemeType.MutualTLS;
        return this;
    }

    /// <summary>Adds an OAuth2 authorization code flow.</summary>
    /// <param name="authorizationUrl">The authorization URL.</param>
    /// <param name="tokenUrl">The token URL.</param>
    /// <param name="scopes">The available scopes as name/description pairs.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSecuritySchemeBuilder OAuth2AuthorizationCode(string authorizationUrl, string tokenUrl, params (string Name, string Description)[] scopes)
    {
        ArgumentNullException.ThrowIfNull(authorizationUrl);
        ArgumentNullException.ThrowIfNull(tokenUrl);
        var flow = new OpenApiOAuthFlow { AuthorizationUrl = authorizationUrl, TokenUrl = tokenUrl };
        AddScopes(flow, scopes);
        EnsureFlows().AuthorizationCode = flow;
        _scheme.Type = SecuritySchemeType.OAuth2;
        return this;
    }

    /// <summary>Adds an OAuth2 client credentials flow.</summary>
    /// <param name="tokenUrl">The token URL.</param>
    /// <param name="scopes">The available scopes as name/description pairs.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiSecuritySchemeBuilder OAuth2ClientCredentials(string tokenUrl, params (string Name, string Description)[] scopes)
    {
        ArgumentNullException.ThrowIfNull(tokenUrl);
        var flow = new OpenApiOAuthFlow { TokenUrl = tokenUrl };
        AddScopes(flow, scopes);
        EnsureFlows().ClientCredentials = flow;
        _scheme.Type = SecuritySchemeType.OAuth2;
        return this;
    }

    private OpenApiOAuthFlows EnsureFlows() => _scheme.Flows ??= new OpenApiOAuthFlows();

    private static void AddScopes(OpenApiOAuthFlow flow, (string Name, string Description)[] scopes)
    {
        foreach (var (name, description) in scopes ?? [])
        {
            flow.Scopes[name] = description;
        }
    }
}
