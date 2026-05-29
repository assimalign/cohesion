using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A declaration of the security mechanisms that can be used for an operation or the whole API. See the
/// "Security Requirement Object" section of the OpenAPI Specification.
/// </summary>
/// <remarks>
/// Each key is the name of a security scheme declared in the Components Object. The associated value lists
/// the scope names required for OAuth2 / OpenID Connect schemes, and is empty for other scheme types. The
/// Security Requirement Object does not allow specification extensions.
/// </remarks>
public sealed class OpenApiSecurityRequirement : IOpenApiElement
{
    /// <summary>Gets the required security schemes, keyed by scheme name, with their required scope names as values.</summary>
    public IDictionary<string, IList<string>> Schemes { get; } = new Dictionary<string, IList<string>>();
}
