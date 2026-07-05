using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Declares a document-level security scheme. Applied to an assembly or a class to register a reusable
/// security scheme by name.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OpenApiSecuritySchemeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiSecuritySchemeAttribute"/> class.
    /// </summary>
    /// <param name="name">The security scheme component name.</param>
    /// <param name="type">The security scheme type.</param>
    public OpenApiSecuritySchemeAttribute(string name, SecuritySchemeType type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>Gets the security scheme component name.</summary>
    public string Name { get; }

    /// <summary>Gets the security scheme type.</summary>
    public SecuritySchemeType Type { get; }

    /// <summary>Gets or sets a description of the scheme. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the parameter name for an API key scheme.</summary>
    public string? ParameterName { get; set; }

    /// <summary>Gets or sets the API key location.</summary>
    public ParameterLocation? In { get; set; }

    /// <summary>Gets or sets the HTTP authorization scheme, for example <c>bearer</c>.</summary>
    public string? Scheme { get; set; }

    /// <summary>Gets or sets a bearer token format hint.</summary>
    public string? BearerFormat { get; set; }

    /// <summary>Gets or sets the OpenID Connect discovery URL.</summary>
    public string? OpenIdConnectUrl { get; set; }
}
