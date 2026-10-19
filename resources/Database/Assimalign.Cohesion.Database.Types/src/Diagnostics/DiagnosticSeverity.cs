namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// The severity of a <see cref="Diagnostic"/>.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>No severity was assigned.</summary>
    None,

    /// <summary>A problem that does not stop the request.</summary>
    Warning,

    /// <summary>A problem that stops the request.</summary>
    Error,

    /// <summary>A suggested change that is not a problem.</summary>
    Suggestion,

    /// <summary>Information about the request.</summary>
    Information,

    /// <summary>A diagnostic that is not shown to the user.</summary>
    Hidden
}
