using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiSecuritySchemeBuilder"/> implementation.</summary>
internal sealed class OpenApiSecuritySchemeBuilder : IOpenApiSecuritySchemeBuilder
{
    private readonly OpenApiSecurityScheme _scheme = new();
    private readonly OpenApiSpecVersion _version;

    internal OpenApiSecuritySchemeBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    internal OpenApiSecurityScheme Build() => _scheme;

    public IOpenApiSecuritySchemeBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _scheme.Description = description;
        return this;
    }

    public IOpenApiSecuritySchemeBuilder ApiKey(string name, ParameterLocation location)
    {
        ArgumentNullException.ThrowIfNull(name);
        _scheme.Type = SecuritySchemeType.ApiKey;
        _scheme.Name = name;
        _scheme.In = location;
        return this;
    }

    public IOpenApiSecuritySchemeBuilder Http(string scheme, string? bearerFormat = null)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        _scheme.Type = SecuritySchemeType.Http;
        _scheme.Scheme = scheme;
        _scheme.BearerFormat = bearerFormat;
        return this;
    }

    public IOpenApiSecuritySchemeBuilder OpenIdConnect(string openIdConnectUrl)
    {
        ArgumentNullException.ThrowIfNull(openIdConnectUrl);
        _scheme.Type = SecuritySchemeType.OpenIdConnect;
        _scheme.OpenIdConnectUrl = openIdConnectUrl;
        return this;
    }

    public IOpenApiSecuritySchemeBuilder MutualTls()
    {
        OpenApiBuildGuard.Require(_version, OpenApiFeature.MutualTlsSecurityScheme, "The 'mutualTLS' security scheme type");
        _scheme.Type = SecuritySchemeType.MutualTLS;
        return this;
    }

    public IOpenApiSecuritySchemeBuilder OAuth2AuthorizationCode(string authorizationUrl, string tokenUrl, params (string Name, string Description)[] scopes)
    {
        ArgumentNullException.ThrowIfNull(authorizationUrl);
        ArgumentNullException.ThrowIfNull(tokenUrl);
        var flow = new OpenApiOAuthFlow { AuthorizationUrl = authorizationUrl, TokenUrl = tokenUrl };
        AddScopes(flow, scopes);
        EnsureFlows().AuthorizationCode = flow;
        _scheme.Type = SecuritySchemeType.OAuth2;
        return this;
    }

    public IOpenApiSecuritySchemeBuilder OAuth2ClientCredentials(string tokenUrl, params (string Name, string Description)[] scopes)
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
