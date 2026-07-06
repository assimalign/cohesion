using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// static HttpHeaderKeyn HTTP {get;}h new(eader name).
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpHeaderKey : IEquatable<HttpHeaderKey>, IComparable<HttpHeaderKey>
{
    /// <summary>
    /// static HttpHeaderKeya new {get;}h new(eader key).
    /// </summary>
    /// <param name="value">The header name.</param>
    public HttpHeaderKey(string value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>
    /// static HttpHeaderKey header {get;}n new(ame).
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// static HttpHeaderKey indicating {get;}w new(hether the header key is empty).
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <inheritdoc />
    public bool Equals(HttpHeaderKey other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    /// <inheritdoc />
    public int CompareTo(HttpHeaderKey other) => StringComparer.OrdinalIgnoreCase.Compare(Value, other.Value);

    public override string ToString() => Value;
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public override bool Equals(object? instance) => instance is HttpHeaderKey key && Equals(key);

    public static implicit operator HttpHeaderKey(string key) => new(key);
    public static implicit operator string(HttpHeaderKey key) => key.Value;
    public static bool operator ==(HttpHeaderKey left, HttpHeaderKey right) => left.Equals(right);
    public static bool operator !=(HttpHeaderKey left, HttpHeaderKey right) => !left.Equals(right);


    /// <summary>Gets the <c>Accept</c> HTTP header name.</summary>
    public static HttpHeaderKey Accept {get;}= new( "Accept");

    /// <summary>Gets the <c>Accept-Charset</c> HTTP header name.</summary>
    public static HttpHeaderKey AcceptCharset {get;}= new( "Accept-Charset");

    /// <summary>Gets the <c>Accept-Encoding</c> HTTP header name.</summary>
    public static HttpHeaderKey AcceptEncoding {get;}= new( "Accept-Encoding");

    /// <summary>Gets the <c>Accept-Language</c> HTTP header name.</summary>
    public static HttpHeaderKey AcceptLanguage {get;}= new( "Accept-Language");

    /// <summary>Gets the <c>Accept-Ranges</c> HTTP header name.</summary>
    public static HttpHeaderKey AcceptRanges {get;}= new( "Accept-Ranges");

    /// <summary>Gets the <c>Access-Control-Allow-Credentials</c> HTTP header name.</summary>
    public static HttpHeaderKey AccessControlAllowCredentials {get;}= new( "Access-Control-Allow-Credentials");

    /// <summary>Gets the <c>Access-Control-Allow-Headers</c> HTTP header name.</summary>
    public static HttpHeaderKey AccessControlAllowHeaders {get;}= new( "Access-Control-Allow-Headers");

    /// <summary>Gets the <c>Access-Control-Allow-Methods</c> HTTP header name.</summary>
    public static HttpHeaderKey AccessControlAllowMethods {get;}= new( "Access-Control-Allow-Methods");

    /// <summary>Gets the <c>Access-Control-Allow-Origin</c> HTTP header name.</summary>
    public static HttpHeaderKey AccessControlAllowOrigin {get;}= new( "Access-Control-Allow-Origin");

    /// <summary>Gets the <c>Access-Control-Expose-Headers</c> HTTP header name.</summary>
    public static HttpHeaderKey AccessControlExposeHeaders {get;}= new( "Access-Control-Expose-Headers");

    /// <summary>Gets the <c>Access-Control-Max-Age</c> HTTP header name.</summary>
    public static HttpHeaderKey AccessControlMaxAge {get;}= new( "Access-Control-Max-Age");

    /// <summary>Gets the <c>Access-Control-Request-Headers</c> HTTP header name.</summary>
    public static HttpHeaderKey AccessControlRequestHeaders {get;}= new( "Access-Control-Request-Headers");

    /// <summary>Gets the <c>Access-Control-Request-Method</c> HTTP header name.</summary>
    public static HttpHeaderKey AccessControlRequestMethod {get;}= new( "Access-Control-Request-Method");

    /// <summary>Gets the <c>Age</c> HTTP header name.</summary>
    public static HttpHeaderKey Age {get;}= new( "Age");

    /// <summary>Gets the <c>Allow</c> HTTP header name.</summary>
    public static HttpHeaderKey Allow {get;}= new( "Allow");

    /// <summary>Gets the <c>Alt-Svc</c> HTTP header name.</summary>
    public static HttpHeaderKey AltSvc {get;}= new( "Alt-Svc");

    /// <summary>Gets the <c>:authority</c> HTTP header name.</summary>
    public static HttpHeaderKey Authority {get;}= new( ":authority");

    /// <summary>Gets the <c>Authorization</c> HTTP header name.</summary>
    public static HttpHeaderKey Authorization {get;}= new( "Authorization");

    /// <summary>Gets the <c>baggage</c> HTTP header name.</summary>
    public static HttpHeaderKey Baggage {get;}= new( "baggage");

    /// <summary>Gets the <c>Cache-Control</c> HTTP header name.</summary>
    public static HttpHeaderKey CacheControl {get;}= new( "Cache-Control");

    /// <summary>Gets the <c>Connection</c> HTTP header name.</summary>
    public static HttpHeaderKey Connection {get;}= new( "Connection");

    /// <summary>Gets the <c>Content-Disposition</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentDisposition {get;}= new( "Content-Disposition");

    /// <summary>Gets the <c>Content-Encoding</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentEncoding {get;}= new( "Content-Encoding");

    /// <summary>Gets the <c>Content-Language</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentLanguage {get;}= new( "Content-Language");

    /// <summary>Gets the <c>Content-Length</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentLength {get;}= new( "Content-Length");

    /// <summary>Gets the <c>Content-Location</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentLocation {get;}= new( "Content-Location");

    /// <summary>Gets the <c>Content-MD5</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentMD5 {get;}= new( "Content-MD5");

    /// <summary>Gets the <c>Content-Range</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentRange {get;}= new( "Content-Range");

    /// <summary>Gets the <c>Content-Security-Policy</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentSecurityPolicy {get;}= new( "Content-Security-Policy");

    /// <summary>Gets the <c>Content-Security-Policy-Report-Only</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentSecurityPolicyReportOnly {get;}= new( "Content-Security-Policy-Report-Only");

    /// <summary>Gets the <c>Content-Type</c> HTTP header name.</summary>
    public static HttpHeaderKey ContentType {get;}= new( "Content-Type");

    /// <summary>Gets the <c>Correlation-Context</c> HTTP header name.</summary>
    public static HttpHeaderKey CorrelationContext {get;}= new( "Correlation-Context");

    /// <summary>Gets the <c>Cookie</c> HTTP header name.</summary>
    public static HttpHeaderKey Cookie {get;}= new( "Cookie");

    /// <summary>Gets the <c>Date</c> HTTP header name.</summary>
    public static HttpHeaderKey Date {get;}= new( "Date");

    /// <summary>Gets the <c>DNT</c> HTTP header name.</summary>
    public static HttpHeaderKey DNT {get;}= new( "DNT");

    /// <summary>Gets the <c>ETag</c> HTTP header name.</summary>
    public static HttpHeaderKey ETag {get;}= new( "ETag");

    /// <summary>Gets the <c>Expires</c> HTTP header name.</summary>
    public static HttpHeaderKey Expires {get;}= new( "Expires");

    /// <summary>Gets the <c>Expect</c> HTTP header name.</summary>
    public static HttpHeaderKey Expect {get;}= new( "Expect");

    /// <summary>Gets the <c>From</c> HTTP header name.</summary>
    public static HttpHeaderKey From {get;}= new( "From");

    /// <summary>Gets the <c>Grpc-Accept-Encoding</c> HTTP header name.</summary>
    public static HttpHeaderKey GrpcAcceptEncoding {get;}= new( "Grpc-Accept-Encoding");

    /// <summary>Gets the <c>Grpc-Encoding</c> HTTP header name.</summary>
    public static HttpHeaderKey GrpcEncoding {get;}= new( "Grpc-Encoding");

    /// <summary>Gets the <c>Grpc-Message</c> HTTP header name.</summary>
    public static HttpHeaderKey GrpcMessage {get;}= new( "Grpc-Message");

    /// <summary>Gets the <c>Grpc-Status</c> HTTP header name.</summary>
    public static HttpHeaderKey GrpcStatus {get;}= new( "Grpc-Status");

    /// <summary>Gets the <c>Grpc-Timeout</c> HTTP header name.</summary>
    public static HttpHeaderKey GrpcTimeout {get;}= new( "Grpc-Timeout");

    /// <summary>Gets the <c>Host</c> HTTP header name.</summary>
    public static HttpHeaderKey Host {get;}= new( "Host");

    /// <summary>Gets the <c>Keep-Alive</c> HTTP header name.</summary>
    public static HttpHeaderKey KeepAlive {get;}= new( "Keep-Alive");

    /// <summary>Gets the <c>If-Match</c> HTTP header name.</summary>
    public static HttpHeaderKey IfMatch {get;}= new( "If-Match");

    /// <summary>Gets the <c>If-Modified-Since</c> HTTP header name.</summary>
    public static HttpHeaderKey IfModifiedSince {get;}= new( "If-Modified-Since");

    /// <summary>Gets the <c>If-None-Match</c> HTTP header name.</summary>
    public static HttpHeaderKey IfNoneMatch {get;}= new( "If-None-Match");

    /// <summary>Gets the <c>If-Range</c> HTTP header name.</summary>
    public static HttpHeaderKey IfRange {get;}= new( "If-Range");

    /// <summary>Gets the <c>If-Unmodified-Since</c> HTTP header name.</summary>
    public static HttpHeaderKey IfUnmodifiedSince {get;}= new( "If-Unmodified-Since");

    /// <summary>Gets the <c>Last-Event-ID</c> HTTP header name.</summary>
    /// <remarks>
    /// Defined by the WHATWG HTML Server-Sent Events specification. A browser
    /// reconnecting to an <c>text/event-stream</c> endpoint replays the last event
    /// id it saw in this request header so the server can resume the stream from
    /// that point.
    /// </remarks>
    public static HttpHeaderKey LastEventId {get;}= new( "Last-Event-ID");

    /// <summary>Gets the <c>Last-Modified</c> HTTP header name.</summary>
    public static HttpHeaderKey LastModified {get;}= new( "Last-Modified");

    /// <summary>Gets the <c>Link</c> HTTP header name.</summary>
    public static HttpHeaderKey Link {get;}= new( "Link");

    /// <summary>Gets the <c>Location</c> HTTP header name.</summary>
    public static HttpHeaderKey Location {get;}= new( "Location");

    /// <summary>Gets the <c>Max-Forwards</c> HTTP header name.</summary>
    public static HttpHeaderKey MaxForwards {get;}= new( "Max-Forwards");

    /// <summary>Gets the <c>:method</c> HTTP header name.</summary>
    public static HttpHeaderKey Method {get;}= new( ":method");

    /// <summary>Gets the <c>Origin</c> HTTP header name.</summary>
    public static HttpHeaderKey Origin {get;}= new( "Origin");

    /// <summary>Gets the <c>:path</c> HTTP header name.</summary>
    public static HttpHeaderKey Path {get;}= new( ":path");

    /// <summary>Gets the <c>Pragma</c> HTTP header name.</summary>
    public static HttpHeaderKey Pragma {get;}= new( "Pragma");

    /// <summary>Gets the <c>ProtocolType</c> HTTP header name.</summary>
    public static HttpHeaderKey Protocol {get;}= new( ":protocol");

    /// <summary>Gets the <c>Proxy-Authenticate</c> HTTP header name.</summary>
    public static HttpHeaderKey ProxyAuthenticate {get;}= new( "Proxy-Authenticate");

    /// <summary>Gets the <c>Proxy-Authorization</c> HTTP header name.</summary>
    public static HttpHeaderKey ProxyAuthorization {get;}= new( "Proxy-Authorization");

    /// <summary>Gets the <c>Proxy-Connection</c> HTTP header name.</summary>
    public static HttpHeaderKey ProxyConnection {get;}= new( "Proxy-Connection");

    /// <summary>Gets the <c>Range</c> HTTP header name.</summary>
    public static HttpHeaderKey Range {get;}= new( "Range");

    /// <summary>Gets the <c>Referer</c> HTTP header name.</summary>
    public static HttpHeaderKey Referer {get;}= new( "Referer");

    /// <summary>Gets the <c>Retry-After</c> HTTP header name.</summary>
    public static HttpHeaderKey RetryAfter {get;}= new( "Retry-After");

    /// <summary>Gets the <c>Request-Id</c> HTTP header name.</summary>
    public static HttpHeaderKey RequestId {get;}= new( "Request-Id");

    /// <summary>Gets the <c>:scheme</c> HTTP header name.</summary>
    public static HttpHeaderKey Scheme {get;}= new( ":scheme");

    /// <summary>Gets the <c>Sec-WebSocket-Accept</c> HTTP header name.</summary>
    public static HttpHeaderKey SecWebSocketAccept {get;}= new( "Sec-WebSocket-Accept");

    /// <summary>Gets the <c>Sec-WebSocket-Key</c> HTTP header name.</summary>
    public static HttpHeaderKey SecWebSocketKey {get;}= new( "Sec-WebSocket-Key");

    /// <summary>Gets the <c>Sec-WebSocket-Protocol</c> HTTP header name.</summary>
    /// <remarks>
    /// This is the single canonical key for the WebSocket sub-protocol negotiation header defined by
    /// RFC 6455 §11.3.4. The same header name carries both the client's list of requested sub-protocols
    /// and the server's single selected sub-protocol, so no separate key is required for either direction.
    /// A former redundant <c>WebSocketSubProtocols</c> alias — which incorrectly emitted a non-standard
    /// header name that appears in no specification — was removed in favor of this key.
    /// </remarks>
    public static HttpHeaderKey SecWebSocketProtocol {get;}= new( "Sec-WebSocket-Protocol");

    /// <summary>Gets the <c>Sec-WebSocket-Version</c> HTTP header name.</summary>
    public static HttpHeaderKey SecWebSocketVersion {get;}= new( "Sec-WebSocket-Version");

    /// <summary>Gets the <c>Sec-WebSocket-Extensions</c> HTTP header name.</summary>
    public static HttpHeaderKey SecWebSocketExtensions {get;}= new( "Sec-WebSocket-Extensions");

    /// <summary>Gets the <c>Server</c> HTTP header name.</summary>
    public static HttpHeaderKey Server {get;}= new( "Server");

    /// <summary>Gets the <c>Set-Cookie</c> HTTP header name.</summary>
    public static HttpHeaderKey SetCookie {get;}= new( "Set-Cookie");

    /// <summary>Gets the <c>:status</c> HTTP header name.</summary>
    public static HttpHeaderKey Status {get;}= new( ":status");

    /// <summary>Gets the <c>Strict-Transports-Security</c> HTTP header name.</summary>
    public static HttpHeaderKey StrictTransportSecurity {get;}= new( "Strict-Transports-Security");

    /// <summary>Gets the <c>TE</c> HTTP header name.</summary>
    public static HttpHeaderKey TE {get;}= new( "TE");

    /// <summary>Gets the <c>Trailer</c> HTTP header name.</summary>
    public static HttpHeaderKey Trailer {get;}= new( "Trailer");

    /// <summary>Gets the <c>Transfer-Encoding</c> HTTP header name.</summary>
    public static HttpHeaderKey TransferEncoding {get;}= new( "Transfer-Encoding");

    /// <summary>Gets the <c>Translate</c> HTTP header name.</summary>
    public static HttpHeaderKey Translate {get;}= new( "Translate");

    /// <summary>Gets the <c>traceparent</c> HTTP header name.</summary>
    public static HttpHeaderKey TraceParent {get;}= new( "traceparent");

    /// <summary>Gets the <c>tracestate</c> HTTP header name.</summary>
    public static HttpHeaderKey TraceState {get;}= new( "tracestate");

    /// <summary>Gets the <c>Upgrade</c> HTTP header name.</summary>
    public static HttpHeaderKey Upgrade {get;}= new( "Upgrade");

    /// <summary>Gets the <c>Upgrade-Insecure-Requests</c> HTTP header name.</summary>
    public static HttpHeaderKey UpgradeInsecureRequests {get;}= new( "Upgrade-Insecure-Requests");

    /// <summary>Gets the <c>User-Agent</c> HTTP header name.</summary>
    public static HttpHeaderKey UserAgent {get;}= new( "User-Agent");

    /// <summary>Gets the <c>Vary</c> HTTP header name.</summary>
    public static HttpHeaderKey Vary {get;}= new( "Vary");

    /// <summary>Gets the <c>Via</c> HTTP header name.</summary>
    public static HttpHeaderKey Via {get;}= new( "Via");

    /// <summary>Gets the <c>Warning</c> HTTP header name.</summary>
    public static HttpHeaderKey Warning {get;}= new( "Warning");

    /// <summary>Gets the <c>WWW-Authenticate</c> HTTP header name.</summary>
    public static HttpHeaderKey WWWAuthenticate {get;}= new( "WWW-Authenticate");

    /// <summary>Gets the <c>X-Content-Type-Options</c> HTTP header name.</summary>
    public static HttpHeaderKey XContentTypeOptions {get;}= new( "X-Content-Type-Options");

    /// <summary>Gets the <c>X-Frame-Options</c> HTTP header name.</summary>
    public static HttpHeaderKey XFrameOptions {get;}= new( "X-Frame-Options");

    /// <summary>Gets the <c>X-Powered-By</c> HTTP header name.</summary>
    public static HttpHeaderKey XPoweredBy {get;}= new( "X-Powered-By");

    /// <summary>Gets the <c>X-Requested-With</c> HTTP header name.</summary>
    public static HttpHeaderKey XRequestedWith {get;}= new( "X-Requested-With");

    /// <summary>Gets the <c>X-UA-Compatible</c> HTTP header name.</summary>
    public static HttpHeaderKey XUACompatible {get;}= new( "X-UA-Compatible");

    /// <summary>Gets the <c>X-XSS-Protection</c> HTTP header name.</summary>
    public static HttpHeaderKey XXSSProtection {get;}= new( "X-XSS-Protection");
}
