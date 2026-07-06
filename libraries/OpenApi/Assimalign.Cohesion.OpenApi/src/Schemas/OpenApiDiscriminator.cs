using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Discriminator metadata for polymorphic schemas. See the "Discriminator Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiDiscriminator : IOpenApiExtensible
{
    /// <summary>Gets or sets the name of the property in the payload that holds the discriminator value. Required by the specification.</summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>Gets the mapping between payload discriminator values and schema names or references.</summary>
    public IDictionary<string, string> Mapping { get; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets or sets the schema name or URI reference used when the discriminating property is absent or has
    /// no explicit or implicit mapping (OpenAPI 3.2+). Required when the discriminating property is optional.
    /// </summary>
    public string? DefaultMapping { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
