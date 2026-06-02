using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the extended CONNECT capability (RFC 8441 / RFC 9220) for the
/// current exchange, backed by an <see cref="IHttpExtendedConnectFeature"/> the
/// transport installs when a request is a valid extended CONNECT.
/// </summary>
public static class HttpExtendedConnectExtensions
{
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
                return context.Features.Get<IHttpExtendedConnectFeature>();
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
                return context.Features.Get<IHttpExtendedConnectFeature>() is not null;
            }
        }
    }
}
