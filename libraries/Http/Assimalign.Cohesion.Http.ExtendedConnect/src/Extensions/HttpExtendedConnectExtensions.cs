using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the extended CONNECT capability (RFC 8441 / RFC 9220) of the current exchange on
/// <see cref="IHttpContext"/>, backed by the <see cref="IHttpExtendedConnectFeature"/> the
/// interceptor from <see cref="HttpExtendedConnect.CreateInterceptor"/> installs on the exchange's
/// feature collection.
/// </summary>
/// <remarks>
/// <para>
/// The interceptor installs the feature on every extended CONNECT the HTTP/2 and HTTP/3 transports
/// validated and on no other exchange, and its <see cref="IHttpExchangeInterceptor.BeforeResponse"/>
/// hook removes it again when the transport's exchange control cannot accept the tunnel. These
/// members are plain feature reads: the same feature instance is returned on every read, and an
/// ordinary exchange — including any HTTP/1.1 exchange — reads <see langword="null"/>. So does every
/// exchange on a listener that did not register the interceptor.
/// </para>
/// <code>
/// if (context.ExtendedConnect is { Protocol: "websocket" } extendedConnect)
/// {
///     await using Stream tunnel = await extendedConnect.AcceptAsync(context.RequestCancelled);
///     // ...run the inner protocol over the tunnel...
/// }
/// </code>
/// </remarks>
public static class HttpExtendedConnectExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the extended CONNECT feature for this exchange, or <see langword="null"/> when the
        /// exchange carries none.
        /// </summary>
        /// <remarks>
        /// The feature is present for an HTTP/2 or HTTP/3 extended CONNECT on a listener that
        /// registered <see cref="HttpExtendedConnect.CreateInterceptor"/>, provided the transport's
        /// exchange control could still accept the tunnel when the exchange's response phase began;
        /// the interceptor's <see cref="IHttpExchangeInterceptor.BeforeResponse"/> hook removes it
        /// otherwise. Any other exchange reads <see langword="null"/>, and so does an extended CONNECT
        /// on a listener without the interceptor.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IHttpExtendedConnectFeature? ExtendedConnect
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                return context.Features.Get<IHttpExtendedConnectFeature>();
            }
        }

        /// <summary>
        /// Gets whether the current exchange carries the extended CONNECT feature: an HTTP/2 or
        /// HTTP/3 extended CONNECT (a <c>CONNECT</c> carrying a <c>:protocol</c> pseudo-header) on a
        /// listener that registered <see cref="HttpExtendedConnect.CreateInterceptor"/>, whose tunnel
        /// the transport's exchange control could still accept when the exchange's response phase
        /// began.
        /// </summary>
        /// <remarks>
        /// <see langword="false"/> for an extended CONNECT on a listener without the interceptor, and
        /// for one whose feature the interceptor's <see cref="IHttpExchangeInterceptor.BeforeResponse"/>
        /// hook removed because the tunnel could not be accepted.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public bool IsExtendedConnect
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                return context.Features.Get<IHttpExtendedConnectFeature>() is not null;
            }
        }
    }
}
