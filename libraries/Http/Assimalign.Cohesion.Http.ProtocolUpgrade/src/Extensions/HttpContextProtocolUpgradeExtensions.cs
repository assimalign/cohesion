using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the protocol-upgrade capability for the current exchange on
/// <see cref="IHttpContext"/>, backed by the <see cref="IHttpProtocolUpgradeFeature"/> the
/// package's interceptors install on the exchange's feature collection.
/// </summary>
public static class HttpContextProtocolUpgradeExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the protocol upgrade for this exchange, or <see langword="null"/> when the
        /// exchange is not a candidate for a connection transition.
        /// </summary>
        /// <value>
        /// A non-null value indicates the request matched either the RFC 9110 §7.8 upgrade
        /// signal (<c>Connection: upgrade</c> + <c>Upgrade</c>) or the RFC 9110 §9.3.6
        /// <c>CONNECT</c> tunnel shape; inspect <see cref="IHttpProtocolUpgrade.Kind"/> to
        /// disambiguate. Most exchanges are ordinary request/response and read
        /// <see langword="null"/>.
        /// </value>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// The feature is installed at parse time by the interceptor pair registered through
        /// <see cref="HttpProtocolUpgrade.CreateRequestInterceptor"/> /
        /// <see cref="HttpProtocolUpgrade.CreateResponseInterceptor"/>, so this accessor is a
        /// plain feature read: without that registration — or on transports that cannot
        /// surrender their connection (HTTP/2, HTTP/3) — it reads <see langword="null"/> rather
        /// than throwing.
        /// </remarks>
        public IHttpProtocolUpgrade? Upgrade
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                return context.Features.Get<IHttpProtocolUpgradeFeature>()?.Upgrade;
            }
        }
    }
}
