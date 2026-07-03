using System.IO;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Entry points for reading and writing OpenAPI documents as YAML.
/// </summary>
public static class OpenApiYaml
{
    /// <summary>
    /// Parses an OpenAPI document from a YAML string.
    /// </summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="OpenApiException">Thrown when the input is not a well-formed OpenAPI document.</exception>
    public static OpenApiDocument Parse(string yaml) => new OpenApiYamlReader().Read(yaml);

    /// <summary>
    /// Parses an OpenAPI document from a stream of YAML, detecting the Unicode encoding from its
    /// leading bytes.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="OpenApiException">Thrown when the input is not a well-formed OpenAPI document.</exception>
    public static OpenApiDocument Parse(Stream stream) => new OpenApiYamlReader().Read(stream);

    /// <summary>
    /// Serializes an OpenAPI document to a YAML string.
    /// </summary>
    /// <param name="document">The document to serialize.</param>
    /// <param name="version">The target OpenAPI line, or <see langword="null"/> to use the document's own version.</param>
    /// <returns>The serialized YAML.</returns>
    public static string Serialize(OpenApiDocument document, OpenApiSpecVersion? version = null) =>
        new OpenApiYamlWriter().WriteToString(document, new OpenApiWriterOptions { Version = version });

    /// <summary>
    /// Writes an OpenAPI document as UTF-8 YAML to a stream.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="stream">The destination stream.</param>
    /// <param name="version">The target OpenAPI line, or <see langword="null"/> to use the document's own version.</param>
    public static void Write(OpenApiDocument document, Stream stream, OpenApiSpecVersion? version = null) =>
        new OpenApiYamlWriter().Write(document, stream, new OpenApiWriterOptions { Version = version });
}
