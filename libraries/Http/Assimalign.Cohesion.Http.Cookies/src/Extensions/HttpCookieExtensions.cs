using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the per-exchange cookie collections on <see cref="IHttpRequest"/>
/// and <see cref="IHttpResponse"/> as properties, backed by the matching
/// <see cref="IHttpRequestCookieFeature"/> / <see cref="IHttpResponseCookieFeature"/>
/// in the context's feature collection.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion HTTP protocol core (<c>Assimalign.Cohesion.Http</c>)
/// deliberately exposes only the raw <c>Cookie</c> and <c>Set-Cookie</c>
/// headers on <see cref="IHttpRequest.Headers"/> / <see cref="IHttpResponse.Headers"/>;
/// parsing into a typed cookie collection is an application convenience.
/// This package brings property-style access (<c>request.Cookies</c>,
/// <c>response.Cookies</c>) without forcing the protocol core to ship a
/// cookie model.
/// </para>
/// <para>
/// Both feature implementations construct an <see cref="HttpCookieCollection"/>
/// bound to the underlying header collection, so the wire-level header is
/// always the source of truth. Mutations on either side propagate to the
/// header automatically; the transport layer can drain response cookies by
/// reading <c>response.Headers[Set-Cookie]</c> without taking a dependency
/// on the cookies package.
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
        /// <see cref="IHttpRequestCookieFeature"/> on first read; subsequent
        /// reads return the same instance.
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
                    feature = new HttpRequestCookieFeature(request.Headers);
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
        /// <c>Set-Cookie</c> headers on the response. Every mutation
        /// synchronizes through to
        /// <c>response.Headers[Set-Cookie]</c>, so the transport layer
        /// can drain cookies by iterating headers alone. The collection
        /// is cached in <see cref="IHttpContext.Features"/> as an
        /// <see cref="IHttpResponseCookieFeature"/> on first read;
        /// subsequent reads return the same instance.
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
}
