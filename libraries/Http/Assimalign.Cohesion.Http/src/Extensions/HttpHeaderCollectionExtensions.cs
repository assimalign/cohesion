using System;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Assimalign.Cohesion.Http;

public static class HttpHeaderCollectionExtensions
{
    private static HttpHeaderValue? GetHeaderValue(IHttpHeaderCollection headers, ref HttpHeaderKey key)
    {
        return headers[key];
    }
    private static void SetHeaderValue(IHttpHeaderCollection headers, HttpHeaderKey key, HttpHeaderValue? value)
    {
        if (value.HasValue)
        {
            // headers[key] = value.Value;
        }
        headers.Remove(key);
    }
    extension(IHttpHeaderCollection headers)
    {
        public HttpHeaderValue? Accepts
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Accepts);
            set => SetHeaderValue(headers, HttpHeaderKey.Accepts, value);
        }
        public HttpHeaderValue? ContentType
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentType);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentType, value);
        }
        public HttpHeaderValue? ContentLength
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentLength);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentLength, value);
        }
        public HttpHeaderValue? TransferEncoding
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.TransferEncoding);
            set => SetHeaderValue(headers, HttpHeaderKey.TransferEncoding, value);
        }
        public HttpHeaderValue? Connection
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Connection);
            set => SetHeaderValue(headers, HttpHeaderKey.Connection, value);
        }
        public HttpHeaderValue? AcceptCharset
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AcceptCharset);
            set => SetHeaderValue(headers, HttpHeaderKey.AcceptCharset, value);
        }
        public HttpHeaderValue? AcceptEncoding
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AcceptEncoding);
            set => SetHeaderValue(headers, HttpHeaderKey.AcceptEncoding, value);
        }
        public HttpHeaderValue? AcceptLanguage
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AcceptLanguage);
            set => SetHeaderValue(headers, HttpHeaderKey.AcceptLanguage, value);
        }
        public HttpHeaderValue? AcceptRanges
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AcceptRanges);
            set => SetHeaderValue(headers, HttpHeaderKey.AcceptRanges, value);
        }
        public HttpHeaderValue? AccessControlAllowCredentials
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AccessControlAllowCredentials);
            set => SetHeaderValue(headers, HttpHeaderKey.AccessControlAllowCredentials, value);
        }
        public HttpHeaderValue? AccessControlAllowHeaders
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AccessControlAllowHeaders);
            set => SetHeaderValue(headers, HttpHeaderKey.AccessControlAllowHeaders, value);
        }
        public HttpHeaderValue? AccessControlAllowMethods
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AccessControlAllowMethods);
            set => SetHeaderValue(headers, HttpHeaderKey.AccessControlAllowMethods, value);
        }
        public HttpHeaderValue? AccessControlAllowOrigin
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AccessControlAllowOrigin);
            set => SetHeaderValue(headers, HttpHeaderKey.AccessControlAllowOrigin, value);
        }
        public HttpHeaderValue? AccessControlExposeHeaders
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AccessControlExposeHeaders);
            set => SetHeaderValue(headers, HttpHeaderKey.AccessControlExposeHeaders, value);
        }
        public HttpHeaderValue? AccessControlMaxAge
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AccessControlMaxAge);
            set => SetHeaderValue(headers, HttpHeaderKey.AccessControlMaxAge, value);
        }
        public HttpHeaderValue? AccessControlRequestHeaders
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AccessControlRequestHeaders);
            set => SetHeaderValue(headers, HttpHeaderKey.AccessControlRequestHeaders, value);
        }
        public HttpHeaderValue? AccessControlRequestMethod
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AccessControlRequestMethod);
            set => SetHeaderValue(headers, HttpHeaderKey.AccessControlRequestMethod, value);
        }
        public HttpHeaderValue? Age
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Age);
            set => SetHeaderValue(headers, HttpHeaderKey.Age, value);
        }
        public HttpHeaderValue? Allow
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Allow);
            set => SetHeaderValue(headers, HttpHeaderKey.Allow, value);
        }
        public HttpHeaderValue? AltSvc
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.AltSvc);
            set => SetHeaderValue(headers, HttpHeaderKey.AltSvc, value);
        }
        public HttpHeaderValue? Authorization
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Authorization);
            set => SetHeaderValue(headers, HttpHeaderKey.Authorization, value);
        }
        public HttpHeaderValue? Baggage
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Baggage);
            set => SetHeaderValue(headers, HttpHeaderKey.Baggage, value);
        }
        public HttpHeaderValue? CacheControl
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.CacheControl);
            set => SetHeaderValue(headers, HttpHeaderKey.CacheControl, value);
        }
        public HttpHeaderValue? ContentDisposition
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentDisposition);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentDisposition, value);
        }
        public HttpHeaderValue? ContentEncoding
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentEncoding);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentEncoding, value);
        }
        public HttpHeaderValue? ContentLanguage
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentLanguage);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentLanguage, value);
        }
        public HttpHeaderValue? ContentLocation
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentLocation);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentLocation, value);
        }
        public HttpHeaderValue? ContentMD5
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentMD5);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentMD5, value);
        }
        public HttpHeaderValue? ContentRange
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentRange);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentRange, value);
        }
        public HttpHeaderValue? ContentSecurityPolicy
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentSecurityPolicy);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentSecurityPolicy, value);
        }
        public HttpHeaderValue? ContentSecurityPolicyReportOnly
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ContentSecurityPolicyReportOnly);
            set => SetHeaderValue(headers, HttpHeaderKey.ContentSecurityPolicyReportOnly, value);
        }
        public HttpHeaderValue? CorrelationContext
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.CorrelationContext);
            set => SetHeaderValue(headers, HttpHeaderKey.CorrelationContext, value);
        }
        public HttpHeaderValue? Cookie
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Cookie);
            set => SetHeaderValue(headers, HttpHeaderKey.Cookie, value);
        }
        public HttpHeaderValue? Date
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Date);
            set => SetHeaderValue(headers, HttpHeaderKey.Date, value);
        }
        public HttpHeaderValue? ETag
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ETag);
            set => SetHeaderValue(headers, HttpHeaderKey.ETag, value);
        }
        public HttpHeaderValue? Expires
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Expires);
            set => SetHeaderValue(headers, HttpHeaderKey.Expires, value);
        }
        public HttpHeaderValue? Expect
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Expect);
            set => SetHeaderValue(headers, HttpHeaderKey.Expect, value);
        }
        public HttpHeaderValue? From
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.From);
            set => SetHeaderValue(headers, HttpHeaderKey.From, value);
        }
        public HttpHeaderValue? GrpcAcceptEncoding
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.GrpcAcceptEncoding);
            set => SetHeaderValue(headers, HttpHeaderKey.GrpcAcceptEncoding, value);
        }
        public HttpHeaderValue? GrpcEncoding
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.GrpcEncoding);
            set => SetHeaderValue(headers, HttpHeaderKey.GrpcEncoding, value);
        }
        public HttpHeaderValue? GrpcMessage
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.GrpcMessage);
            set => SetHeaderValue(headers, HttpHeaderKey.GrpcMessage, value);
        }
        public HttpHeaderValue? GrpcStatus
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.GrpcStatus);
            set => SetHeaderValue(headers, HttpHeaderKey.GrpcStatus, value);
        }
        public HttpHeaderValue? GrpcTimeout
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.GrpcTimeout);
            set => SetHeaderValue(headers, HttpHeaderKey.GrpcTimeout, value);
        }
        public HttpHeaderValue? Host
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Host);
            set => SetHeaderValue(headers, HttpHeaderKey.Host, value);
        }
        public HttpHeaderValue? KeepAlive
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.KeepAlive);
            set => SetHeaderValue(headers, HttpHeaderKey.KeepAlive, value);
        }
        public HttpHeaderValue? IfMatch
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.IfMatch);
            set => SetHeaderValue(headers, HttpHeaderKey.IfMatch, value);
        }
        public HttpHeaderValue? IfModifiedSince
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.IfModifiedSince);
            set => SetHeaderValue(headers, HttpHeaderKey.IfModifiedSince, value);
        }
        public HttpHeaderValue? IfNoneMatch
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.IfNoneMatch);
            set => SetHeaderValue(headers, HttpHeaderKey.IfNoneMatch, value);
        }
        public HttpHeaderValue? IfRange
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.IfRange);
            set => SetHeaderValue(headers, HttpHeaderKey.IfRange, value);
        }
        public HttpHeaderValue? IfUnmodifiedSince
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.IfUnmodifiedSince);
            set => SetHeaderValue(headers, HttpHeaderKey.IfUnmodifiedSince, value);
        }
        public HttpHeaderValue? LastModified
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.LastModified);
            set => SetHeaderValue(headers, HttpHeaderKey.LastModified, value);
        }
        public HttpHeaderValue? Link
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Link);
            set => SetHeaderValue(headers, HttpHeaderKey.Link, value);
        }
        public HttpHeaderValue? Location
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Location);
            set => SetHeaderValue(headers, HttpHeaderKey.Location, value);
        }
        public HttpHeaderValue? MaxForwards
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.MaxForwards);
            set => SetHeaderValue(headers, HttpHeaderKey.MaxForwards, value);
        }
        public HttpHeaderValue? Origin
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Origin);
            set => SetHeaderValue(headers, HttpHeaderKey.Origin, value);
        }
        public HttpHeaderValue? Pragma
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Pragma);
            set => SetHeaderValue(headers, HttpHeaderKey.Pragma, value);
        }
        public HttpHeaderValue? ProxyAuthenticate
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ProxyAuthenticate);
            set => SetHeaderValue(headers, HttpHeaderKey.ProxyAuthenticate, value);
        }
        public HttpHeaderValue? ProxyAuthorization
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ProxyAuthorization);
            set => SetHeaderValue(headers, HttpHeaderKey.ProxyAuthorization, value);
        }
        public HttpHeaderValue? ProxyConnection
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.ProxyConnection);
            set => SetHeaderValue(headers, HttpHeaderKey.ProxyConnection, value);
        }
        public HttpHeaderValue? Range
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Range);
            set => SetHeaderValue(headers, HttpHeaderKey.Range, value);
        }
        public HttpHeaderValue? Referer
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Referer);
            set => SetHeaderValue(headers, HttpHeaderKey.Referer, value);
        }
        public HttpHeaderValue? RetryAfter
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.RetryAfter);
            set => SetHeaderValue(headers, HttpHeaderKey.RetryAfter, value);
        }
        public HttpHeaderValue? RequestId
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.RequestId);
            set => SetHeaderValue(headers, HttpHeaderKey.RequestId, value);
        }
        public HttpHeaderValue? SecWebSocketAccept
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.SecWebSocketAccept);
            set => SetHeaderValue(headers, HttpHeaderKey.SecWebSocketAccept, value);
        }
        public HttpHeaderValue? SecWebSocketKey
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.SecWebSocketKey);
            set => SetHeaderValue(headers, HttpHeaderKey.SecWebSocketKey, value);
        }
        public HttpHeaderValue? SecWebSocketProtocol
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.SecWebSocketProtocol);
            set => SetHeaderValue(headers, HttpHeaderKey.SecWebSocketProtocol, value);
        }
        public HttpHeaderValue? SecWebSocketVersion
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.SecWebSocketVersion);
            set => SetHeaderValue(headers, HttpHeaderKey.SecWebSocketVersion, value);
        }
        public HttpHeaderValue? SecWebSocketExtensions
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.SecWebSocketExtensions);
            set => SetHeaderValue(headers, HttpHeaderKey.SecWebSocketExtensions, value);
        }
        public HttpHeaderValue? Server
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Server);
            set => SetHeaderValue(headers, HttpHeaderKey.Server, value);
        }
        public HttpHeaderValue? SetCookie
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.SetCookie);
            set => SetHeaderValue(headers, HttpHeaderKey.SetCookie, value);
        }
        public HttpHeaderValue? StrictTransportSecurity
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.StrictTransportSecurity);
            set => SetHeaderValue(headers, HttpHeaderKey.StrictTransportSecurity, value);
        }
        public HttpHeaderValue? TE
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.TE);
            set => SetHeaderValue(headers, HttpHeaderKey.TE, value);
        }
        public HttpHeaderValue? Trailer
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Trailer);
            set => SetHeaderValue(headers, HttpHeaderKey.Trailer, value);
        }
        public HttpHeaderValue? Translate
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Translate);
            set => SetHeaderValue(headers, HttpHeaderKey.Translate, value);
        }
        public HttpHeaderValue? TraceParent
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.TraceParent);
            set => SetHeaderValue(headers, HttpHeaderKey.TraceParent, value);
        }
        public HttpHeaderValue? TraceState
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.TraceState);
            set => SetHeaderValue(headers, HttpHeaderKey.TraceState, value);
        }
        public HttpHeaderValue? Upgrade
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Upgrade);
            set => SetHeaderValue(headers, HttpHeaderKey.Upgrade, value);
        }
        public HttpHeaderValue? UpgradeInsecureRequests
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.UpgradeInsecureRequests);
            set => SetHeaderValue(headers, HttpHeaderKey.UpgradeInsecureRequests, value);
        }
        public HttpHeaderValue? UserAgent
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.UserAgent);
            set => SetHeaderValue(headers, HttpHeaderKey.UserAgent, value);
        }
        public HttpHeaderValue? Vary
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Vary);
            set => SetHeaderValue(headers, HttpHeaderKey.Vary, value);
        }
        public HttpHeaderValue? Via
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Via);
            set => SetHeaderValue(headers, HttpHeaderKey.Via, value);
        }
        public HttpHeaderValue? Warning
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.Warning);
            set => SetHeaderValue(headers, HttpHeaderKey.Warning, value);
        }
        public HttpHeaderValue? WebSocketSubProtocols
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.WebSocketSubProtocols);
            set => SetHeaderValue(headers, HttpHeaderKey.WebSocketSubProtocols, value);
        }
        public HttpHeaderValue? WWWAuthenticate
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.WWWAuthenticate);
            set => SetHeaderValue(headers, HttpHeaderKey.WWWAuthenticate, value);
        }
        public HttpHeaderValue? XContentTypeOptions
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.XContentTypeOptions);
            set => SetHeaderValue(headers, HttpHeaderKey.XContentTypeOptions, value);
        }
        public HttpHeaderValue? XFrameOptions
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.XFrameOptions);
            set => SetHeaderValue(headers, HttpHeaderKey.XFrameOptions, value);
        }
        public HttpHeaderValue? XPoweredBy
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.XPoweredBy);
            set => SetHeaderValue(headers, HttpHeaderKey.XPoweredBy, value);
        }
        public HttpHeaderValue? XRequestedWith
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.XRequestedWith);
            set => SetHeaderValue(headers, HttpHeaderKey.XRequestedWith, value);
        }
        public HttpHeaderValue? XUACompatible
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.XUACompatible);
            set => SetHeaderValue(headers, HttpHeaderKey.XUACompatible, value);
        }
        public HttpHeaderValue? XXSSProtection
        {
            get => GetHeaderValue(headers, ref HttpHeaderKey.XXSSProtection);
            set => SetHeaderValue(headers, HttpHeaderKey.XXSSProtection, value);
        }
    }
}
