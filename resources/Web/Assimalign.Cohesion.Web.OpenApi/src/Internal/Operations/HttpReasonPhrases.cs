using System.Globalization;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// The RFC 9110 §15 reason phrases, used as a response's description when the endpoint gives none.
/// </summary>
/// <remarks>
/// OpenAPI requires a response <c>description</c> (before 3.2). The registered phrase is the honest
/// default: it says what the status means and nothing the endpoint did not declare. The table follows
/// RFC 9110 rather than <c>HttpStatusCode.ToString()</c>, whose wording predates it in places
/// (<c>Ok</c>, <c>Request Entity Too Large</c>).
/// </remarks>
internal static class HttpReasonPhrases
{
    /// <summary>
    /// Gets the reason phrase for a status code.
    /// </summary>
    /// <param name="statusCode">The status code.</param>
    /// <returns>The registered phrase, or <c>Status {code}</c> for an unregistered code.</returns>
    public static string Get(int statusCode) => statusCode switch
    {
        100 => "Continue",
        101 => "Switching Protocols",
        103 => "Early Hints",
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        203 => "Non-Authoritative Information",
        204 => "No Content",
        205 => "Reset Content",
        206 => "Partial Content",
        207 => "Multi-Status",
        208 => "Already Reported",
        300 => "Multiple Choices",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        402 => "Payment Required",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        407 => "Proxy Authentication Required",
        408 => "Request Timeout",
        409 => "Conflict",
        410 => "Gone",
        411 => "Length Required",
        412 => "Precondition Failed",
        413 => "Content Too Large",
        414 => "URI Too Long",
        415 => "Unsupported Media Type",
        416 => "Range Not Satisfiable",
        417 => "Expectation Failed",
        421 => "Misdirected Request",
        422 => "Unprocessable Content",
        423 => "Locked",
        424 => "Failed Dependency",
        425 => "Too Early",
        426 => "Upgrade Required",
        428 => "Precondition Required",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        451 => "Unavailable For Legal Reasons",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        505 => "HTTP Version Not Supported",
        506 => "Variant Also Negotiates",
        507 => "Insufficient Storage",
        508 => "Loop Detected",
        511 => "Network Authentication Required",
        _ => "Status " + statusCode.ToString(CultureInfo.InvariantCulture)
    };
}
