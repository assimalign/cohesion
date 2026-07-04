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
    /// A request contained a malformed request-target (RFC 9112 &#167; 3.2). Includes
    /// targets that do not match any of the four canonical forms (origin / absolute /
    /// authority / asterisk) and targets that violate the method/form pairing rules.
    /// </summary>
    InvalidRequestTarget,

    /// <summary>
    /// A request or response contained an invalid header.
    /// </summary>
    InvalidHeader,

    /// <summary>
    /// A field value could not be parsed or serialized as an RFC 9651 Structured
    /// Field Value (Item, List, or Dictionary). Includes bare items that violate
    /// their range or syntax rules and fields that fail strict fail-parsing.
    /// </summary>
    InvalidStructuredField,

    /// <summary>
    /// The HTTP protocol was violated.
    /// </summary>
    ProtocolViolation,

    /// <summary>
    /// Application execution failed while processing the request.
    /// </summary>
    ExecutionError,
}
