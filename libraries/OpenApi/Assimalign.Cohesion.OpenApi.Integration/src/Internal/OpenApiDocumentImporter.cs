using System;

using Assimalign.Cohesion.OpenApi.Serialization;

namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>The default <see cref="IOpenApiDocumentImporter"/>, delegating to the JSON and YAML readers.</summary>
internal sealed class OpenApiDocumentImporter : IOpenApiDocumentImporter
{
    public OpenApiDocument Import(string content, OpenApiFormat format)
    {
        ArgumentNullException.ThrowIfNull(content);
        return format switch
        {
            OpenApiFormat.Json => OpenApiJson.Parse(content),
            OpenApiFormat.Yaml => OpenApiYaml.Parse(content),
            _ => throw new OpenApiException($"Unknown OpenApi format '{format}'.")
        };
    }
}
