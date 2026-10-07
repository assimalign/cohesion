using System;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the WebSocket capability of the current exchange on <see cref="IHttpContext"/>.
/// </summary>
public static class HttpContextWebSocketExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the WebSocket capability of this exchange. Never <see langword="null"/>: an
        /// ordinary request reads <see cref="IHttpWebSocketFeature.IsWebSocketRequest"/> as
        /// <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The feature installed in <see cref="IHttpContext.Features"/> wins, which is how a policy
        /// layer decorates it. Otherwise the first read of a WebSocket handshake attempt validates
        /// the handshake once and installs the result, so later reads, and the accept, share it. A
        /// request that does not ask for a WebSocket installs nothing: it reads one shared,
        /// immutable answer.
        /// </para>
        /// <para>
        /// On HTTP/1.1 the attempt is detected through <c>context.Upgrade</c>, which needs the
        /// protocol-upgrade interceptor (<c>HttpProtocolUpgrade.CreateInterceptor()</c>) on the
        /// listener. Without it, every request reads as an ordinary one.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IHttpWebSocketFeature WebSockets
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                return context.Features.Get<IHttpWebSocketFeature>() ?? HttpWebSocketFeature.Create(context);
            }
        }
    }
}
