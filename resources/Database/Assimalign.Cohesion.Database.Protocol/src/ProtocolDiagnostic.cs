namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>A model-independent wire diagnostic with optional source offsets.</summary>
/// <param name="Severity">Warning or Error.</param>
/// <param name="Code">The engine or parser code.</param>
/// <param name="Message">The diagnostic text.</param>
/// <param name="Start">The inclusive source offset, or null.</param>
/// <param name="End">The exclusive source offset, or null.</param>
/// <param name="Line">The one-based source line.</param>
public sealed record ProtocolDiagnostic(ProtocolDiagnosticSeverity Severity, string Code, string Message, int? Start, int? End, int Line);
