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

    /// <summary>
    /// Creates the official-schema conformance stage: the document is serialized for its declared
    /// version and evaluated against the vendored official OpenAPI meta-schema for that line
    /// (3.0: <c>schema/2024-10-18</c>; 3.1 and 3.2: <c>schema/2025-11-23</c>). Findings are
    /// <see cref="OpenApiDiagnosticSeverity.Warning"/> diagnostics with code
    /// <see cref="OpenApiValidationRuleCodes.SchemaViolation"/>, because the specification text — not
    /// the schema files — is authoritative. The stage is not part of the default pipeline; compose it
    /// in with <c>Create([.. DefaultRules(), CreateOfficialSchemaRule()])</c>.
    /// </summary>
    /// <returns>The pluggable official-schema rule.</returns>
    public static IOpenApiValidationRule CreateOfficialSchemaRule() => new OpenApiSchemaConformanceRule();
}
