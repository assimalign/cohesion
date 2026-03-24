namespace Assimalign.Cohesion.Database.Language;

public enum DiagnosticLocation
{
    // <summary>
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
