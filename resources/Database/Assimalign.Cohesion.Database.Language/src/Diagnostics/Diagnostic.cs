using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Assimalign.Cohesion.Database.Language;

[DebuggerDisplay("{Severity}: ({Start}..{Start+Length}): {Message}")]
public sealed partial class Diagnostic
{
    public Diagnostic() { }
    public Diagnostic(string code, string? message, int start, int end, DiagnosticSeverity severity)
    {
        Code = code;
        Message = message;
        Start = start;
        End = end;
        Severity = severity;
    }


    /// <summary>
    /// The diagnostic code.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// 
    /// </summary>
    public DiagnosticLocation? Location { get; init; }

    /// <summary>
    /// 
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// The length of the error.
    /// </summary>
    public int? Length => End - Start;

    /// <summary>
    /// Start of diagnostic location in the source.
    /// </summary>
    public int? Start { get; init; }

    /// <summary>
    /// End of diagnostic location in the source.
    /// </summary>
    public int? End { get; init; }

    /// <summary>
    /// The line number of the diagnostics. Default is 1.
    /// </summary>
    public int Line { get; init; } = 1;

    /// <summary>
    /// 
    /// </summary>
    public DiagnosticSeverity? Severity { get; init; }
}
