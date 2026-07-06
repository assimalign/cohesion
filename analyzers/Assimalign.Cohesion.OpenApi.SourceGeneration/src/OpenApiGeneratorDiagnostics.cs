using Microsoft.CodeAnalysis;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration;

/// <summary>
/// The compiler diagnostics the OpenApi metadata generator reports. The identifiers mirror the runtime
/// mapper's <c>OpenApiMetadataDiagnosticCodes</c> so compile-time generation and runtime mapping agree
/// on rule identity.
/// </summary>
internal static class OpenApiGeneratorDiagnostics
{
    private const string Category = "OpenApi";

    internal static readonly DiagnosticDescriptor MissingPath = new(
        "OPENAPIATTR0001",
        "OpenApi operation is missing a path",
        "The [OpenApiOperation] on '{0}' declares an empty path",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor AmbiguousSchema = new(
        "OPENAPIATTR0002",
        "OpenApi body declares an ambiguous schema",
        "The body on '{0}' declares both a model type and an explicit schema reference; use one",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor PathParameterRequired = new(
        "OPENAPIATTR0003",
        "OpenApi path parameter should be required",
        "Path parameter '{0}' is not marked required; it has been generated as required",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor AmbiguousExample = new(
        "OPENAPIATTR0004",
        "OpenApi example declares conflicting values",
        "Example '{0}' declares both an embedded value and an external value",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor IncompleteApiKey = new(
        "OPENAPIATTR0006",
        "OpenApi API key scheme is incomplete",
        "API key security scheme '{0}' requires both a parameter name and a location",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>Resolves a descriptor by its id for reporting a captured <see cref="DiagnosticInfo"/>.</summary>
    /// <param name="id">The diagnostic id.</param>
    /// <returns>The matching descriptor, or the missing-path descriptor as a fallback.</returns>
    internal static DiagnosticDescriptor GetDescriptor(string id) => id switch
    {
        "OPENAPIATTR0002" => AmbiguousSchema,
        "OPENAPIATTR0003" => PathParameterRequired,
        "OPENAPIATTR0004" => AmbiguousExample,
        "OPENAPIATTR0006" => IncompleteApiKey,
        _ => MissingPath
    };
}
