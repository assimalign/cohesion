using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// License information for the API. See the "License Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiLicense : IOpenApiExtensible
{
    /// <summary>Gets or sets the license name used for the API. Required by the specification.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the SPDX license expression for the API (OpenAPI 3.1+). Mutually exclusive with <see cref="Url"/>.</summary>
    public string? Identifier { get; set; }

    /// <summary>Gets or sets a URI to the license used for the API. Mutually exclusive with <see cref="Identifier"/>.</summary>
    public string? Url { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
