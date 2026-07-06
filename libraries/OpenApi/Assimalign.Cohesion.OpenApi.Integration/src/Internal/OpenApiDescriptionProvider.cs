using System.Collections.Generic;

using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Generation;

namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>
/// The default <see cref="IOpenApiDescriptionProvider"/>: aggregates the metadata contributed by one or
/// more <see cref="IOpenApiEndpointSource"/> instances and generates a version-targeted document through
/// the generation pipeline. No reflection is used — the composition is plain data assembly.
/// </summary>
internal sealed class OpenApiDescriptionProvider : IOpenApiDescriptionProvider
{
    private readonly IReadOnlyList<IOpenApiEndpointSource> _sources;
    private readonly OpenApiDescriptionInfo _info;

    internal OpenApiDescriptionProvider(IReadOnlyList<IOpenApiEndpointSource> sources, OpenApiDescriptionInfo info)
    {
        _sources = sources;
        _info = info;
    }

    public OpenApiDocument GetDocument(OpenApiSpecVersion version)
    {
        var operations = new List<OpenApiOperationMetadata>();
        var schemas = new List<OpenApiSchemaMetadata>();
        var tags = new List<OpenApiTagMetadata>();
        var securitySchemes = new List<OpenApiSecuritySchemeMetadata>();

        foreach (var source in _sources)
        {
            operations.AddRange(source.Operations);
            schemas.AddRange(source.Schemas);
            tags.AddRange(source.Tags);
            securitySchemes.AddRange(source.SecuritySchemes);
        }

        var input = new OpenApiGenerationInput
        {
            Operations = operations,
            Schemas = schemas,
            Tags = tags,
            SecuritySchemes = securitySchemes
        };

        return OpenApiDocumentGenerator.Generate(input, new OpenApiGenerationOptions
        {
            Version = version,
            Title = _info.Title,
            ApiVersion = _info.ApiVersion,
            Description = _info.Description
        });
    }
}
