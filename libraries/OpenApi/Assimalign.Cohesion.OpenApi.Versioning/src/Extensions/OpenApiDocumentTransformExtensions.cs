namespace Assimalign.Cohesion.OpenApi.Versioning;

/// <summary>
/// Convenience extensions for transforming a document between OpenAPI lines.
/// </summary>
public static class OpenApiDocumentTransformExtensions
{
    extension(OpenApiDocument document)
    {
        /// <summary>
        /// Transforms the document to a target OpenAPI line, returning a copy and the findings raised.
        /// </summary>
        /// <param name="target">The line to target.</param>
        /// <returns>The transform result.</returns>
        public OpenApiTransformResult TransformTo(OpenApiSpecVersion target) =>
            OpenApiVersionTransformer.Transform(document, target);
    }
}
