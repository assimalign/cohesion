using System;
using System.Net;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The feature-first read convention for proxy-resolved connection identity: the
/// <c>Effective*</c> members answer "what client is this exchange really from / what
/// scheme and host did the client really use" by consulting the
/// <see cref="IHttpForwardedFeature"/> and falling back to the wire values when no
/// producer has attached one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IHttpRequest.Scheme"/>, <see cref="IHttpRequest.Host"/>, and
/// <see cref="IHttpContext.ConnectionInfo"/> are deliberately get-only wire facts;
/// forwarded-header resolution never mutates them. Any concern that keys on client
/// identity behind a proxy &#8212; CORS origins, cookie <c>Secure</c> decisions, session
/// partitioning, rate-limit keys, redirect generation, access logging &#8212; should read
/// these members instead of the wire surfaces, and must run <em>after</em> the
/// forwarded-headers middleware in the pipeline (the middleware documents a
/// first-position ordering contract).
/// </para>
/// <para>
/// Without an attached feature the members are exactly the wire values, so they are
/// always safe to read &#8212; in a proxy-less deployment (or before resolution has run)
/// "effective" and "wire" are the same thing.
/// </para>
/// </remarks>
public static class HttpContextForwardedExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the effective request scheme: <see cref="IHttpForwardedFeature.Scheme"/>
        /// when a forwarded feature is attached, otherwise <see cref="IHttpRequest.Scheme"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public HttpScheme EffectiveScheme
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpForwardedFeature>()?.Scheme ?? context.Request.Scheme;
            }
        }

        /// <summary>
        /// Gets the effective host: <see cref="IHttpForwardedFeature.Host"/> when a
        /// forwarded feature is attached, otherwise <see cref="IHttpRequest.Host"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public HttpHost EffectiveHost
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpForwardedFeature>()?.Host ?? context.Request.Host;
            }
        }

        /// <summary>
        /// Gets the effective remote endpoint: <see cref="IHttpForwardedFeature.RemoteEndPoint"/>
        /// when a forwarded feature is attached, otherwise
        /// <see cref="IHttpConnectionInfo.RemoteEndPoint"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public EndPoint? EffectiveRemoteEndPoint
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                IHttpForwardedFeature? feature = context.Features.Get<IHttpForwardedFeature>();
                return feature is null ? context.ConnectionInfo.RemoteEndPoint : feature.RemoteEndPoint;
            }
        }

        /// <summary>
        /// Gets the effective remote IP address: <see cref="IHttpForwardedFeature.RemoteIp"/>
        /// when a forwarded feature is attached, otherwise
        /// <see cref="IHttpConnectionInfo.RemoteIp"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IPAddress? EffectiveRemoteIp
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                IHttpForwardedFeature? feature = context.Features.Get<IHttpForwardedFeature>();
                return feature is null ? context.ConnectionInfo.RemoteIp : feature.RemoteIp;
            }
        }
    }
}
