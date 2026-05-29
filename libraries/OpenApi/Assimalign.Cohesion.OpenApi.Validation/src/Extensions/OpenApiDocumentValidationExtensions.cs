namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// Ergonomic validation members on <see cref="OpenApiDocument"/> that delegate to <see cref="OpenApiValidation"/>.
/// </summary>
public static class OpenApiDocumentValidationExtensions
{
    extension(OpenApiDocument document)
    {
        /// <summary>Validates the document with the default rule pipeline.</summary>
        /// <returns>The validation result.</returns>
        public OpenApiValidationResult Validate() => OpenApiValidation.Validate(document);
    }
}
