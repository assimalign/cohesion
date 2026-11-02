using Microsoft.CodeAnalysis;

namespace Assimalign.Cohesion.SourceGeneration.Internal;

/// <summary>
/// Compiler diagnostics reported by the in-process program generators.
/// </summary>
internal static class InProcessProgramDiagnostics
{
    private const string Category = "ApplicationModel";

    internal static readonly DiagnosticDescriptor ProgramMissing = new(
        "COHAPM0001",
        "Resource reference has no in-process program",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ProviderMissing = new(
        "COHAPM0002",
        "In-process resource has no provider",
        "{0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ResourceDuplicate = new(
        "COHAPM0003",
        "In-process resource is bound twice",
        "{0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>Resolves a descriptor by its id for reporting a captured diagnostic.</summary>
    /// <param name="id">The diagnostic id.</param>
    /// <returns>The matching descriptor.</returns>
    internal static DiagnosticDescriptor GetDescriptor(string id) => id switch
    {
        "COHAPM0002" => ProviderMissing,
        "COHAPM0003" => ResourceDuplicate,
        _ => ProgramMissing
    };
}
