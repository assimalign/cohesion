using System.IO;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Entry points for reading and writing OpenAPI documents as JSON.
/// </summary>
public static class OpenApiJson
{
    /// <summary>
    /// Parses an OpenAPI document from a JSON string.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="OpenApiException">Thrown when the input is not a well-formed OpenAPI document.</exception>
    public static OpenApiDocument Parse(string json) => new OpenApiJsonReader().Read(json);

    /// <summary>
    /// Parses an OpenAPI document from a stream of JSON.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="OpenApiException">Thrown when the input is not a well-formed OpenAPI document.</exception>
    public static OpenApiDocument Parse(Stream stream) => new OpenApiJsonReader().Read(stream);

    /// <summary>
    /// Serializes an OpenAPI document to a JSON string.
    /// </summary>
    /// <param name="document">The document to serialize.</param>
    /// <param name="version">The target OpenAPI line, or <see langword="null"/> to use the document's own version.</param>
    /// <param name="indented">Whether the output is indented for readability.</param>
    /// <returns>The serialized JSON.</returns>
    public static string Serialize(OpenApiDocument document, OpenApiSpecVersion? version = null, bool indented = true) =>
        new OpenApiJsonWriter().WriteToString(document, new OpenApiWriterOptions { Version = version, Indented = indented });

    /// <summary>
    /// Writes an OpenAPI document as JSON to a stream.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="stream">The destination stream.</param>
    /// <param name="version">The target OpenAPI line, or <see langword="null"/> to use the document's own version.</param>
    /// <param name="indented">Whether the output is indented for readability.</param>
    public static void Write(OpenApiDocument document, Stream stream, OpenApiSpecVersion? version = null, bool indented = true) =>
        new OpenApiJsonWriter().Write(document, stream, new OpenApiWriterOptions { Version = version, Indented = indented });
}
