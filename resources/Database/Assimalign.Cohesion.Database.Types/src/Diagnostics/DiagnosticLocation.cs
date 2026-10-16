namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// How a <see cref="Diagnostic"/>'s position relates to the source text.
/// </summary>
public enum DiagnosticLocation
{
    /// <summary>
    /// The diagnostic location is known with absolute start and length values.
    /// </summary>
    Absolute,

    /// <summary>
    /// The diagnostic location is unknown, but relative to the syntax item it is associated with.
    /// </summary>
    Relative,

    /// <summary>
    /// The diagnostic location is unknown, but after the end of the syntax item it is associated with.
    /// </summary>
    RelativeEnd
}
