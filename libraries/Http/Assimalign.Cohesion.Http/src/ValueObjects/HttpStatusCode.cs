using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Internal;

/// <summary>
/// An HTTP status code (RFC 9110 &#167; 15) and its reason phrase.
/// </summary>
/// <remarks>
/// <para>
/// The known codes are the IANA HTTP Status Code Registry's assigned codes, each carrying the phrase
/// the registry gives it: the RFC 9110 &#167; 15 phrase for the codes RFC 9110 defines, and the
/// defining specification's phrase for the rest (<c>102</c>, <c>103</c>, <c>207</c>, <c>208</c>,
/// <c>226</c>, <c>423</c>&#8211;<c>425</c>, <c>428</c>, <c>429</c>, <c>431</c>, <c>451</c>,
/// <c>506</c>&#8211;<c>508</c>, <c>510</c>, <c>511</c>). The registry marks <c>510</c>
/// "Not Extended (OBSOLETED)", after RFC 2774 moved to Historic; that annotation is not part of the
/// phrase, so <c>510</c> carries <c>Not Extended</c>. HTTP/1.1 writes the phrase on the status line,
/// and HTTP/2 and HTTP/3 carry no phrase at all.
/// </para>
/// <para>
/// Reserved codes are not included: the reserved <c>418</c> (RFC 9110 &#167; 15.5.19) and the
/// registry's temporary registrations are unknown. The reserved <c>306</c> (RFC 9110 &#167; 15.4.7)
/// is the one exception: it stays known, with the registry's <c>(Unused)</c>, only because
/// <see cref="Unused"/> shipped in 10.0.0-preview.1, and dropping the code would make that accessor
/// throw. <see cref="Unused"/> is obsolete instead.
/// </para>
/// <para>
/// Only a known code can be constructed: <see cref="IsValid(int)"/> rejects every other value. RFC 9110
/// &#167; 15 allows any three-digit code from <c>100</c> to <c>599</c>, and a recipient treats an
/// unknown one as the <c>x00</c> code of its class, so an application cannot send an unregistered or
/// private code through this type.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString()}")]
public readonly struct HttpStatusCode : IEquatable<HttpStatusCode>
{
    // The IANA HTTP Status Code Registry's phrases, which are RFC 9110 §15's for the codes it defines.
    private static readonly KeyValuePair<int, string>[] _statusCodes =
    {
        // 1xx Informational
        new (100, "Continue"),
        new (101, "Switching Protocols"),
        new (102, "Processing"),                        // RFC 2518
        new (103, "Early Hints"),                       // RFC 8297
        // 2xx Successful
        new (200, "OK"),
        new (201, "Created"),
        new (202, "Accepted"),
        new (203, "Non-Authoritative Information"),
        new (204, "No Content"),
        new (205, "Reset Content"),
        new (206, "Partial Content"),
        new (207, "Multi-Status"),                      // RFC 4918
        new (208, "Already Reported"),                  // RFC 5842
        new (226, "IM Used"),                           // RFC 3229
        // 3xx Redirection
        new (300, "Multiple Choices"),
        new (301, "Moved Permanently"),
        new (302, "Found"),
        new (303, "See Other"),
        new (304, "Not Modified"),
        new (305, "Use Proxy"),
        new (306, "(Unused)"),                          // reserved; kept only because Unused shipped in 10.0.0-preview.1
        new (307, "Temporary Redirect"),
        new (308, "Permanent Redirect"),
        // 4xx Client Error
        new (400, "Bad Request"),
        new (401, "Unauthorized"),
        new (402, "Payment Required"),
        new (403, "Forbidden"),
        new (404, "Not Found"),
        new (405, "Method Not Allowed"),
        new (406, "Not Acceptable"),
        new (407, "Proxy Authentication Required"),
        new (408, "Request Timeout"),
        new (409, "Conflict"),
        new (410, "Gone"),
        new (411, "Length Required"),
        new (412, "Precondition Failed"),
        new (413, "Content Too Large"),
        new (414, "URI Too Long"),
        new (415, "Unsupported Media Type"),
        new (416, "Range Not Satisfiable"),
        new (417, "Expectation Failed"),
        new (421, "Misdirected Request"),
        new (422, "Unprocessable Content"),
        new (423, "Locked"),                            // RFC 4918
        new (424, "Failed Dependency"),                 // RFC 4918
        new (425, "Too Early"),                         // RFC 8470
        new (426, "Upgrade Required"),
        new (428, "Precondition Required"),             // RFC 6585
        new (429, "Too Many Requests"),                 // RFC 6585
        new (431, "Request Header Fields Too Large"),   // RFC 6585
        new (451, "Unavailable For Legal Reasons"),     // RFC 7725
        // 5xx Server Error
        new (500, "Internal Server Error"),
        new (501, "Not Implemented"),
        new (502, "Bad Gateway"),
        new (503, "Service Unavailable"),
        new (504, "Gateway Timeout"),
        new (505, "HTTP Version Not Supported"),
        new (506, "Variant Also Negotiates"),           // RFC 2295
        new (507, "Insufficient Storage"),              // RFC 4918
        new (508, "Loop Detected"),                     // RFC 5842
        new (510, "Not Extended"),                      // RFC 2774 (Historic); the registry's "(OBSOLETED)" is an annotation, not phrase
        new (511, "Network Authentication Required"),   // RFC 6585
    };

    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="statusCode">The numeric HTTP status code.</param>
    public HttpStatusCode(int statusCode)
    {
        ArgumentException.ThrowIf(!IsValid(statusCode), $"The provided status code is invalid: '{statusCode}'");

        Value = statusCode;
    }

    /// <summary>
    /// The raw HTTP Status Code.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// Gets the code's registered reason phrase, such as <c>OK</c> or <c>Content Too Large</c>
    /// (RFC 9110 &#167; 15), or an empty string for a default-constructed instance.
    /// </summary>
    public string ReasonPhrase
    {
        get
        {
            for (int i = 0; i < _statusCodes.Length; i++)
            {
                if (_statusCodes[i].Key == Value)
                {
                    return _statusCodes[i].Value;
                }
            }

            return string.Empty;
        }
    }

    /// <inheritdoc />
    public bool Equals(HttpStatusCode other)
    {
        return other.Value == Value;
    }

    #region Overloads

    /// <summary>
    /// Renders the code and its reason phrase as an HTTP/1.1 status line carries them, for example
    /// <c>200 OK</c>.
    /// </summary>
    /// <returns>The code and reason phrase, or an empty string for a default-constructed instance.</returns>
    public override string ToString()
    {
        string reasonPhrase = ReasonPhrase;

        return reasonPhrase.Length == 0 ? string.Empty : Value + " " + reasonPhrase;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is HttpStatusCode statusCode)
        {
            return Equals(statusCode);
        }
        return false;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(typeof(HttpStatusCode), Value);
    }
    #endregion

    #region Operators

    /// <summary>
    /// Converts a numeric status code into a <see cref="HttpStatusCode"/> value.
    /// </summary>
    /// <param name="statusCode">The numeric HTTP status code.</param>
    public static implicit operator HttpStatusCode(int statusCode)
    {
        return new HttpStatusCode(statusCode);
    }

    /// <summary>
    /// Converts an <see cref="HttpStatusCode"/> value into its numeric representation.
    /// </summary>
    /// <param name="statusCode">The status code to convert.</param>
    public static implicit operator int(HttpStatusCode statusCode)
    {
        return statusCode.Value;
    }

    #endregion

    #region Static Helpers
    public static HttpStatusCode Continue => 100;
    public static HttpStatusCode SwitchingProtocols => 101;
    public static HttpStatusCode Processing => 102;
    public static HttpStatusCode EarlyHints => 103;
    public static HttpStatusCode Ok => 200;
    public static HttpStatusCode Created => 201;
    public static HttpStatusCode Accepted => 202;
    public static HttpStatusCode NonAuthoritativeInformation => 203;
    public static HttpStatusCode NoContent => 204;
    public static HttpStatusCode ResetContent => 205;
    public static HttpStatusCode PartialContent => 206;
    public static HttpStatusCode MultiStatus => 207;
    public static HttpStatusCode AlreadyReported => 208;

    /// <summary>Gets <c>226 IM Used</c> (RFC 3229 &#167; 10.4.1).</summary>
    public static HttpStatusCode ImUsed => 226;
    public static HttpStatusCode MultipleChoices => 300;
    public static HttpStatusCode MovedPermanently => 301;
    public static HttpStatusCode Found => 302;
    public static HttpStatusCode SeeOther => 303;
    public static HttpStatusCode NotModified => 304;
    public static HttpStatusCode UseProxy => 305;

    /// <summary>Gets the reserved <c>306 (Unused)</c> (RFC 9110 &#167; 15.4.7), which no response should carry.</summary>
    [Obsolete("306 is reserved (RFC 9110 §15.4.7) and is no longer used.")]
    public static HttpStatusCode Unused => 306;
    public static HttpStatusCode RedirectKeepVerb => 307;
    public static HttpStatusCode PermanentRedirect => 308;
    public static HttpStatusCode BadRequest => 400;
    public static HttpStatusCode Unauthorized => 401;
    public static HttpStatusCode PaymentRequired => 402;
    public static HttpStatusCode Forbidden => 403;
    public static HttpStatusCode NotFound => 404;
    public static HttpStatusCode MethodNotAllowed => 405;
    public static HttpStatusCode NotAcceptable => 406;
    public static HttpStatusCode ProxyAuthenticationRequired => 407;
    public static HttpStatusCode RequestTimeout => 408;
    public static HttpStatusCode Conflict => 409;
    public static HttpStatusCode Gone => 410;
    public static HttpStatusCode LengthRequired => 411;
    public static HttpStatusCode PreconditionFailed => 412;
    public static HttpStatusCode RequestEntityTooLarge => 413;
    public static HttpStatusCode RequestUriTooLong => 414;
    public static HttpStatusCode UnsupportedMediaType => 415;
    public static HttpStatusCode RequestedRangeNotSatisfiable => 416;
    public static HttpStatusCode ExpectationFailed => 417;
    public static HttpStatusCode MisdirectedRequest => 421;
    public static HttpStatusCode UnProcessableEntity => 422;
    public static HttpStatusCode Locked => 423;
    public static HttpStatusCode FailedDependency => 424;
    public static HttpStatusCode TooEarly => 425;
    public static HttpStatusCode UpgradeRequired => 426;
    public static HttpStatusCode PreconditionRequired => 428;
    public static HttpStatusCode TooManyRequests => 429;
    public static HttpStatusCode RequestHeaderFieldsTooLarge => 431;
    public static HttpStatusCode UnavailableForLegalReasons => 451;
    public static HttpStatusCode InternalServerError => 500;
    public static HttpStatusCode NotImplemented => 501;
    public static HttpStatusCode BadGateway => 502;
    public static HttpStatusCode ServiceUnavailable => 503;
    public static HttpStatusCode GatewayTimeout => 504;
    public static HttpStatusCode HttpVersionNotSupported => 505;
    public static HttpStatusCode VariantAlsoNegotiates => 506;
    public static HttpStatusCode InsufficientStorage => 507;
    public static HttpStatusCode LoopDetected => 508;
    public static HttpStatusCode NotExtended => 510;
    public static HttpStatusCode NetworkAuthenticationRequired => 511;
    #endregion

    /// <summary>
    /// Checks whether the numeric <paramref name="statusCode"/> is valid.
    /// </summary>
    /// <remarks>
    /// Only an assigned code in the IANA registry is known, so an unregistered code inside the RFC 9110
    /// &#167; 15 range of <c>100</c>&#8211;<c>599</c>, such as <c>299</c> or the reserved <c>418</c>,
    /// returns <see langword="false"/>. The reserved <c>306</c> returns <see langword="true"/>, only
    /// because the obsolete <see cref="Unused"/> shipped in 10.0.0-preview.1 (see the type remarks).
    /// </remarks>
    /// <param name="statusCode">The numeric status code to validate.</param>
    /// <returns><see langword="true"/> when the supplied code is known; otherwise <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValid(int statusCode)
    {
        for (int i = 0; i < _statusCodes.Length; i++)
        {
            if (_statusCodes[i].Key == statusCode)
            {
                return true;
            }
        }
        return false;
    }
}
