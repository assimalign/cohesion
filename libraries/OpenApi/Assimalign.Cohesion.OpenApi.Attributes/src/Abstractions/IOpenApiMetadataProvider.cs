using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// A source of the flat OpenApi metadata that one assembly contributes. The source generator emits an
/// implementation for every assembly that applies the OpenApi attributes and advertises it with
/// <see cref="OpenApiMetadataProviderAttribute"/>, so a referencing compilation composes every
/// assembly's metadata at compile time instead of discovering it at run time.
/// </summary>
/// <remarks>
/// A provider describes only its own assembly and returns the same lists on every call. A composing
/// compilation concatenates providers in a fixed order and does not de-duplicate them.
/// </remarks>
public interface IOpenApiMetadataProvider
{
    /// <summary>Gets the operations this provider contributes.</summary>
    IReadOnlyList<OpenApiOperationMetadata> Operations { get; }

    /// <summary>Gets the schema components this provider contributes.</summary>
    IReadOnlyList<OpenApiSchemaMetadata> Schemas { get; }

    /// <summary>Gets the document tags this provider contributes.</summary>
    IReadOnlyList<OpenApiTagMetadata> Tags { get; }

    /// <summary>Gets the security schemes this provider contributes.</summary>
    IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes { get; }
}
