using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpResponseCookieFeature"/> implementation. The
/// underlying <see cref="Cookies"/> collection is the sync-aware
/// <see cref="HttpCookieCollection"/> bound to
/// <see cref="HttpHeaderKey.SetCookie"/>, so every mutation flows
/// straight into <c>response.Headers[Set-Cookie]</c>. The transport layer
/// drains cookies into the wire by iterating headers alone &#8211; no
/// dependency on the cookies package required.
/// </summary>
internal sealed class HttpResponseCookieFeature : IHttpResponseCookieFeature
{
    public HttpResponseCookieFeature(IHttpHeaderCollection headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Cookies = new HttpCookieCollection(headers, HttpHeaderKey.SetCookie);
    }

    public string Name => nameof(HttpResponseCookieFeature);

    public IHttpCookieCollection Cookies { get; }
}
