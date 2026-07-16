using System;

namespace Assimalign.Cohesion.Web.Diagnostics;

/// <summary>
/// Selects which parts of an HTTP exchange the logging middleware captures and emits.
/// </summary>
/// <remarks>
/// <para>
/// The flags are resolved at builder time through <see cref="HttpLoggingOptions.Fields"/> and may
/// be overridden per endpoint by attaching an <see cref="HttpLoggingMetadata"/> to the route's
/// metadata bag. <see cref="None"/> suppresses the log entry entirely — useful for chatty
/// endpoints such as health probes.
/// </para>
/// <para>
/// <see cref="RequestQuery"/>, <see cref="RequestBody"/>, and <see cref="ResponseBody"/> are
/// deliberately excluded from <see cref="Default"/>: query strings and bodies routinely carry
/// credentials, tokens, and personal data, so capturing them is an explicit opt-in.
/// </para>
/// </remarks>
[Flags]
public enum HttpLoggingFields
{
    /// <summary>
    /// Log nothing. When this is the effective field set for an exchange, no entry is emitted.
    /// </summary>
    None = 0,

    /// <summary>
    /// The request method (<c>http.request.method</c>).
    /// </summary>
    RequestMethod = 1 << 0,

    /// <summary>
    /// The request scheme (<c>http.request.scheme</c>).
    /// </summary>
    RequestScheme = 1 << 1,

    /// <summary>
    /// The request host (<c>http.request.host</c>).
    /// </summary>
    RequestHost = 1 << 2,

    /// <summary>
    /// The request path (<c>http.request.path</c>).
    /// </summary>
    RequestPath = 1 << 3,

    /// <summary>
    /// The request query string (<c>http.request.query</c>). Excluded from
    /// <see cref="Default"/> because query strings routinely carry sensitive values.
    /// </summary>
    RequestQuery = 1 << 4,

    /// <summary>
    /// The HTTP protocol version (<c>http.request.protocol</c>).
    /// </summary>
    RequestProtocol = 1 << 5,

    /// <summary>
    /// The request headers (<c>http.request.header.*</c>). Header names always log; a header's
    /// value logs only when its name is in <see cref="HttpLoggingOptions.AllowedRequestHeaders"/>,
    /// otherwise the value is replaced with <see cref="HttpLoggingOptions.RedactedValue"/>.
    /// </summary>
    RequestHeaders = 1 << 6,

    /// <summary>
    /// A bounded prefix of the request body (<c>http.request.body</c>), captured only when the
    /// request content type matches <see cref="HttpLoggingOptions.LoggableBodyContentTypes"/> and
    /// capped at <see cref="HttpLoggingOptions.RequestBodyLimit"/> bytes. Opt-in.
    /// </summary>
    RequestBody = 1 << 7,

    /// <summary>
    /// The response status code (<c>http.response.status</c>).
    /// </summary>
    ResponseStatusCode = 1 << 8,

    /// <summary>
    /// The response headers (<c>http.response.header.*</c>), value-redacted outside
    /// <see cref="HttpLoggingOptions.AllowedResponseHeaders"/> exactly like request headers.
    /// </summary>
    ResponseHeaders = 1 << 9,

    /// <summary>
    /// A bounded prefix of the response body (<c>http.response.body</c>), captured only when the
    /// response content type matches <see cref="HttpLoggingOptions.LoggableBodyContentTypes"/> and
    /// capped at <see cref="HttpLoggingOptions.ResponseBodyLimit"/> bytes. Opt-in.
    /// </summary>
    ResponseBody = 1 << 10,

    /// <summary>
    /// The wall-clock duration of the exchange in milliseconds (<c>http.duration</c>), measured
    /// from middleware entry to downstream completion.
    /// </summary>
    Duration = 1 << 11,

    /// <summary>
    /// The effective client address and port (<c>http.client.address</c> /
    /// <c>http.client.port</c>). Resolved through
    /// <see cref="HttpLoggingOptions.ClientAddressResolver"/> when set; otherwise the transport
    /// socket peer.
    /// </summary>
    ClientAddress = 1 << 12,

    /// <summary>
    /// Request and response body byte counts observed at the application layer
    /// (<c>http.request.body.bytes</c> / <c>http.response.body.bytes</c>) — the W3C
    /// <c>cs-bytes</c>/<c>sc-bytes</c> source.
    /// </summary>
    BytesTransferred = 1 << 13,

    /// <summary>
    /// W3C trace-context correlation (<c>trace.id</c> / <c>span.id</c>) parsed from the inbound
    /// <c>traceparent</c> header when present. No OpenTelemetry dependency is taken.
    /// </summary>
    TraceContext = 1 << 14,

    /// <summary>
    /// The request line: method, scheme, host, path, and protocol.
    /// <see cref="RequestQuery"/> is deliberately not included.
    /// </summary>
    RequestLine = RequestMethod | RequestScheme | RequestHost | RequestPath | RequestProtocol,

    /// <summary>
    /// The request line and request headers.
    /// </summary>
    Request = RequestLine | RequestHeaders,

    /// <summary>
    /// The response status code and response headers.
    /// </summary>
    Response = ResponseStatusCode | ResponseHeaders,

    /// <summary>
    /// The default field set: request line, request headers, response status and headers,
    /// duration, client address, byte counts, and trace context. Bodies and the query string are
    /// excluded and must be opted into.
    /// </summary>
    Default = Request | Response | Duration | ClientAddress | BytesTransferred | TraceContext,

    /// <summary>
    /// Every field, including the query string and bounded request/response bodies.
    /// </summary>
    All = Default | RequestQuery | RequestBody | ResponseBody,
}
