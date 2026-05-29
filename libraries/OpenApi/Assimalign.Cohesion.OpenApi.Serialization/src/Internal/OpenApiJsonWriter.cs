using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// The JSON implementation of <see cref="IOpenApiWriter"/>. Composes the version-aware model-to-node
/// mapping with the JSON node formatter.
/// </summary>
internal sealed class OpenApiJsonWriter : IOpenApiWriter
{
    public void Write(OpenApiDocument document, Stream stream, OpenApiWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);

        options ??= new OpenApiWriterOptions();
        var node = OpenApiModelToNodeConverter.Convert(document, options.Version ?? document.SpecVersion);

        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = options.Indented });
        OpenApiJsonNodeFormatter.Write(writer, node);
        writer.Flush();
    }

    public string WriteToString(OpenApiDocument document, OpenApiWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        options ??= new OpenApiWriterOptions();
        var node = OpenApiModelToNodeConverter.Convert(document, options.Version ?? document.SpecVersion);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = options.Indented }))
        {
            OpenApiJsonNodeFormatter.Write(writer, node);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
