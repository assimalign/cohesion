using System.IO;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Writes an <see cref="OpenApiDocument"/> to a serialized representation for a target OpenAPI line.
/// </summary>
public interface IOpenApiWriter
{
    /// <summary>
    /// Writes the document to a stream.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="stream">The destination stream.</param>
    /// <param name="options">Options controlling target version and formatting, or <see langword="null"/> for defaults.</param>
    void Write(OpenApiDocument document, Stream stream, OpenApiWriterOptions? options = null);

    /// <summary>
    /// Writes the document to a string.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="options">Options controlling target version and formatting, or <see langword="null"/> for defaults.</param>
    /// <returns>The serialized document.</returns>
    string WriteToString(OpenApiDocument document, OpenApiWriterOptions? options = null);
}
