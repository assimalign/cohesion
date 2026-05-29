using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Configuration of the supported OAuth flows. See the "OAuth Flows Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiOAuthFlows : IOpenApiExtensible
{
    /// <summary>Gets or sets the configuration for the OAuth Implicit flow.</summary>
    public OpenApiOAuthFlow? Implicit { get; set; }

    /// <summary>Gets or sets the configuration for the OAuth Resource Owner Password flow.</summary>
    public OpenApiOAuthFlow? Password { get; set; }

    /// <summary>Gets or sets the configuration for the OAuth Client Credentials flow.</summary>
    public OpenApiOAuthFlow? ClientCredentials { get; set; }

    /// <summary>Gets or sets the configuration for the OAuth Authorization Code flow.</summary>
    public OpenApiOAuthFlow? AuthorizationCode { get; set; }

    /// <summary>Gets or sets the configuration for the OAuth Device Authorization flow (OpenAPI 3.2+).</summary>
    public OpenApiOAuthFlow? DeviceAuthorization { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
