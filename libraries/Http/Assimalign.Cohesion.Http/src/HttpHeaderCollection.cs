using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http;

public sealed partial class HttpHeaderCollection : IHttpHeaderCollection
{
    private static readonly IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> EmptyIEnumeratorType = default(Enumerator);
    private static readonly IEnumerator EmptyIEnumerator = default(Enumerator);

    private Dictionary<HttpHeaderKey, HttpHeaderValue> _store;

    #region Constructors

    public HttpHeaderCollection()
    {
        _store = new Dictionary<HttpHeaderKey, HttpHeaderValue>();
    }
    public HttpHeaderCollection(int capacity)
    {
        _store = new Dictionary<HttpHeaderKey, HttpHeaderValue>(capacity);
    }
    public HttpHeaderCollection(Dictionary<HttpHeaderKey, HttpHeaderValue>? store)
    {
        _store = store ?? new Dictionary<HttpHeaderKey, HttpHeaderValue>();
    }

    #endregion

    #region Properties

    public HttpHeaderValue this[HttpHeaderKey key]
    {
        get
        {
            if (_store == null)
            {
                return HttpHeaderValue.Empty;
            }
            if (TryGetValue(key, out var value))
            {
                return value;
            }
            return HttpHeaderValue.Empty;
        }
        set
        {
            ThrowIfReadOnly();
            if (value.Count == 0)
            {
                _store?.Remove(key);
                return;
            }
            EnsureStore(1);
            _store![key] = value;
        }
    }
    public int Count => _store.Count;
    public bool IsReadOnly { get; }

    #endregion

    public void Add(HttpHeaderKey key, HttpHeaderValue value)
    {
        ThrowIfReadOnly();
        EnsureStore(1);
        _store!.Add(key, value);
    }
    public void Remove(HttpHeaderKey key)
    {
        ThrowIfReadOnly();
        //if (_store == null)
        //{
        //    return false;
        //}
        _store!.Remove(key);
    }
    public void Clear()
    {
        ThrowIfReadOnly();
        _store?.Clear();
    }
    public bool ContainsKey(HttpHeaderKey key)
    {
        if (_store == null)
        {
            return false;
        }
        return _store!.ContainsKey(key);
    }
    public bool TryGetValue(HttpHeaderKey key, [MaybeNullWhen(false)] out HttpHeaderValue value)
    {
        if (_store == null)
        {
            value = default(HttpHeaderValue);
            return false;
        }
        return _store!.TryGetValue(key, out value);
    }
    //public void Add(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    //{
    //    ThrowIfReadOnly();
    //    EnsureStore(1);
    //    _store!.Add(item.Key, item.Value);
    //}
    //public bool Contains(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    //{
    //    if (_store == null || !_store!.TryGetValue(item.Key, out var value) || !HttpHeaderValue.Equals(value, item.Value))
    //    {
    //        return false;
    //    }
    //    return true;
    //}
    //public void CopyTo(KeyValuePair<HttpHeaderKey, HttpHeaderValue>[] array, int arrayIndex)
    //{
    //    if (_store == null)
    //    {
    //        return;
    //    }
    //    foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> item in _store!)
    //    {
    //        var keyValuePair = (array[arrayIndex] = item);
    //        arrayIndex++;
    //    }
    //}  

    //public bool Remove(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    //{
    //    ThrowIfReadOnly();
    //    if (_store == null)
    //    {
    //        return false;
    //    }
    //    if (_store!.TryGetValue(item.Key, out var value) && HttpHeaderValue.Equals(item.Value, value))
    //    {
    //        return _store!.Remove(item.Key);
    //    }
    //    return false;
    //}



    public IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> GetEnumerator()
    {
        if (_store == null || _store!.Count == 0)
        {
            return default(Enumerator);
        }
        return new Enumerator(_store!.GetEnumerator());
    }
    IEnumerator IEnumerable.GetEnumerator()
    {
        if (_store == null || _store!.Count == 0)
        {
            return EmptyIEnumerator;
        }
        return _store!.GetEnumerator();
    }


    private void EnsureStore(int capacity)
    {
        if (_store == null)
        {
            _store = new Dictionary<HttpHeaderKey, HttpHeaderValue>(capacity);
        }
    }
    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The response headers cannot be modified because the response has already started.");
        }
    }

    #region Partials
    private struct Enumerator : IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>>, IEnumerator, IDisposable
    {
        private Dictionary<HttpHeaderKey, HttpHeaderValue>.Enumerator enumerator;
        private readonly bool isNotEmpty;

        public KeyValuePair<HttpHeaderKey, HttpHeaderValue> Current
        {
            get
            {
                if (isNotEmpty)
                {
                    return enumerator.Current;
                }
                return default(KeyValuePair<HttpHeaderKey, HttpHeaderValue>);
            }
        }

        object IEnumerator.Current => Current;
        internal Enumerator(Dictionary<HttpHeaderKey, HttpHeaderValue>.Enumerator dictionaryEnumerator)
        {
            enumerator = dictionaryEnumerator;
            isNotEmpty = true;
        }
        public bool MoveNext() => isNotEmpty ? enumerator.MoveNext() : false;
        public void Dispose() { }
        void IEnumerator.Reset()
        {
            if (isNotEmpty)
            {
                ((IEnumerator)enumerator).Reset();
            }
        }
    }

    #endregion

    private HttpHeaderValue? GetHeaderValue(ref HttpHeaderKey key)
    {
        return this[key];
    }
    private void SetHeaderValue(HttpHeaderKey key, HttpHeaderValue? value)
    {
        if (value.HasValue)
        {
            this[key] = value.Value;
        }
        Remove(key);
    }



    public HttpHeaderValue? Accepts 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Accepts); 
        set => SetHeaderValue(HttpHeaderKey.Accepts, value); 
    }
    public HttpHeaderValue? ContentType 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentType); 
        set => SetHeaderValue(HttpHeaderKey.ContentType, value); 
    }
    public HttpHeaderValue? ContentLength 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentLength); 
        set => SetHeaderValue(HttpHeaderKey.ContentLength, value); 
    }
    public HttpHeaderValue? TransferEncoding 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.TransferEncoding); 
        set => SetHeaderValue(HttpHeaderKey.TransferEncoding, value); 
    }
    public HttpHeaderValue? Connection 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Connection); 
        set => SetHeaderValue(HttpHeaderKey.Connection, value); 
    }
    public HttpHeaderValue? AcceptCharset 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AcceptCharset); 
        set => SetHeaderValue(HttpHeaderKey.AcceptCharset, value); 
    }
    public HttpHeaderValue? AcceptEncoding 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AcceptEncoding); 
        set => SetHeaderValue(HttpHeaderKey.AcceptEncoding, value); 
    }
    public HttpHeaderValue? AcceptLanguage 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AcceptLanguage); 
        set => SetHeaderValue(HttpHeaderKey.AcceptLanguage, value); 
    }
    public HttpHeaderValue? AcceptRanges 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AcceptRanges); 
        set => SetHeaderValue(HttpHeaderKey.AcceptRanges, value); 
    }
    public HttpHeaderValue? AccessControlAllowCredentials 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AccessControlAllowCredentials); 
        set => SetHeaderValue(HttpHeaderKey.AccessControlAllowCredentials, value); 
    }
    public HttpHeaderValue? AccessControlAllowHeaders 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AccessControlAllowHeaders); 
        set => SetHeaderValue(HttpHeaderKey.AccessControlAllowHeaders, value); 
    }
    public HttpHeaderValue? AccessControlAllowMethods 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AccessControlAllowMethods); 
        set => SetHeaderValue(HttpHeaderKey.AccessControlAllowMethods, value); 
    }
    public HttpHeaderValue? AccessControlAllowOrigin 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AccessControlAllowOrigin); 
        set => SetHeaderValue(HttpHeaderKey.AccessControlAllowOrigin, value); 
    }
    public HttpHeaderValue? AccessControlExposeHeaders 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AccessControlExposeHeaders); 
        set => SetHeaderValue(HttpHeaderKey.AccessControlExposeHeaders, value); 
    }
    public HttpHeaderValue? AccessControlMaxAge 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AccessControlMaxAge); 
        set => SetHeaderValue(HttpHeaderKey.AccessControlMaxAge, value); 
    }
    public HttpHeaderValue? AccessControlRequestHeaders 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AccessControlRequestHeaders); 
        set => SetHeaderValue(HttpHeaderKey.AccessControlRequestHeaders, value); 
    }
    public HttpHeaderValue? AccessControlRequestMethod 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AccessControlRequestMethod); 
        set => SetHeaderValue(HttpHeaderKey.AccessControlRequestMethod, value); 
    }
    public HttpHeaderValue? Age 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Age); 
        set => SetHeaderValue(HttpHeaderKey.Age, value);
    }
    public HttpHeaderValue? Allow 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Allow); 
        set => SetHeaderValue(HttpHeaderKey.Allow, value);
    }
    public HttpHeaderValue? AltSvc 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.AltSvc); 
        set => SetHeaderValue(HttpHeaderKey.AltSvc, value);
    }
    public HttpHeaderValue? Authorization 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Authorization); 
        set => SetHeaderValue(HttpHeaderKey.Authorization, value);
    }
    public HttpHeaderValue? Baggage 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Baggage); 
        set => SetHeaderValue(HttpHeaderKey.Baggage, value);
    }
    public HttpHeaderValue? CacheControl 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.CacheControl); 
        set => SetHeaderValue(HttpHeaderKey.CacheControl, value);
    }
    public HttpHeaderValue? ContentDisposition 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentDisposition); 
        set => SetHeaderValue(HttpHeaderKey.ContentDisposition, value);
    }
    public HttpHeaderValue? ContentEncoding 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentEncoding); 
        set => SetHeaderValue(HttpHeaderKey.ContentEncoding, value);
    }
    public HttpHeaderValue? ContentLanguage 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentLanguage); 
        set => SetHeaderValue(HttpHeaderKey.ContentLanguage, value);
    }
    public HttpHeaderValue? ContentLocation 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentLocation); 
        set => SetHeaderValue(HttpHeaderKey.ContentLocation, value);
    }
    public HttpHeaderValue? ContentMD5 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentMD5); 
        set => SetHeaderValue(HttpHeaderKey.ContentMD5, value);
    }
    public HttpHeaderValue? ContentRange 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentRange); 
        set => SetHeaderValue(HttpHeaderKey.ContentRange, value);
    }
    public HttpHeaderValue? ContentSecurityPolicy 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentSecurityPolicy); 
        set => SetHeaderValue(HttpHeaderKey.ContentSecurityPolicy, value);
    }
    public HttpHeaderValue? ContentSecurityPolicyReportOnly 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ContentSecurityPolicyReportOnly); 
        set => SetHeaderValue(HttpHeaderKey.ContentSecurityPolicyReportOnly, value);
    }
    public HttpHeaderValue? CorrelationContext 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.CorrelationContext); 
        set => SetHeaderValue(HttpHeaderKey.CorrelationContext, value);
    }
    public HttpHeaderValue? Cookie 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Cookie); 
        set => SetHeaderValue(HttpHeaderKey.Cookie, value);
    }
    public HttpHeaderValue? Date 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Date); 
        set => SetHeaderValue(HttpHeaderKey.Date, value);
    }
    public HttpHeaderValue? ETag 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ETag); 
        set => SetHeaderValue(HttpHeaderKey.ETag, value);
    }
    public HttpHeaderValue? Expires 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Expires); 
        set => SetHeaderValue(HttpHeaderKey.Expires, value);
    }
    public HttpHeaderValue? Expect 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Expect); 
        set => SetHeaderValue(HttpHeaderKey.Expect, value);
    }
    public HttpHeaderValue? From 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.From); 
        set => SetHeaderValue(HttpHeaderKey.From, value);
    }
    public HttpHeaderValue? GrpcAcceptEncoding 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.GrpcAcceptEncoding); 
        set => SetHeaderValue(HttpHeaderKey.GrpcAcceptEncoding, value);
    }
    public HttpHeaderValue? GrpcEncoding 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.GrpcEncoding); 
        set => SetHeaderValue(HttpHeaderKey.GrpcEncoding, value);
    }
    public HttpHeaderValue? GrpcMessage 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.GrpcMessage); 
        set => SetHeaderValue(HttpHeaderKey.GrpcMessage, value);
    }
    public HttpHeaderValue? GrpcStatus 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.GrpcStatus); 
        set => SetHeaderValue(HttpHeaderKey.GrpcStatus, value);
    }
    public HttpHeaderValue? GrpcTimeout 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.GrpcTimeout); 
        set => SetHeaderValue(HttpHeaderKey.GrpcTimeout, value);
    }
    public HttpHeaderValue? Host 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Host); 
        set => SetHeaderValue(HttpHeaderKey.Host, value);
    }
    public HttpHeaderValue? KeepAlive 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.KeepAlive); 
        set => SetHeaderValue(HttpHeaderKey.KeepAlive, value);
    }
    public HttpHeaderValue? IfMatch 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.IfMatch); 
        set => SetHeaderValue(HttpHeaderKey.IfMatch, value);
    }
    public HttpHeaderValue? IfModifiedSince 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.IfModifiedSince); 
        set => SetHeaderValue(HttpHeaderKey.IfModifiedSince, value);
    }
    public HttpHeaderValue? IfNoneMatch 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.IfNoneMatch); 
        set => SetHeaderValue(HttpHeaderKey.IfNoneMatch, value);
    }
    public HttpHeaderValue? IfRange 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.IfRange); 
        set => SetHeaderValue(HttpHeaderKey.IfRange, value); 
    }
    public HttpHeaderValue? IfUnmodifiedSince 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.IfUnmodifiedSince); 
        set => SetHeaderValue(HttpHeaderKey.IfUnmodifiedSince, value); 
    }
    public HttpHeaderValue? LastModified 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.LastModified); 
        set => SetHeaderValue(HttpHeaderKey.LastModified, value); 
    }
    public HttpHeaderValue? Link 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Link); 
        set => SetHeaderValue(HttpHeaderKey.Link, value); 
    }
    public HttpHeaderValue? Location 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Location); 
        set => SetHeaderValue(HttpHeaderKey.Location, value); 
    }
    public HttpHeaderValue? MaxForwards 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.MaxForwards); 
        set => SetHeaderValue(HttpHeaderKey.MaxForwards, value); 
    }
    public HttpHeaderValue? Origin 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Origin); 
        set => SetHeaderValue(HttpHeaderKey.Origin, value); 
    }
    public HttpHeaderValue? Pragma 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Pragma); 
        set => SetHeaderValue(HttpHeaderKey.Pragma, value); 
    }
    public HttpHeaderValue? ProxyAuthenticate 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ProxyAuthenticate); 
        set => SetHeaderValue(HttpHeaderKey.ProxyAuthenticate, value); 
    }
    public HttpHeaderValue? ProxyAuthorization 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ProxyAuthorization); 
        set => SetHeaderValue(HttpHeaderKey.ProxyAuthorization, value); 
    }
    public HttpHeaderValue? ProxyConnection 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.ProxyConnection); 
        set => SetHeaderValue(HttpHeaderKey.ProxyConnection, value); 
    }
    public HttpHeaderValue? Range 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Range); 
        set => SetHeaderValue(HttpHeaderKey.Range, value); 
    }
    public HttpHeaderValue? Referer 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Referer); 
        set => SetHeaderValue(HttpHeaderKey.Referer, value); 
    }
    public HttpHeaderValue? RetryAfter 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.RetryAfter); 
        set => SetHeaderValue(HttpHeaderKey.RetryAfter, value); 
    }
    public HttpHeaderValue? RequestId 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.RequestId); 
        set => SetHeaderValue(HttpHeaderKey.RequestId, value); 
    }
    public HttpHeaderValue? SecWebSocketAccept 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.SecWebSocketAccept); 
        set => SetHeaderValue(HttpHeaderKey.SecWebSocketAccept, value); 
    }
    public HttpHeaderValue? SecWebSocketKey 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.SecWebSocketKey); 
        set => SetHeaderValue(HttpHeaderKey.SecWebSocketKey, value); 
    }
    public HttpHeaderValue? SecWebSocketProtocol 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.SecWebSocketProtocol); 
        set => SetHeaderValue(HttpHeaderKey.SecWebSocketProtocol, value); 
    }
    public HttpHeaderValue? SecWebSocketVersion 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.SecWebSocketVersion); 
        set => SetHeaderValue(HttpHeaderKey.SecWebSocketVersion, value); 
    }
    public HttpHeaderValue? SecWebSocketExtensions 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.SecWebSocketExtensions); 
        set => SetHeaderValue(HttpHeaderKey.SecWebSocketExtensions, value); 
    }
    public HttpHeaderValue? Server 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Server); 
        set => SetHeaderValue(HttpHeaderKey.Server, value); 
    }
    public HttpHeaderValue? SetCookie 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.SetCookie); 
        set => SetHeaderValue(HttpHeaderKey.SetCookie, value); 
    }
    public HttpHeaderValue? StrictTransportSecurity 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.StrictTransportSecurity); 
        set => SetHeaderValue(HttpHeaderKey.StrictTransportSecurity, value); 
    }
    public HttpHeaderValue? TE 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.TE); 
        set => SetHeaderValue(HttpHeaderKey.TE, value); 
    }
    public HttpHeaderValue? Trailer 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Trailer); 
        set => SetHeaderValue(HttpHeaderKey.Trailer, value); 
    }
    public HttpHeaderValue? Translate 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Translate); 
        set => SetHeaderValue(HttpHeaderKey.Translate, value); 
    }
    public HttpHeaderValue? TraceParent 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.TraceParent); 
        set => SetHeaderValue(HttpHeaderKey.TraceParent, value); 
    }
    public HttpHeaderValue? TraceState 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.TraceState); 
        set => SetHeaderValue(HttpHeaderKey.TraceState, value); 
    }
    public HttpHeaderValue? Upgrade 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Upgrade); 
        set => SetHeaderValue(HttpHeaderKey.Upgrade, value); 
    }
    public HttpHeaderValue? UpgradeInsecureRequests 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.UpgradeInsecureRequests); 
        set => SetHeaderValue(HttpHeaderKey.UpgradeInsecureRequests, value); 
    }
    public HttpHeaderValue? UserAgent 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.UserAgent); 
        set => SetHeaderValue(HttpHeaderKey.UserAgent, value); 
    }
    public HttpHeaderValue? Vary 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Vary); 
        set => SetHeaderValue(HttpHeaderKey.Vary, value); 
    }
    public HttpHeaderValue? Via 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Via); 
        set => SetHeaderValue(HttpHeaderKey.Via, value); 
    }
    public HttpHeaderValue? Warning 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.Warning); 
        set => SetHeaderValue(HttpHeaderKey.Warning, value); 
    }
    public HttpHeaderValue? WebSocketSubProtocols 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.WebSocketSubProtocols); 
        set => SetHeaderValue(HttpHeaderKey.WebSocketSubProtocols, value); 
    }
    public HttpHeaderValue? WWWAuthenticate 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.WWWAuthenticate); 
        set => SetHeaderValue(HttpHeaderKey.WWWAuthenticate, value); 
    }
    public HttpHeaderValue? XContentTypeOptions 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.XContentTypeOptions); 
        set => SetHeaderValue(HttpHeaderKey.XContentTypeOptions, value); 
    }
    public HttpHeaderValue? XFrameOptions 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.XFrameOptions); 
        set => SetHeaderValue(HttpHeaderKey.XFrameOptions, value); 
    }
    public HttpHeaderValue? XPoweredBy 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.XPoweredBy); 
        set => SetHeaderValue(HttpHeaderKey.XPoweredBy, value); 
    }
    public HttpHeaderValue? XRequestedWith 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.XRequestedWith); 
        set => SetHeaderValue(HttpHeaderKey.XRequestedWith, value); 
    }
    public HttpHeaderValue? XUACompatible 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.XUACompatible); 
        set => SetHeaderValue(HttpHeaderKey.XUACompatible, value); 
    }
    public HttpHeaderValue? XXSSProtection 
    { 
        get => GetHeaderValue(ref HttpHeaderKey.XXSSProtection); 
        set => SetHeaderValue(HttpHeaderKey.XXSSProtection, value); 
    }

}