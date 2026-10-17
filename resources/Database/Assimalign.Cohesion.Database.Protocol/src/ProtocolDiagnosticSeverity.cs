namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>The severities admitted by the Diagnostics wire message.</summary>
public enum ProtocolDiagnosticSeverity : byte
{
    /// <summary>A nonfatal statement problem.</summary>
    Warning = 1,
    /// <summary>A statement failure.</summary>
    Error = 2,
}
