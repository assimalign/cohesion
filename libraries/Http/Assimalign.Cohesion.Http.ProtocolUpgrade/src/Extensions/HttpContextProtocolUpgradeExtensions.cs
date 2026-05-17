using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the protocol-upgrade capability for the current exchange on
/// <see cref="IHttpRequest"/>, backed by an
/// <see cref="IHttpProtocolUpgradeFeature"/> stored in the context's feature
/// collection.
/// </summary>
public static class HttpContextProtocolUpgradeExtensions
{
    extension(IHttpRequest request)
    {
        /// <summary>
        /// Gets the protocol-upgrade feature for this exchange, or
        /// <see langword="null"/> when the exchange is not a candidate for a
        /// connection transition.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A non-null value indicates that the request matches either the
        /// RFC 9110 §7.8 upgrade signal (<c>Connection: upgrade</c> +
        /// <c>Upgrade</c>) or the RFC 9110 §9.3.6 <c>CONNECT</c> tunnel shape.
        /// Inspect <see cref="IHttpProtocolUpgrade.Kind"/> to disambiguate.
        /// </para>
        /// <para>
        /// Most exchanges are normal request/response and this property
        /// returns <see langword="null"/>. The feature is installed by a
        /// transport bridge that detects the wire-level upgrade conditions
        /// and exposes the surrender-stream hook; that bridge is in transit
        /// while the transport drops its direct dependency on this package,
        /// so the property currently returns <see langword="null"/> for all
        /// transports until the bridge lands.
        /// </para>
        /// </remarks>
        public IHttpProtocolUpgrade? Upgrade
        {
            get
            {
                ArgumentNullException.ThrowIfNull(request);
                return request.HttpContext.Features.Get<IHttpProtocolUpgradeFeature>()?.Upgrade;
            }
        }
    }
}
