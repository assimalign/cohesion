using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the per-exchange request cookies as a property on
/// <see cref="IHttpRequest"/>, backed by an
/// <see cref="IHttpRequestCookieFeature"/> stored in the context's feature
/// collection.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion HTTP protocol core (<c>Assimalign.Cohesion.Http</c>)
/// deliberately exposes only the raw <c>Cookie</c> header on
/// <see cref="IHttpRequest.Headers"/>; parsing into a typed cookie collection
/// is an application convenience. This package brings property-style access
/// (<c>request.Cookies</c>) without forcing the protocol core to ship a
/// cookie model.
/// </para>
/// <para>
/// Parsing is lazy: the first read of <c>request.Cookies</c> tokenizes the
/// <c>Cookie</c> header and installs the result in
/// <see cref="IHttpContext.Features"/> as an
/// <see cref="IHttpRequestCookieFeature"/>. Subsequent reads return the same
/// parsed collection without re-tokenizing.
/// </para>
/// </remarks>
public static class HttpCookieExtensions
{
    extension(IHttpRequest request)
    {
        /// <summary>
        /// Gets the cookies parsed from the request's <c>Cookie</c> header(s).
        /// Returns an empty collection when no <c>Cookie</c> header is
        /// present. The collection is cached in
        /// <see cref="IHttpContext.Features"/> as an
        /// <see cref="IHttpRequestCookieFeature"/> on first read.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
        public IHttpCookieCollection Cookies
        {
            get
            {
                ArgumentNullException.ThrowIfNull(request);

                IHttpRequestCookieFeature? feature = request.HttpContext.Features.Get<IHttpRequestCookieFeature>();
                
                if (feature is null)
                {
                    feature = new HttpRequestCookieFeature(ParseRequestCookies(request.Headers));
                    request.HttpContext.Features.Set(feature);
                }

                return feature.Cookies;
            }
        }
    }

    extension(IHttpResponse response)
    {
        /// <summary>
        /// Gets the mutable collection of cookies to be emitted as
        /// <c>Set-Cookie</c> headers on the response. The collection is
        /// created and cached in <see cref="IHttpContext.Features"/> as an
        /// <see cref="IHttpResponseCookieFeature"/> on first read; subsequent
        /// reads return the same instance.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
        public IHttpCookieCollection Cookies
        {
            get
            {
                ArgumentNullException.ThrowIfNull(response);

                IHttpResponseCookieFeature? feature = response.HttpContext.Features.Get<IHttpResponseCookieFeature>();

                if (feature is null)
                {
                    feature = new HttpResponseCookieFeature(response.Headers);
                    response.HttpContext.Features.Set(feature);
                }

                return feature.Cookies;
            }
        }
    }

    /// <summary>
    /// Tokenizes the request's <c>Cookie</c> header(s) into a typed cookie
    /// collection. Multiple <c>Cookie</c> header values are concatenated; each
    /// value is split on <c>;</c> into <c>name=value</c> segments per the
    /// RFC 6265 §4.2.1 cookie-string grammar.
    /// </summary>
    private static HttpCookieCollection ParseRequestCookies(IHttpHeaderCollection headers)
    {
        HttpCookieCollection cookies = new(headers);

        if (!headers.TryGetValue(HttpHeaderKey.Cookie, out HttpHeaderValue cookieHeader))
        {
            return cookies;
        }

        foreach (string? headerValue in cookieHeader)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                continue;
            }

            foreach (string segment in headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = segment.Split('=', 2);
                string name = parts[0].Trim();
                string value = parts.Length == 2 ? parts[1].Trim() : string.Empty;

                if (name.Length > 0)
                {
                    cookies.Add(new HttpCookie(name, value));
                }
            }
        }

        return cookies;
    }
}
