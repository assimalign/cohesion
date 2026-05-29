using System.IO;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Ergonomic serialization members on <see cref="OpenApiDocument"/> that delegate to <see cref="OpenApiJson"/>.
/// </summary>
public static class OpenApiDocumentSerializationExtensions
{
    extension(OpenApiDocument document)
    {
        /// <summary>
        /// Serializes the document to a JSON string.
        /// </summary>
        /// <param name="version">The target OpenAPI line, or <see langword="null"/> to use the document's own version.</param>
        /// <param name="indented">Whether the output is indented for readability.</param>
        /// <returns>The serialized JSON.</returns>
        public string ToJson(OpenApiSpecVersion? version = null, bool indented = true) =>
            OpenApiJson.Serialize(document, version, indented);

        /// <summary>
        /// Writes the document as JSON to a stream.
        /// </summary>
        /// <param name="stream">The destination stream.</param>
        /// <param name="version">The target OpenAPI line, or <see langword="null"/> to use the document's own version.</param>
        /// <param name="indented">Whether the output is indented for readability.</param>
        public void WriteJson(Stream stream, OpenApiSpecVersion? version = null, bool indented = true) =>
            OpenApiJson.Write(document, stream, version, indented);
    }
}
