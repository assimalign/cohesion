namespace Assimalign.Cohesion.Web.Diagnostics;

/// <summary>
/// The stable attribute names the HTTP logging middleware stamps on the
/// <see cref="Assimalign.Cohesion.Logging.ILoggerEntry"/> instances it emits.
/// </summary>
/// <remarks>
/// <para>
/// These names are the contract between the middleware and every downstream consumer riding the
/// Cohesion logging pipeline — the <see cref="W3CAccessLogProvider"/> resolves its W3C fields
/// from them, and applications can key their own
/// <see cref="Assimalign.Cohesion.Logging.ILoggerFilter"/> or provider logic off them. The
/// naming aligns with the OpenTelemetry HTTP semantic conventions where one exists
/// (<c>http.request.method</c>, <c>http.response.status</c>, ...) without taking an
/// OpenTelemetry dependency.
/// </para>
/// <para>
/// Header attributes are emitted as <c>http.request.header.&lt;name&gt;</c> /
/// <c>http.response.header.&lt;name&gt;</c> with the header name lower-cased, so consumers can
/// look headers up without case juggling.
/// </para>
/// </remarks>
public static class HttpLoggingAttributes
{
    /// <summary>Marks what the entry describes: <see cref="EventExchange"/> or <see cref="EventStart"/>.</summary>
    public const string Event = "http.event";

    /// <summary>The <see cref="Event"/> value for a completed exchange — one entry per request/response pair.</summary>
    public const string EventExchange = "exchange";

    /// <summary>The <see cref="Event"/> value for the optional request-start entry (<see cref="HttpLoggingOptions.LogRequestStart"/>).</summary>
    public const string EventStart = "start";

    /// <summary>The request method, e.g. <c>GET</c>. String.</summary>
    public const string RequestMethod = "http.request.method";

    /// <summary>The request scheme, <c>http</c> or <c>https</c>. String.</summary>
    public const string RequestScheme = "http.request.scheme";

    /// <summary>The request host (authority). String.</summary>
    public const string RequestHost = "http.request.host";

    /// <summary>The request path. String.</summary>
    public const string RequestPath = "http.request.path";

    /// <summary>The request query string without the leading <c>?</c>, re-serialized from the parsed query collection. String.</summary>
    public const string RequestQuery = "http.request.query";

    /// <summary>The HTTP protocol version, e.g. <c>HTTP/1.1</c>. String.</summary>
    public const string RequestProtocol = "http.request.protocol";

    /// <summary>Prefix for request header attributes; the suffix is the lower-cased header name. String values.</summary>
    public const string RequestHeaderPrefix = "http.request.header.";

    /// <summary>The captured request body prefix, UTF-8 decoded. String.</summary>
    public const string RequestBody = "http.request.body";

    /// <summary>Request body bytes observed at the application layer (W3C <c>cs-bytes</c> source). Long.</summary>
    public const string RequestBodyBytes = "http.request.body.bytes";

    /// <summary>The response status code. Int.</summary>
    public const string ResponseStatusCode = "http.response.status";

    /// <summary>Prefix for response header attributes; the suffix is the lower-cased header name. String values.</summary>
    public const string ResponseHeaderPrefix = "http.response.header.";

    /// <summary>The captured response body prefix, UTF-8 decoded. String.</summary>
    public const string ResponseBody = "http.response.body";

    /// <summary>Response body bytes observed at the application layer (W3C <c>sc-bytes</c> source). Long.</summary>
    public const string ResponseBodyBytes = "http.response.body.bytes";

    /// <summary>The exchange duration in milliseconds. Double.</summary>
    public const string Duration = "http.duration";

    /// <summary>The effective client IP address. String.</summary>
    public const string ClientAddress = "http.client.address";

    /// <summary>The client port of the transport connection. Int.</summary>
    public const string ClientPort = "http.client.port";

    /// <summary>The W3C trace-context trace id parsed from the inbound <c>traceparent</c> header. String (32 hex digits).</summary>
    public const string TraceId = "trace.id";

    /// <summary>The W3C trace-context parent span id parsed from the inbound <c>traceparent</c> header. String (16 hex digits).</summary>
    public const string SpanId = "span.id";
}
