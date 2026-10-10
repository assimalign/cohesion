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
/// <para>
/// Scheme, host, and client attributes carry the <em>effective</em> values — what a trusted proxy
/// chain vouched for when the forwarded-headers middleware ran, otherwise the transport's (the
/// <c>Effective*</c> convention of <c>Assimalign.Cohesion.Http.Forwarded</c>). When the logged
/// client is not the transport peer, the peer is recorded under <see cref="PeerAddress"/> /
/// <see cref="PeerPort"/>.
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

    /// <summary>
    /// The effective request scheme, <c>http</c> or <c>https</c> — the scheme the client used on the
    /// outermost trusted hop when the forwarded-headers middleware resolved one, otherwise the
    /// transport's. String.
    /// </summary>
    public const string RequestScheme = "http.request.scheme";

    /// <summary>
    /// The effective request host (authority) — the host a trusted proxy forwarded when the
    /// forwarded-headers middleware resolved one, otherwise the transport-resolved host. String.
    /// </summary>
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

    /// <summary>
    /// The response status code. Int. For a client fault (<see cref="ClientFault"/>) it is the status
    /// the transport answers the exchange with in place of a response that has not started.
    /// </summary>
    public const string ResponseStatusCode = "http.response.status";

    /// <summary>
    /// Present, and <see langword="true"/>, when the server reported that the client's request was at
    /// fault: reading a request body that broke its framing or a configured limit failed, and the
    /// transport answered the exchange itself (<c>IWebClientFaultFeature</c>). Such an exchange is
    /// logged at the configured level rather than escalated to <c>Error</c>, without the exception.
    /// Absent otherwise. Bool.
    /// </summary>
    public const string ClientFault = "http.client_fault";

    /// <summary>Prefix for response header attributes; the suffix is the lower-cased header name. String values.</summary>
    public const string ResponseHeaderPrefix = "http.response.header.";

    /// <summary>The captured response body prefix, UTF-8 decoded. String.</summary>
    public const string ResponseBody = "http.response.body";

    /// <summary>Response body bytes observed at the application layer (W3C <c>sc-bytes</c> source). Long.</summary>
    public const string ResponseBodyBytes = "http.response.body.bytes";

    /// <summary>The exchange duration in milliseconds. Double.</summary>
    public const string Duration = "http.duration";

    /// <summary>
    /// The effective client IP address — the client a trusted proxy chain vouched for when the
    /// forwarded-headers middleware ran, otherwise the transport peer — or the
    /// <see cref="HttpLoggingOptions.ClientAddressResolver"/> result when one is configured. String.
    /// </summary>
    public const string ClientAddress = "http.client.address";

    /// <summary>
    /// The effective client port — the forwarded client node's port when a trusted hop resolved the
    /// client (omitted when that node carried none), otherwise the transport connection's. Int.
    /// </summary>
    public const string ClientPort = "http.client.port";

    /// <summary>
    /// The transport peer's IP address — the directly connected hop, normally the nearest proxy —
    /// emitted only when it differs from <see cref="ClientAddress"/>. Named after the OpenTelemetry
    /// <c>network.peer.address</c> attribute. String.
    /// </summary>
    public const string PeerAddress = "network.peer.address";

    /// <summary>
    /// The transport peer's port, emitted alongside <see cref="PeerAddress"/>. Named after the
    /// OpenTelemetry <c>network.peer.port</c> attribute. Int.
    /// </summary>
    public const string PeerPort = "network.peer.port";

    /// <summary>The W3C trace-context trace id parsed from the inbound <c>traceparent</c> header. String (32 hex digits).</summary>
    public const string TraceId = "trace.id";

    /// <summary>The W3C trace-context parent span id parsed from the inbound <c>traceparent</c> header. String (16 hex digits).</summary>
    public const string SpanId = "span.id";
}
