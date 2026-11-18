namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// How serious a <see cref="FabricDiagnostic"/> is.
/// </summary>
public enum FabricDiagnosticSeverity
{
    /// <summary>
    /// Informational; never blocks.
    /// </summary>
    Info,

    /// <summary>
    /// Accepted, but reported, such as a deprecated API version.
    /// </summary>
    Warning,

    /// <summary>
    /// Refused: emit exits non-zero, admission returns <c>ValidationFailed</c>, preflight fails the step.
    /// </summary>
    Error,
}
