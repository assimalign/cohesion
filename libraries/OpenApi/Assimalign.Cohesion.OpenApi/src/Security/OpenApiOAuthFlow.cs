using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Configuration details for a single supported OAuth flow. See the "OAuth Flow Object" section of the
/// OpenAPI Specification.
/// </summary>
public sealed class OpenApiOAuthFlow : IOpenApiExtensible
{
    /// <summary>Gets or sets the authorization URI. Required for the implicit and authorization code flows.</summary>
    public string? AuthorizationUrl { get; set; }

    /// <summary>Gets or sets the token URI. Required for the password, client credentials, authorization code, and device authorization flows.</summary>
    public string? TokenUrl { get; set; }

    /// <summary>Gets or sets the device authorization URI. Required for the device authorization flow (OpenAPI 3.2+).</summary>
    public string? DeviceAuthorizationUrl { get; set; }

    /// <summary>Gets or sets the URI used to obtain refresh tokens.</summary>
    public string? RefreshUrl { get; set; }

    /// <summary>Gets the available scopes for the OAuth2 security scheme, keyed by scope name with a short description as the value.</summary>
    public IDictionary<string, string> Scopes { get; } = new Dictionary<string, string>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
