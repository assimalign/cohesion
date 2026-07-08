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
    /// A media type or media range (RFC 9110 &#167; 8.3.1) could not be parsed.
    /// </summary>
    InvalidMediaType,
    /// A field value could not be parsed or serialized as an RFC 9651 Structured
    /// Field Value (Item, List, or Dictionary). Includes bare items that violate
    /// their range or syntax rules and fields that fail strict fail-parsing.
    /// </summary>
    InvalidStructuredField,

    /// <summary>
    /// A <c>Cache-Control</c> field value (RFC 9111 &#167; 5.2) could not be parsed.
    /// </summary>
    InvalidCacheControl,

    /// <summary>
    /// An entity-tag or an <c>If-Match</c> / <c>If-None-Match</c> condition (RFC 9110 &#167; 8.8.3)
    /// could not be parsed.
    /// </summary>
    InvalidEntityTag,

    /// <summary>
    /// The HTTP protocol was violated.
    /// </summary>
    ProtocolViolation,

    /// <summary>
    /// Application execution failed while processing the request.
    /// </summary>
    ExecutionError,

    /// <summary>
    /// A <c>Range</c> header value (RFC 9110 &#167; 14.1.1) could not be parsed — an
    /// unrecognized range unit or a malformed range set.
    /// </summary>
    InvalidRange,

    /// <summary>
    /// A <c>Content-Range</c> header value (RFC 9110 &#167; 14.4) could not be parsed.
    /// </summary>
    InvalidContentRange,

    /// <summary>
    /// An <c>If-Range</c> conditional-request header (RFC 9110 &#167; 13.1.5) could not be parsed.
    /// </summary>
    InvalidConditional,

    /// <summary>
    /// An RFC 7239 <c>Forwarded</c> element (or one of its <c>for</c>/<c>by</c> nodes) or an
    /// <c>X-Forwarded-*</c> list could not be parsed.
    /// </summary>
    InvalidForwarded,
}
