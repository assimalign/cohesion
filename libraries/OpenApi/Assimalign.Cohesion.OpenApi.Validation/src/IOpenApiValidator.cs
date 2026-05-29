namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// Validates an <see cref="OpenApiDocument"/> and returns the diagnostics produced.
/// </summary>
public interface IOpenApiValidator
{
    /// <summary>Runs the validation pipeline against a document.</summary>
    /// <param name="document">The document to validate.</param>
    /// <returns>The validation result, including all diagnostics.</returns>
    OpenApiValidationResult Validate(OpenApiDocument document);
}
