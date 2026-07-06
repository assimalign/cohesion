using System.Collections.Generic;

using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Generated;

namespace Assimalign.Cohesion.OpenApi.Integration.Tests;

/// <summary>
/// A representative Web-layer endpoint source: it exposes the source generator's compile-time metadata
/// registry through the integration contract, with no runtime reflection. This is the shape a real Web
/// layer would take.
/// </summary>
internal sealed class GeneratedEndpointSource : IOpenApiEndpointSource
{
    public IReadOnlyList<OpenApiOperationMetadata> Operations => OpenApiMetadataRegistry.Operations;

    public IReadOnlyList<OpenApiSchemaMetadata> Schemas => OpenApiMetadataRegistry.Schemas;

    public IReadOnlyList<OpenApiTagMetadata> Tags => OpenApiMetadataRegistry.Tags;

    public IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes => OpenApiMetadataRegistry.SecuritySchemes;
}
