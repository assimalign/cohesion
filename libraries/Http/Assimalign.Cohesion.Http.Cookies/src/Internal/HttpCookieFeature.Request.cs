using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpRequestCookieFeature"/> implementation. The
/// underlying <see cref="Cookies"/> collection is the sync-aware
/// <see cref="HttpCookieCollection"/> bound to
/// <see cref="HttpHeaderKey.Cookie"/>; it parses the incoming
/// <c>Cookie</c> header on construction and writes mutations back to
/// the same header so any middleware that inspects
/// <c>request.Headers[Cookie]</c> sees a consistent view.
/// </summary>
internal sealed class HttpRequestCookieFeature : IHttpRequestCookieFeature
{
    public HttpRequestCookieFeature(IHttpHeaderCollection headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Cookies = new HttpCookieCollection(headers, HttpHeaderKey.Cookie);
    }

    public string Name => nameof(HttpRequestCookieFeature);

    public IHttpCookieCollection Cookies { get; }
}
