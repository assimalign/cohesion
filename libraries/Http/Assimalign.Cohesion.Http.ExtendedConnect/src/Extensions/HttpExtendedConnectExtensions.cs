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
/// validated and on no other exchange, so these members are plain feature reads: the same feature
/// instance is returned on every read, and an ordinary exchange — including any HTTP/1.1 exchange —
/// reads <see langword="null"/>. So does every exchange on a listener that did not register the
/// interceptor.
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
        /// request is not an extended CONNECT.
        /// </summary>
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
        /// Gets whether the current exchange is an extended CONNECT request (a <c>CONNECT</c>
        /// carrying a <c>:protocol</c> pseudo-header).
        /// </summary>
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
