using System;
using System.IO;

using Assimalign.Cohesion.Content.Yaml;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// The YAML implementation of <see cref="IOpenApiWriter"/>. Composes the version-aware model-to-node
/// mapping with the YAML node formatter. Block-style YAML is inherently indented, so the
/// <see cref="OpenApiWriterOptions.Indented"/> flag has no effect for this writer.
/// </summary>
internal sealed class OpenApiYamlWriter : IOpenApiWriter
{
    public void Write(OpenApiDocument document, Stream stream, OpenApiWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);

        YamlText.Write(stream, Convert(document, options));
    }

    public string WriteToString(OpenApiDocument document, OpenApiWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        return YamlText.Write(Convert(document, options));
    }

    private static YamlStream Convert(OpenApiDocument document, OpenApiWriterOptions? options)
    {
        options ??= new OpenApiWriterOptions();
        var node = OpenApiModelToNodeConverter.Convert(document, options.Version ?? document.SpecVersion);
        return new YamlStream(new YamlDocument(OpenApiYamlNodeFormatter.Write(node)));
    }
}
