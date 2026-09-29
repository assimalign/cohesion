namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// A single validation rule. Implementations inspect <see cref="OpenApiValidationContext.Document"/> and
/// report findings through the context. Rules compose into the <see cref="IOpenApiValidator"/> pipeline.
/// </summary>
/// <remarks>
/// The official-schema conformance stage is intended to be implemented as a rule and added to the pipeline
/// through <see cref="OpenApiValidation.Create(System.Collections.Generic.IEnumerable{IOpenApiValidationRule})"/>;
/// the built-in pipeline covers structural and semantic validation only.
/// </remarks>
public interface IOpenApiValidationRule
{
    /// <summary>Validates the document, reporting any findings to the context.</summary>
    /// <param name="context">The validation context for the current pass.</param>
    void Validate(OpenApiValidationContext context);
}
