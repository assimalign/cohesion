using System.IO;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Reads a serialized OpenAPI description into an <see cref="OpenApiDocument"/>.
/// </summary>
public interface IOpenApiReader
{
    /// <summary>
    /// Reads a document from a stream.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="OpenApiException">Thrown when the input is not a well-formed OpenAPI document.</exception>
    OpenApiDocument Read(Stream stream);

    /// <summary>
    /// Reads a document from a string.
    /// </summary>
    /// <param name="text">The serialized document text.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="OpenApiException">Thrown when the input is not a well-formed OpenAPI document.</exception>
    OpenApiDocument Read(string text);
}
