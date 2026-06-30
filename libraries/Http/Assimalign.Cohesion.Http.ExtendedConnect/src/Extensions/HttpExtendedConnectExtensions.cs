using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the extended CONNECT capability (RFC 8441 / RFC 9220) for the
/// current exchange.
/// </summary>
/// <remarks>
/// <para>
/// The HTTP/2 and HTTP/3 transports recognize the <c>:protocol</c> pseudo-header
/// and stash it verbatim under a well-known <see cref="IHttpContext.Items"/> key
/// when a valid extended CONNECT arrives. These members model that loosely-typed
/// value as a strongly-typed <see cref="IHttpExtendedConnectFeature"/> without
/// the transport taking a dependency on this package — the transport only ever
/// produces a string, and this package interprets it.
/// </para>
/// </remarks>
public static class HttpExtendedConnectExtensions
{
    // The HTTP/2 and HTTP/3 transports surface the :protocol pseudo-header under
    // this IHttpContext.Items key (the Assimalign.Cohesion.Http.Connections
    // internal TransportItemKeys.Protocol). The key is the pseudo-header name by
    // convention; both sides agree on the literal so neither needs a shared symbol.
    private const string ProtocolItemKey = ":protocol";

    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the extended CONNECT feature for this exchange, or
        /// <see langword="null"/> when the request is not an extended CONNECT.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IHttpExtendedConnectFeature? ExtendedConnect
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                if (context.Items.TryGetValue(ProtocolItemKey, out object? value)
                    && value is string protocol
                    && !string.IsNullOrEmpty(protocol))
                {
                    return new HttpExtendedConnectFeature(protocol);
                }

                return null;
            }
        }

        /// <summary>
        /// Gets whether the current exchange is an extended CONNECT request
        /// (a <c>CONNECT</c> carrying a <c>:protocol</c> pseudo-header).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public bool IsExtendedConnect
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                return context.Items.TryGetValue(ProtocolItemKey, out object? value)
                    && value is string protocol
                    && !string.IsNullOrEmpty(protocol);
            }
        }
    }
}
