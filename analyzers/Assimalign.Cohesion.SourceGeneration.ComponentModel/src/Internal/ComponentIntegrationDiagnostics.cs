using Microsoft.CodeAnalysis;

namespace Assimalign.Cohesion.SourceGeneration.ComponentModel.Internal;

/// <summary>
/// Compiler diagnostics reported by the component-integration generator.
/// </summary>
internal static class ComponentIntegrationDiagnostics
{
    private const string Category = "ComponentModel";

    internal static readonly DiagnosticDescriptor MalformedDeclaration = new(
        "COHCMP0001",
        "Component integration declaration is malformed",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InaccessibleFactory = new(
        "COHCMP0002",
        "Component factory is not externally visible",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedFactoryShape = new(
        "COHCMP0003",
        "Component factory has an unsupported shape",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateProjection = new(
        "COHCMP0004",
        "Component projection duplicates an earlier declaration",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor MissingSeam = new(
        "COHCMP0005",
        "Component integration seam is not referenced",
        "{0}",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedLanguageVersion = new(
        "COHCMP0006",
        "Component integrations require C# 14",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DisposableInstance = new(
        "COHCMP0007",
        "Disposable component is returned as an instance",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>Resolves a descriptor by its id for reporting a captured diagnostic.</summary>
    /// <param name="id">The diagnostic id.</param>
    /// <returns>The matching descriptor, or the malformed-declaration descriptor as a fallback.</returns>
    internal static DiagnosticDescriptor GetDescriptor(string id) => id switch
    {
        "COHCMP0002" => InaccessibleFactory,
        "COHCMP0003" => UnsupportedFactoryShape,
        "COHCMP0004" => DuplicateProjection,
        "COHCMP0005" => MissingSeam,
        "COHCMP0006" => UnsupportedLanguageVersion,
        "COHCMP0007" => DisposableInstance,
        _ => MalformedDeclaration
    };
}
