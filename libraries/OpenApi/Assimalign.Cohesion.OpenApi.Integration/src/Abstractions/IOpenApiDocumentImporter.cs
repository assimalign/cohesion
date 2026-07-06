namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>
/// Imports an external OpenApi description into the canonical model. This is the ApiManager consumption
/// contract: a management layer parses third-party descriptions through this seam without depending on
/// the serialization internals.
/// </summary>
public interface IOpenApiDocumentImporter
{
    /// <summary>
    /// Imports a document from its serialized form.
    /// </summary>
    /// <param name="content">The serialized document.</param>
    /// <param name="format">The wire format of <paramref name="content"/>.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="OpenApiException">Thrown when the content cannot be parsed.</exception>
    OpenApiDocument Import(string content, OpenApiFormat format);
}
