namespace Assimalign.Cohesion.Http;

/// <summary>
/// The outcome of applying a <c>Range</c> header to a representation (RFC 9110 &#167; 14.2), and the
/// response status it maps to.
/// </summary>
public enum HttpRangeSelectionStatus
{
    /// <summary>
    /// The range was ignored and the entire representation should be served with <c>200 OK</c>.
    /// A server chooses this when a range request is present but not worth honoring — for example a
    /// range set larger than the configured limit (a denial-of-service guard permitted by
    /// RFC 9110 &#167; 14.2).
    /// </summary>
    Full = 0,

    /// <summary>
    /// At least one range is satisfiable; serve <c>206 Partial Content</c> using the selected
    /// <see cref="HttpRangeSelection.Slices"/>.
    /// </summary>
    Partial,

    /// <summary>
    /// No range is satisfiable; serve <c>416 Range Not Satisfiable</c> with the
    /// <see cref="HttpRangeSelection.UnsatisfiedContentRange"/> (<c>bytes */N</c>).
    /// </summary>
    Unsatisfiable,
}
