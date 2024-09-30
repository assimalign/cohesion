using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

public interface IHttpHeaderCollection : IDictionary<HttpHeaderKey, HttpHeaderValue>
{
    HttpHeaderValue? Accepts { get; }
    HttpHeaderValue? ContentType { get; }
    HttpHeaderValue? ContentLength { get; }
    HttpHeaderValue? TransferEncoding { get; }
    HttpHeaderValue? Connection { get; }
    HttpHeaderValue? AcceptCharset { get; }
    HttpHeaderValue? AcceptEncoding { get; }
    HttpHeaderValue? AcceptLanguage { get; }
    HttpHeaderValue? AcceptRanges { get; }
    HttpHeaderValue? AccessControlAllowCredentials { get; }
    HttpHeaderValue? AccessControlAllowHeaders { get; }
    HttpHeaderValue? AccessControlAllowMethods { get; }
    HttpHeaderValue? AccessControlAllowOrigin { get; }
    HttpHeaderValue? AccessControlExposeHeaders { get; }
    HttpHeaderValue? AccessControlMaxAge { get; }
    HttpHeaderValue? AccessControlRequestHeaders { get; }
    HttpHeaderValue? AccessControlRequestMethod { get; }
    HttpHeaderValue? Age { get; }
    HttpHeaderValue? Allow { get; }
    HttpHeaderValue? AltSvc { get; }
    HttpHeaderValue? Authorization { get; }
    HttpHeaderValue? Baggage { get; }
    HttpHeaderValue? CacheControl { get; }
    HttpHeaderValue? ContentDisposition { get; }
    HttpHeaderValue? ContentEncoding { get; }
    HttpHeaderValue? ContentLanguage { get; }
    HttpHeaderValue? ContentLocation { get; }
    HttpHeaderValue? ContentMD5 { get; }
    HttpHeaderValue? ContentRange { get; }
    HttpHeaderValue? ContentSecurityPolicy { get; }
    HttpHeaderValue? ContentSecurityPolicyReportOnly { get; }
    HttpHeaderValue? CorrelationContext { get; }
    HttpHeaderValue? Cookie { get; }
    HttpHeaderValue? Date { get; }
    HttpHeaderValue? ETag { get; }
    HttpHeaderValue? Expires { get; }
    HttpHeaderValue? Expect { get; }
    HttpHeaderValue? From { get; }
    HttpHeaderValue? GrpcAcceptEncoding { get; }
    HttpHeaderValue? GrpcEncoding { get; }
    HttpHeaderValue? GrpcMessage { get; }
    HttpHeaderValue? GrpcStatus { get; }
    HttpHeaderValue? GrpcTimeout { get; }
    HttpHeaderValue? Host { get; }
    HttpHeaderValue? KeepAlive { get; }
    HttpHeaderValue? IfMatch { get; }
    HttpHeaderValue? IfModifiedSince { get; }
    HttpHeaderValue? IfNoneMatch { get; }
    HttpHeaderValue? IfRange { get; }
    HttpHeaderValue? IfUnmodifiedSince { get; }
    HttpHeaderValue? LastModified { get; }
    HttpHeaderValue? Link { get; }
    HttpHeaderValue? Location { get; }
    HttpHeaderValue? MaxForwards { get; }
    HttpHeaderValue? Origin { get; }
    HttpHeaderValue? Pragma { get; }
    HttpHeaderValue? ProxyAuthenticate { get; }
    HttpHeaderValue? ProxyAuthorization { get; }
    HttpHeaderValue? ProxyConnection { get; }
    HttpHeaderValue? Range { get; }
    HttpHeaderValue? Referer { get; }
    HttpHeaderValue? RetryAfter { get; }
    HttpHeaderValue? RequestId { get; }
    HttpHeaderValue? SecWebSocketAccept { get; }
    HttpHeaderValue? SecWebSocketKey { get; }
    HttpHeaderValue? SecWebSocketProtocol { get; }
    HttpHeaderValue? SecWebSocketVersion { get; }
    HttpHeaderValue? SecWebSocketExtensions { get; }
    HttpHeaderValue? Server { get; }
    HttpHeaderValue? SetCookie { get; }
    HttpHeaderValue? StrictTransportSecurity { get; }
    HttpHeaderValue? TE { get; }
    HttpHeaderValue? Trailer { get; }
    HttpHeaderValue? Translate { get; }
    HttpHeaderValue? TraceParent { get; }
    HttpHeaderValue? TraceState { get; }
    HttpHeaderValue? Upgrade { get; }
    HttpHeaderValue? UpgradeInsecureRequests { get; }
    HttpHeaderValue? UserAgent { get; }
    HttpHeaderValue? Vary { get; }
    HttpHeaderValue? Via { get; }
    HttpHeaderValue? Warning { get; }
    HttpHeaderValue? WebSocketSubProtocols { get; }
    HttpHeaderValue? WWWAuthenticate { get; }
    HttpHeaderValue? XContentTypeOptions { get; }
    HttpHeaderValue? XFrameOptions { get; }
    HttpHeaderValue? XPoweredBy { get; }
    HttpHeaderValue? XRequestedWith { get; }
    HttpHeaderValue? XUACompatible { get; }
    HttpHeaderValue? XXSSProtection { get; }
}
