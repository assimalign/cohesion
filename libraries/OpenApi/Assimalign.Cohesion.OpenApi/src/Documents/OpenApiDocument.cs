using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The root object of an OpenAPI description. See the "OpenAPI Object" section of the OpenAPI Specification.
/// </summary>
/// <remarks>
/// The model is a superset across the supported lines: a single graph carries every field from 3.0.x,
/// 3.1.x, and 3.2.x. <see cref="SpecVersion"/> records which line the document targets; serialization
/// and validation consult <see cref="OpenApiVersionCapabilities"/> to gate fields that do not apply to
/// that line.
/// </remarks>
public sealed class OpenApiDocument : IOpenApiExtensible
{
    /// <summary>Gets or sets the OpenAPI line this document targets. Defaults to <see cref="OpenApiSpecVersion.V3_1"/>.</summary>
    public OpenApiSpecVersion SpecVersion { get; set; } = OpenApiSpecVersion.V3_1;

    /// <summary>Gets or sets the <c>$self</c> URI that identifies this document (OpenAPI 3.2+).</summary>
    public string? Self { get; set; }

    /// <summary>Gets or sets the metadata about the API. Required by the specification.</summary>
    public OpenApiInfo Info { get; set; } = new();

    /// <summary>Gets or sets the default JSON Schema dialect URI for schemas in this document (OpenAPI 3.1+).</summary>
    public string? JsonSchemaDialect { get; set; }

    /// <summary>Gets the servers that provide connectivity information for the API.</summary>
    public IList<OpenApiServer> Servers { get; } = new List<OpenApiServer>();

    /// <summary>Gets or sets the available paths and operations for the API.</summary>
    public OpenApiPaths? Paths { get; set; }

    /// <summary>Gets the webhooks that may be received as part of this API, keyed by a unique name (OpenAPI 3.1+).</summary>
    public IDictionary<string, OpenApiPathItem> Webhooks { get; } = new Dictionary<string, OpenApiPathItem>();

    /// <summary>Gets or sets the reusable components for the API.</summary>
    public OpenApiComponents? Components { get; set; }

    /// <summary>Gets the security requirements that apply across the API unless overridden per operation.</summary>
    public IList<OpenApiSecurityRequirement> Security { get; } = new List<OpenApiSecurityRequirement>();

    /// <summary>Gets the tags used by the document, with additional metadata.</summary>
    public IList<OpenApiTag> Tags { get; } = new List<OpenApiTag>();

    /// <summary>Gets or sets additional external documentation.</summary>
    public OpenApiExternalDocumentation? ExternalDocs { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
