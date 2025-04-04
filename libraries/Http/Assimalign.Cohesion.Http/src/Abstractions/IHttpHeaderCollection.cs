using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

public interface IHttpHeaderCollection : IEnumerable<KeyValuePair<HttpHeaderKey, HttpHeaderValue>>
{
    /// <summary>
    /// 
    /// </summary>
    int Count { get; }

    /// <summary>
    /// 
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    HttpHeaderValue this[HttpHeaderKey key] { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool ContainsKey(HttpHeaderKey key);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TryGetValue(HttpHeaderKey key, out HttpHeaderValue value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    void Add(HttpHeaderKey key, HttpHeaderValue value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    void Remove(HttpHeaderKey key);

    /// <summary>
    /// Gets or sets the Accepts header value.
    /// </summary>
    HttpHeaderValue? Accepts { get; set; }

    /// <summary>
    /// Gets or sets the ContentType header value.
    /// </summary>
    HttpHeaderValue? ContentType { get; set; }

    /// <summary>
    /// Gets or sets the ContentLength header value.
    /// </summary>
    HttpHeaderValue? ContentLength { get; set; }

    /// <summary>
    /// Gets or sets the TransferEncoding header value.
    /// </summary>
    HttpHeaderValue? TransferEncoding { get; set; }

    /// <summary>
    /// Gets or sets the Connection header value.
    /// </summary>
    HttpHeaderValue? Connection { get; set; }

    /// <summary>
    /// Gets or sets the AcceptCharset header value.
    /// </summary>
    HttpHeaderValue? AcceptCharset { get; set; }

    /// <summary>
    /// Gets or sets the AcceptEncoding header value.
    /// </summary>
    HttpHeaderValue? AcceptEncoding { get; set; }

    /// <summary>
    /// Gets or sets the AcceptLanguage header value.
    /// </summary>
    HttpHeaderValue? AcceptLanguage { get; set; }

    /// <summary>
    /// Gets or sets the AcceptRanges header value.
    /// </summary>
    HttpHeaderValue? AcceptRanges { get; set; }

    /// <summary>
    /// Gets or sets the AccessControlAllowCredentials header value.
    /// </summary>
    HttpHeaderValue? AccessControlAllowCredentials { get; set; }

    /// <summary>
    /// Gets or sets the AccessControlAllowHeaders header value.
    /// </summary>
    HttpHeaderValue? AccessControlAllowHeaders { get; set; }

    /// <summary>
    /// Gets or sets the AccessControlAllowMethods header value.
    /// </summary>
    HttpHeaderValue? AccessControlAllowMethods { get; set; }

    /// <summary>
    /// Gets or sets the AccessControlAllowOrigin header value.
    /// </summary>
    HttpHeaderValue? AccessControlAllowOrigin { get; set; }

    /// <summary>
    /// Gets or sets the AccessControlExposeHeaders header value.
    /// </summary>
    HttpHeaderValue? AccessControlExposeHeaders { get; set; }

    /// <summary>
    /// Gets or sets the AccessControlMaxAge header value.
    /// </summary>
    HttpHeaderValue? AccessControlMaxAge { get; set; }

    /// <summary>
    /// Gets or sets the AccessControlRequestHeaders header value.
    /// </summary>
    HttpHeaderValue? AccessControlRequestHeaders { get; set; }

    /// <summary>
    /// Gets or sets the AccessControlRequestMethod header value.
    /// </summary>
    HttpHeaderValue? AccessControlRequestMethod { get; set; }

    /// <summary>
    /// Gets or sets the Age header value.
    /// </summary>
    HttpHeaderValue? Age { get; set; }

    /// <summary>
    /// Gets or sets the Allow header value.
    /// </summary>
    HttpHeaderValue? Allow { get; set; }

    /// <summary>
    /// Gets or sets the AltSvc header value.
    /// </summary>
    HttpHeaderValue? AltSvc { get; set; }

    /// <summary>
    /// Gets or sets the Authorization header value.
    /// </summary>
    HttpHeaderValue? Authorization { get; set; }
    
    /// <summary>
    /// Gets or sets the Baggage header value.
    /// </summary>
    HttpHeaderValue? Baggage { get; set; }

    /// <summary>
    /// Gets or sets the CacheControl header value.
    /// </summary>
    HttpHeaderValue? CacheControl { get; set; }

    /// <summary>
    /// Gets or sets the ContentDisposition header value.
    /// </summary>
    HttpHeaderValue? ContentDisposition { get; set; }

    /// <summary>
    /// Gets or sets the ContentEncoding header value.
    /// </summary>
    HttpHeaderValue? ContentEncoding { get; set; }

    /// <summary>
    /// Gets or sets the ContentLanguage header value.
    /// </summary>
    HttpHeaderValue? ContentLanguage { get; set; }

    /// <summary>
    /// Gets or sets the ContentLocation header value.
    /// </summary>
    HttpHeaderValue? ContentLocation { get; set; }

    /// <summary>
    /// Gets or sets the ContentMD5 header value.
    /// </summary>
    HttpHeaderValue? ContentMD5 { get; set; }

    /// <summary>
    /// Gets or sets the ContentRange header value.
    /// </summary>
    HttpHeaderValue? ContentRange { get; set; }

    /// <summary>
    /// Gets or sets the ContentSecurityPolicy header value.
    /// </summary>
    HttpHeaderValue? ContentSecurityPolicy { get; set; }

    /// <summary>
    /// Gets or sets the ContentSecurityPolicyReportOnly header value.
    /// </summary>
    HttpHeaderValue? ContentSecurityPolicyReportOnly { get; set; }

    /// <summary>
    /// Gets or sets the CorrelationContext header value.
    /// </summary>
    HttpHeaderValue? CorrelationContext { get; set; }

    /// <summary>
    /// Gets or sets the Cookie header value.
    /// </summary>
    HttpHeaderValue? Cookie { get; set; }

    /// <summary>
    /// Gets or sets the Date header value.
    /// </summary>
    HttpHeaderValue? Date { get; set; }

    /// <summary>
    /// Gets or sets the ETag header value.
    /// </summary>
    HttpHeaderValue? ETag { get; set; }

    /// <summary>
    /// Gets or sets the Expires header value.
    /// </summary>
    HttpHeaderValue? Expires { get; set; }

    /// <summary>
    /// Gets or sets the Expect header value.
    /// </summary>
    HttpHeaderValue? Expect { get; set; }

    /// <summary>
    /// Gets or sets the From header value.
    /// </summary>
    HttpHeaderValue? From { get; set; }

    /// <summary>
    /// Gets or sets the GrpcAcceptEncoding header value.
    /// </summary>
    HttpHeaderValue? GrpcAcceptEncoding { get; set; }

    /// <summary>
    /// Gets or sets the GrpcEncoding header value.
    /// </summary>
    HttpHeaderValue? GrpcEncoding { get; set; }

    /// <summary>
    /// Gets or sets the GrpcMessage header value.
    /// </summary>
    HttpHeaderValue? GrpcMessage { get; set; }

    /// <summary>
    /// Gets or sets the GrpcStatus header value.
    /// </summary>
    HttpHeaderValue? GrpcStatus { get; set; }

    /// <summary>
    /// Gets or sets the GrpcTimeout header value.
    /// </summary>
    HttpHeaderValue? GrpcTimeout { get; set; }

    /// <summary>
    /// Gets or sets the Host header value.
    /// </summary>
    HttpHeaderValue? Host { get; set; }

    /// <summary>
    /// Gets or sets the KeepAlive header value.
    /// </summary>
    HttpHeaderValue? KeepAlive { get; set; }

    /// <summary>
    /// Gets or sets the IfMatch header value.
    /// </summary>
    HttpHeaderValue? IfMatch { get; set; }

    /// <summary>
    /// Gets or sets the IfModifiedSince header value.
    /// </summary>
    HttpHeaderValue? IfModifiedSince { get; set; }

    /// <summary>
    /// Gets or sets the IfNoneMatch header value.
    /// </summary>
    HttpHeaderValue? IfNoneMatch { get; set; }

    /// <summary>
    /// Gets or sets the IfRange header value.
    /// </summary>
    HttpHeaderValue? IfRange { get; set; }

    /// <summary>
    /// Gets or sets the IfUnmodifiedSince header value.
    /// </summary>
    HttpHeaderValue? IfUnmodifiedSince { get; set; }

    /// <summary>
    /// Gets or sets the LastModified header value.
    /// </summary>
    HttpHeaderValue? LastModified { get; set; }

    /// <summary>
    /// Gets or sets the Link header value.
    /// </summary>
    HttpHeaderValue? Link { get; set; }

    /// <summary>
    /// Gets or sets the Location header value.
    /// </summary>
    HttpHeaderValue? Location { get; set; }

    /// <summary>
    /// Gets or sets the MaxForwards header value.
    /// </summary>
    HttpHeaderValue? MaxForwards { get; set; }

    /// <summary>
    /// Gets or sets the Origin header value.
    /// </summary>
    HttpHeaderValue? Origin { get; set; }

    /// <summary>
    /// Gets or sets the Pragma header value.
    /// </summary>
    HttpHeaderValue? Pragma { get; set; }

    /// <summary>
    /// Gets or sets the ProxyAuthenticate header value.
    /// </summary>
    HttpHeaderValue? ProxyAuthenticate { get; set; }

    /// <summary>
    /// Gets or sets the ProxyAuthorization header value.
    /// </summary>
    HttpHeaderValue? ProxyAuthorization { get; set; }

    /// <summary>
    /// Gets or sets the ProxyConnection header value.
    /// </summary>
    HttpHeaderValue? ProxyConnection { get; set; }

    /// <summary>
    /// Gets or sets the Range header value.
    /// </summary>
    HttpHeaderValue? Range { get; set; }

    /// <summary>
    /// Gets or sets the Referer header value.
    /// </summary>
    HttpHeaderValue? Referer { get; set; }

    /// <summary>
    /// Gets or sets the RetryAfter header value.
    /// </summary>
    HttpHeaderValue? RetryAfter { get; set; }

    /// <summary>
    /// Gets or sets the RequestId header value.
    /// </summary>
    HttpHeaderValue? RequestId { get; set; }

    /// <summary>
    /// Gets or sets the SecWebSocketAccept header value.
    /// </summary>
    HttpHeaderValue? SecWebSocketAccept { get; set; }

    /// <summary>
    /// Gets or sets the SecWebSocketKey header value.
    /// </summary>
    HttpHeaderValue? SecWebSocketKey { get; set; }

    /// <summary>
    /// Gets or sets the SecWebSocketProtocol header value.
    /// </summary>
    HttpHeaderValue? SecWebSocketProtocol { get; set; }

    /// <summary>
    /// Gets or sets the SecWebSocketVersion header value.
    /// </summary>
    HttpHeaderValue? SecWebSocketVersion { get; set; }

    /// <summary>
    /// Gets or sets the SecWebSocketExtensions header value.
    /// </summary>
    HttpHeaderValue? SecWebSocketExtensions { get; set; }

    /// <summary>
    /// Gets or sets the Server header value.
    /// </summary>
    HttpHeaderValue? Server { get; set; }

    /// <summary>
    /// Gets or sets the SetCookie header value.
    /// </summary>
    HttpHeaderValue? SetCookie { get; set; }

    /// <summary>
    /// Gets or sets the StrictTransportSecurity header value.
    /// </summary>
    HttpHeaderValue? StrictTransportSecurity { get; set; }

    /// <summary>
    /// Gets or sets the TE header value.
    /// </summary>
    HttpHeaderValue? TE { get; set; }

    /// <summary>
    /// Gets or sets the Trailer header value.
    /// </summary>
    HttpHeaderValue? Trailer { get; set; }

    /// <summary>
    /// Gets or sets the Translate header value.
    /// </summary>
    HttpHeaderValue? Translate { get; set; }

    /// <summary>
    /// Gets or sets the TraceParent header value.
    /// </summary>
    HttpHeaderValue? TraceParent { get; set; }

    /// <summary>
    /// Gets or sets the TraceState header value.
    /// </summary>
    HttpHeaderValue? TraceState { get; set; }

    /// <summary>
    /// Gets or sets the Upgrade header value.
    /// </summary>
    HttpHeaderValue? Upgrade { get; set; }

    /// <summary>
    /// Gets or sets the UpgradeInsecureRequests header value.
    /// </summary>
    HttpHeaderValue? UpgradeInsecureRequests { get; set; }

    /// <summary>
    /// Gets or sets the UserAgent header value.
    /// </summary>
    HttpHeaderValue? UserAgent { get; set; }

    /// <summary>
    /// Gets or sets the Vary header value.
    /// </summary>
    HttpHeaderValue? Vary { get; set; }

    /// <summary>
    /// Gets or sets the Via header value.
    /// </summary>
    HttpHeaderValue? Via { get; set; }

    /// <summary>
    /// Gets or sets the Warning header value.
    /// </summary>
    HttpHeaderValue? Warning { get; set; }

    /// <summary>
    /// Gets or sets the WebSocketSubProtocols header value.
    /// </summary>
    HttpHeaderValue? WebSocketSubProtocols { get; set; }

    /// <summary>
    /// Gets or sets the WWWAuthenticate header value.
    /// </summary>
    HttpHeaderValue? WWWAuthenticate { get; set; }

    /// <summary>
    /// Gets or sets the XContentTypeOptions header value.
    /// </summary>
    HttpHeaderValue? XContentTypeOptions { get; set; }

    /// <summary>
    /// Gets or sets the XFrameOptions header value.
    /// </summary>
    HttpHeaderValue? XFrameOptions { get; set; }

    /// <summary>
    /// Gets or sets the XPoweredBy header value.
    /// </summary>
    HttpHeaderValue? XPoweredBy { get; set; }

    /// <summary>
    /// Gets or sets the XRequestedWith header value.
    /// </summary>
    HttpHeaderValue? XRequestedWith { get; set; }

    /// <summary>
    /// Gets or sets the XUACompatible header value.
    /// </summary>
    HttpHeaderValue? XUACompatible { get; set; }

    /// <summary>
    /// Gets or sets the XXSSProtection header value.
    /// </summary>
    HttpHeaderValue? XXSSProtection { get; set; }

}
