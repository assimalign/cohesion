using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Http.Internal;

[DebuggerDisplay("{ToString()}")]
public readonly struct HttpStatusCode : IEquatable<HttpStatusCode>
{
    private static ReadOnlySpan<KeyValuePair<int, string>> statusCodes => new KeyValuePair<int, string>[]
    {
        // 1xx Information
        new (100, "Continue"), 
        new (101, "Switching Protocols"), 
        new (102, "Processing"),
        new (103, "Early Hints"),
        // 2xx Success
        new (200, "Ok"), 
        new (201, "Created"), 
        new (202, "Accepted"), 
        new (203, "Non-Authoritative Information"), 
        new (204, "No Content"),
        new (205, "Reset Content"), 
        new (206, "Partial Content"), 
        new (207, "Multi-Status"), 
        new (208, "Already Reported"),
        // 3xx Redirection
        new (301, "Multiple Choices"),
        new (301, "Moved Permanently"),
        new (302, "Found"),
        new (303, "See Other"),
        new (304, "Not Modified"),
        new (305, "Use Proxy"),
        new (306, "(Unused)"),
        new (307, "Redirect Keep Verb"),
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
        new (413, "Request Entity Too Large"),
        new (414, "Request Uri Too Long"),
        new (415, "Unsupported Media Type"),
        new (416, "Requested Range Not Satisfiable"),
        new (417, "Expectation Failed"),
        new (421, "Misdirected Request"),
        new (422, "Un-Processable Entity"),
        new (423, "Locked"),
        new (424, "Failed Dependency"),
        new (425, "Too Early"),
        new (426, "Upgrade Required"),
        new (428, "Precondition Required"),
        new (429, "Too Many Requests"),
        new (431, "Request Header Fields Too Large"),
        new (451, "Unavailable For Legal Reasons"),
        // 5xx Server Error
        new (500, "Internal Server Error"),
        new (501, "Not Implemented"),
        new (502, "Bad Gateway"),
        new (503, "Service Unavailable"),
        new (504, "Gateway Timeout"),
        new (505, "Http Version Not Supported"),
        new (506, "Variant Also Negotiates"),
        new (507, "Insufficient Storage"),
        new (508, "Loop Detected"),
        new (510, "Not Extended"),
        new (511, "Network Authentication Required"),
    };
    
    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="statusCode"></param>
    public HttpStatusCode(int statusCode)
    {
        if (!IsValid(statusCode))
        {
            ThrowUtility.ThrowArgumentException($"The provided status code is invalid: '{statusCode}'");
        }
        Value = statusCode;
    }

    /// <summary>
    /// The raw HTTP Status Code.
    /// </summary>
    public int Value { get; }

    /// <inheritdoc />
    public bool Equals(HttpStatusCode other)
    {
        return other.Value == Value;
    }

    #region Overloads

    /// <inheritdoc />
    public override string ToString()
    {
        var value = string.Empty;

        for (int i = 0; i < statusCodes.Length; i++)
        {
            var statusCode = statusCodes[i];

            if (statusCode.Key == Value)
            {
                value = statusCode.Key + " " + statusCode.Value;
            }
        }

        return value;
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

    #region Operatos
    public static implicit operator HttpStatusCode(int statusCode) => new HttpStatusCode(statusCode);

    public static implicit operator int(HttpStatusCode statusCode) => statusCode.Value;
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
    public static HttpStatusCode MovedPermanently => 301;
    public static HttpStatusCode Found => 302;
    public static HttpStatusCode SeeOther => 303;
    public static HttpStatusCode NotModified => 304;
    public static HttpStatusCode UseProxy => 305;
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
    /// <param name="statusCode"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValid(int statusCode)
    {
        for (int i = 0; i < statusCodes.Length; i++)
        {
            if (statusCodes[i].Key == statusCode)
            {
                return true;
            }
        }
        return false;
    }

}