using System.Collections.Generic;

using Assimalign.Cohesion.OpenApi.Attributes;

namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>
/// A source of OpenApi endpoint metadata contributed by a service layer such as Web. This is the
/// integration boundary that keeps the OpenApi family free of hosting concerns: a Web layer implements
/// this contract to expose its routes as the transport-neutral intermediate metadata, and the
/// description provider composes one or more sources into a document. Implementations should surface
/// metadata that was produced without runtime reflection (for example, source-generated) so the whole
/// path stays NativeAOT-safe.
/// </summary>
public interface IOpenApiEndpointSource
{
    /// <summary>Gets the operation metadata for the endpoints this source contributes.</summary>
    IReadOnlyList<OpenApiOperationMetadata> Operations { get; }

    /// <summary>Gets the schema component metadata this source contributes.</summary>
    IReadOnlyList<OpenApiSchemaMetadata> Schemas { get; }

    /// <summary>Gets the document tags this source contributes.</summary>
    IReadOnlyList<OpenApiTagMetadata> Tags { get; }

    /// <summary>Gets the security scheme metadata this source contributes.</summary>
    IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes { get; }
}
