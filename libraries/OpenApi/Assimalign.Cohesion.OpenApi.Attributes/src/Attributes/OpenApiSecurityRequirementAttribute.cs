using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Declares a security requirement, applied to a class (document-level) or a method (operation-level).
/// References a security scheme by name and lists any required scopes.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class OpenApiSecurityRequirementAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiSecurityRequirementAttribute"/> class.
    /// </summary>
    /// <param name="scheme">The security scheme name.</param>
    /// <param name="scopes">The required scopes, if any.</param>
    public OpenApiSecurityRequirementAttribute(string scheme, params string[] scopes)
    {
        Scheme = scheme;
        Scopes = scopes ?? [];
    }

    /// <summary>Gets the security scheme name.</summary>
    public string Scheme { get; }

    /// <summary>Gets the required scopes.</summary>
    public string[] Scopes { get; }
}
