using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpRequestCookieFeature"/> implementation. Holds the
/// pre-parsed cookie collection produced by
/// <see cref="HttpCookieExtensions.Cookies"/> on first read. The
/// extension hands the feature an already-parsed snapshot, so this type does
/// not perform any wire-format work itself &#8211; that keeps the parsing
/// rules in a single place (the extension) and lets custom feature
/// implementations supply alternative cookie sources (signed cookies,
/// encrypted cookies, &#8230;) without re-implementing the wire grammar.
/// </summary>
internal sealed class HttpRequestCookieFeature : IHttpRequestCookieFeature
{
    public HttpRequestCookieFeature(IHttpCookieCollection cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        Cookies = cookies;
    }
    public string Name => nameof(HttpRequestCookieFeature);

    public IHttpCookieCollection Cookies { get; }

}
