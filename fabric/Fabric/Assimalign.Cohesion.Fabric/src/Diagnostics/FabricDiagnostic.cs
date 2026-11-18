namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One finding from validation, at emit, admission, or preflight.
/// </summary>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Code">A stable PascalCase code, such as <c>CapabilityUnsupported</c>.</param>
/// <param name="Message">A sentence that names the resource and the field.</param>
/// <param name="Path">The dotted path of the offending field, such as <c>spec.replicas</c>, if the finding is about one.</param>
public sealed record FabricDiagnostic(FabricDiagnosticSeverity Severity, string Code, string Message, string? Path = null);
