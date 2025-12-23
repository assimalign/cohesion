using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpHeaderKey : IEquatable<HttpHeaderKey>, IComparable<HttpHeaderKey>
{
    #region Constructor

    /// <summary>
    /// The default constructor.
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HttpHeaderKey(string value)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(value);

        Value = value;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The raw query key.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// 
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    #endregion

    #region Methods 

    /// <inheritdoc />
    public bool Equals(HttpHeaderKey other)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(this, other);
    }

    /// <inheritdoc />
    public int CompareTo(HttpHeaderKey other)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(this, other);
    }

    #endregion

    #region Overloads

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    }

    /// <inheritdoc />
    public override bool Equals(object? instance)
    {
        if (instance is HttpHeaderKey key)
        {
            return Equals(key);
        }

        return false;
    }
    #endregion

    #region Operators

    public static implicit operator HttpHeaderKey(string key)
    {
        return new HttpHeaderKey(key);
    }

    public static implicit operator string(HttpHeaderKey key)
    {
        return key.Value;
    }

    public static bool operator ==(HttpHeaderKey left, HttpHeaderKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(HttpHeaderKey left, HttpHeaderKey right)
    {
        return !left.Equals(right);
    }

    #endregion

    #region Helpers

    public static HttpHeaderKey Accepts = "Accepts";
    public static HttpHeaderKey ContentType = "ContentType";
    public static HttpHeaderKey ContentLength = "ContentLength";
    public static HttpHeaderKey TransferEncoding = "TransferEncoding";
    public static HttpHeaderKey Connection = "Connection";
    public static HttpHeaderKey AcceptCharset = "AcceptCharset";
    public static HttpHeaderKey AcceptEncoding = "AcceptEncoding";
    public static HttpHeaderKey AcceptLanguage = "AcceptLanguage";
    public static HttpHeaderKey AcceptRanges = "AcceptRanges";
    public static HttpHeaderKey AccessControlAllowCredentials = "AccessControlAllowCredentials";
    public static HttpHeaderKey AccessControlAllowHeaders = "AccessControlAllowHeaders";
    public static HttpHeaderKey AccessControlAllowMethods = "AccessControlAllowMethods";
    public static HttpHeaderKey AccessControlAllowOrigin = "AccessControlAllowOrigin";
    public static HttpHeaderKey AccessControlExposeHeaders = "AccessControlExposeHeaders";
    public static HttpHeaderKey AccessControlMaxAge = "AccessControlMaxAge";
    public static HttpHeaderKey AccessControlRequestHeaders = "AccessControlRequestHeaders";
    public static HttpHeaderKey AccessControlRequestMethod = "AccessControlRequestMethod";
    public static HttpHeaderKey Age = "Age";
    public static HttpHeaderKey Allow = "Allow";
    public static HttpHeaderKey AltSvc = "AltSvc";
    public static HttpHeaderKey Authorization = "Authorization";
    public static HttpHeaderKey Baggage = "Baggage";
    public static HttpHeaderKey CacheControl = "CacheControl";
    public static HttpHeaderKey ContentDisposition = "ContentDisposition";
    public static HttpHeaderKey ContentEncoding = "ContentEncoding";
    public static HttpHeaderKey ContentLanguage = "ContentLanguage";
    public static HttpHeaderKey ContentLocation = "ContentLocation";
    public static HttpHeaderKey ContentMD5 = "ContentMD5";
    public static HttpHeaderKey ContentRange = "ContentRange";
    public static HttpHeaderKey ContentSecurityPolicy = "ContentSecurityPolicy";
    public static HttpHeaderKey ContentSecurityPolicyReportOnly = "ContentSecurityPolicyReportOnly";
    public static HttpHeaderKey CorrelationContext = "CorrelationContext";
    public static HttpHeaderKey Cookie = "Cookie";
    public static HttpHeaderKey Date = "Date";
    public static HttpHeaderKey ETag = "ETag";
    public static HttpHeaderKey Expires = "Expires";
    public static HttpHeaderKey Expect = "Expect";
    public static HttpHeaderKey From = "From";
    public static HttpHeaderKey GrpcAcceptEncoding = "GrpcAcceptEncoding";
    public static HttpHeaderKey GrpcEncoding = "GrpcEncoding";
    public static HttpHeaderKey GrpcMessage = "GrpcMessage";
    public static HttpHeaderKey GrpcStatus = "GrpcStatus";
    public static HttpHeaderKey GrpcTimeout = "GrpcTimeout";
    public static HttpHeaderKey Host = "Host";
    public static HttpHeaderKey KeepAlive = "KeepAlive";
    public static HttpHeaderKey IfMatch = "IfMatch";
    public static HttpHeaderKey IfModifiedSince = "IfModifiedSince";
    public static HttpHeaderKey IfNoneMatch = "IfNoneMatch";
    public static HttpHeaderKey IfRange = "IfRange";
    public static HttpHeaderKey IfUnmodifiedSince = "IfUnmodifiedSince";
    public static HttpHeaderKey LastModified = "LastModified";
    public static HttpHeaderKey Link = "Link";
    public static HttpHeaderKey Location = "Location";
    public static HttpHeaderKey MaxForwards = "MaxForwards";
    public static HttpHeaderKey Origin = "Origin";
    public static HttpHeaderKey Pragma = "Pragma";
    public static HttpHeaderKey ProxyAuthenticate = "ProxyAuthenticate";
    public static HttpHeaderKey ProxyAuthorization = "ProxyAuthorization";
    public static HttpHeaderKey ProxyConnection = "ProxyConnection";
    public static HttpHeaderKey Range = "Range";
    public static HttpHeaderKey Referer = "Referer";
    public static HttpHeaderKey RetryAfter = "RetryAfter";
    public static HttpHeaderKey RequestId = "RequestId";
    public static HttpHeaderKey SecWebSocketAccept = "SecWebSocketAccept";
    public static HttpHeaderKey SecWebSocketKey = "SecWebSocketKey";
    public static HttpHeaderKey SecWebSocketProtocol = "SecWebSocketProtocol";
    public static HttpHeaderKey SecWebSocketVersion = "SecWebSocketVersion";
    public static HttpHeaderKey SecWebSocketExtensions = "SecWebSocketExtensions";
    public static HttpHeaderKey Server = "Server";
    public static HttpHeaderKey SetCookie = "SetCookie";
    public static HttpHeaderKey StrictTransportSecurity = "StrictTransportSecurity";
    public static HttpHeaderKey TE = "TE";
    public static HttpHeaderKey Trailer = "Trailer";
    public static HttpHeaderKey Translate = "Translate";
    public static HttpHeaderKey TraceParent = "TraceParent";
    public static HttpHeaderKey TraceState = "TraceState";
    public static HttpHeaderKey Upgrade = "Upgrade";
    public static HttpHeaderKey UpgradeInsecureRequests = "UpgradeInsecureRequests";
    public static HttpHeaderKey UserAgent = "UserAgent";
    public static HttpHeaderKey Vary = "Vary";
    public static HttpHeaderKey Via = "Via";
    public static HttpHeaderKey Warning = "Warning";
    public static HttpHeaderKey WebSocketSubProtocols = "WebSocketSubProtocols";
    public static HttpHeaderKey WWWAuthenticate = "WWWAuthenticate";
    public static HttpHeaderKey XContentTypeOptions = "XContentTypeOptions";
    public static HttpHeaderKey XFrameOptions = "XFrameOptions";
    public static HttpHeaderKey XPoweredBy = "XPoweredBy";
    public static HttpHeaderKey XRequestedWith = "XRequestedWith";
    public static HttpHeaderKey XUACompatible = "XUACompatible";
    public static HttpHeaderKey XXSSProtection = "XXSSProtection";

    #endregion
}