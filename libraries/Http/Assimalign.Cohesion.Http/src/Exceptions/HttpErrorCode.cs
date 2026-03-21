namespace Assimalign.Cohesion.Http;

/// <summary>
/// Defines common error categories for HTTP failures.
/// </summary>
public enum HttpErrorCode
{
    /// <summary>
    /// An unspecified HTTP error.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The incoming request could not be read.
    /// </summary>
    ReadingError,

    /// <summary>
    /// The outgoing response could not be written.
    /// </summary>
    WritingError,

    /// <summary>
    /// A request contained an invalid method token.
    /// </summary>
    InvalidMethod,

    /// <summary>
    /// A request contained an invalid path value.
    /// </summary>
    InvalidPath,

    /// <summary>
    /// A request or response contained an invalid header.
    /// </summary>
    InvalidHeader,

    /// <summary>
    /// The HTTP protocol was violated.
    /// </summary>
    ProtocolViolation,

    /// <summary>
    /// Application execution failed while processing the request.
    /// </summary>
    ExecutionError,
}
