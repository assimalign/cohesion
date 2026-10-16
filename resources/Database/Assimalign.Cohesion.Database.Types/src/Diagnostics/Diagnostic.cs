using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// A diagnostic a database model reports while it parses or executes a request: a code, a
/// message and a severity, and, for a diagnostic found in statement text, its position in that
/// text.
/// </summary>
/// <remarks>
/// It lives in <c>Database.Types</c>, below both <c>Database.Language</c> and
/// <c>Database.Execution</c>, so a result's diagnostics (<c>DatabaseResult.Diagnostics</c>) reach no
/// language assembly. A language model's parser sets the position members; a diagnostic with no
/// statement position (an execution warning, or a typed request's refusal) leaves
/// <see cref="Start"/> and <see cref="End"/> null, while <see cref="Line"/> keeps its default of 1.
/// </remarks>
[DebuggerDisplay("{Severity}: ({Start}..{Start+Length}): {Message}")]
public sealed partial class Diagnostic
{
    /// <summary>
    /// Initializes a diagnostic whose members are set through their initializers.
    /// </summary>
    public Diagnostic() { }

    /// <summary>
    /// Initializes a diagnostic with a code, a message, a position in the source text and a
    /// severity.
    /// </summary>
    /// <param name="code">The diagnostic code.</param>
    /// <param name="message">The message, or null.</param>
    /// <param name="start">The offset in the source text where the diagnostic starts.</param>
    /// <param name="end">The offset in the source text where the diagnostic ends.</param>
    /// <param name="severity">The diagnostic's severity.</param>
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
    /// How <see cref="Start"/> and <see cref="End"/> relate to the source text or to the syntax item
    /// the diagnostic is associated with; null when the diagnostic does not say.
    /// </summary>
    public DiagnosticLocation? Location { get; init; }

    /// <summary>
    /// The diagnostic's message, or null when it has none.
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
    /// The diagnostic's severity, or null when it was not set.
    /// </summary>
    public DiagnosticSeverity? Severity { get; init; }
}
