using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpResponseCookieFeature"/> implementation. The
/// underlying <see cref="Cookies"/> collection is a sync-aware wrapper that
/// mirrors every mutation into <c>response.Headers[Set-Cookie]</c>, so the
/// transport layer can drain cookies into the wire response by iterating
/// the header collection alone. The transport does not need to take a
/// dependency on the cookies package.
/// </summary>
internal sealed class HttpResponseCookieFeature : IHttpResponseCookieFeature
{
    public HttpResponseCookieFeature(IHttpHeaderCollection headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Cookies = new HttpResponseCookies(headers);
    }

    public string Name => nameof(HttpResponseCookieFeature);

    public IHttpCookieCollection Cookies { get; }
}
