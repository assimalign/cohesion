using System;

using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Versioning;

namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>The default <see cref="IOpenApiDocumentExporter"/>, delegating to the writers and the transform pipeline.</summary>
internal sealed class OpenApiDocumentExporter : IOpenApiDocumentExporter
{
    public string Export(OpenApiDocument document, OpenApiFormat format)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Serialize(document, format, document.SpecVersion);
    }

    public OpenApiExportResult Export(OpenApiDocument document, OpenApiFormat format, OpenApiSpecVersion version)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SpecVersion == version)
        {
            return new OpenApiExportResult(Serialize(document, format, version), []);
        }

        var transform = OpenApiVersionTransformer.Transform(document, version);
        return new OpenApiExportResult(Serialize(transform.Document, format, version), transform.Diagnostics);
    }

    private static string Serialize(OpenApiDocument document, OpenApiFormat format, OpenApiSpecVersion version) => format switch
    {
        OpenApiFormat.Json => document.ToJson(version, indented: true),
        OpenApiFormat.Yaml => document.ToYaml(version),
        _ => throw new OpenApiException($"Unknown OpenApi format '{format}'.")
    };
}
