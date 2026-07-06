namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>
/// Exports a canonical model to a serialized OpenApi description, optionally targeting a different
/// OpenAPI line than the document declares. This is the ApiManager production contract: a management
/// layer emits descriptions through this seam, and the version retarget reuses the transform pipeline so
/// a lossy retarget is reported rather than silent.
/// </summary>
public interface IOpenApiDocumentExporter
{
    /// <summary>
    /// Exports a document to a serialized form for the line it declares.
    /// </summary>
    /// <param name="document">The document to export.</param>
    /// <param name="format">The target wire format.</param>
    /// <returns>The serialized document.</returns>
    string Export(OpenApiDocument document, OpenApiFormat format);

    /// <summary>
    /// Exports a document retargeted to a specific OpenAPI line, returning the serialized result together
    /// with any diagnostics raised while retargeting.
    /// </summary>
    /// <param name="document">The document to export.</param>
    /// <param name="format">The target wire format.</param>
    /// <param name="version">The OpenAPI line to target.</param>
    /// <returns>The serialized document and the retarget diagnostics.</returns>
    OpenApiExportResult Export(OpenApiDocument document, OpenApiFormat format, OpenApiSpecVersion version);
}
