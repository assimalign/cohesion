using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// Entry points for validating OpenAPI documents. The default pipeline runs structural, version-placement,
/// and semantic rules; additional rules — such as an official-schema conformance stage — can be composed in
/// through <see cref="Create(IEnumerable{IOpenApiValidationRule})"/>.
/// </summary>
public static class OpenApiValidation
{
    /// <summary>Validates a document with the default rule pipeline.</summary>
    /// <param name="document">The document to validate.</param>
    /// <returns>The validation result.</returns>
    public static OpenApiValidationResult Validate(OpenApiDocument document) => CreateDefault().Validate(document);

    /// <summary>Creates a validator that runs the default rule pipeline.</summary>
    /// <returns>A validator configured with the built-in rules.</returns>
    public static IOpenApiValidator CreateDefault() => new OpenApiValidator(DefaultRules());

    /// <summary>Creates a validator that runs the supplied rules.</summary>
    /// <param name="rules">The rules to run, in order.</param>
    /// <returns>A validator configured with <paramref name="rules"/>.</returns>
    public static IOpenApiValidator Create(IEnumerable<IOpenApiValidationRule> rules) => new OpenApiValidator(rules);

    /// <summary>Gets a fresh list of the built-in validation rules, for composing custom pipelines.</summary>
    /// <returns>The structural, version-placement, and semantic rules.</returns>
    public static IReadOnlyList<IOpenApiValidationRule> DefaultRules() =>
    [
        new StructuralValidationRule(),
        new VersionPlacementRule(),
        new SemanticValidationRule()
    ];
}
