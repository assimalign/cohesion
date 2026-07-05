using System.Collections.Generic;

using Assimalign.Cohesion.OpenApi.Attributes;

namespace Assimalign.Cohesion.OpenApi.Generation;

/// <summary>
/// The collected intermediate metadata fed into the generation pipeline. Populated by the source
/// generator (which emits it from discovered attributes) or built by hand from the attribute mapper.
/// </summary>
public sealed class OpenApiGenerationInput
{
    /// <summary>Gets the discovered operations.</summary>
    public IReadOnlyList<OpenApiOperationMetadata> Operations { get; init; } = [];

    /// <summary>Gets the discovered schema components.</summary>
    public IReadOnlyList<OpenApiSchemaMetadata> Schemas { get; init; } = [];

    /// <summary>Gets the discovered document tags.</summary>
    public IReadOnlyList<OpenApiTagMetadata> Tags { get; init; } = [];

    /// <summary>Gets the discovered security schemes.</summary>
    public IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes { get; init; } = [];
}
