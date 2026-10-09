using System;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the TLS session of the connection an exchange arrived on.
/// </summary>
/// <remarks>
/// <para>
/// The server transport does not install <see cref="IHttpTlsConnectionFeature"/>. It publishes the
/// handshake facts as the <see cref="ITlsConnectionInfo"/> facet of the exchange's
/// <see cref="IHttpContext.ConnectionInfo"/>, and this accessor turns the facet into the feature the
/// first time it is read, the way <c>request.Cookies</c> builds its feature on first read. An exchange
/// that never reads the session pays nothing for it.
/// </para>
/// <para>
/// A wrapper context that replaces <see cref="IHttpContext.ConnectionInfo"/> with an object of its own
/// hides the facet unless that object implements <see cref="ITlsConnectionInfo"/> too; one that forwards
/// the inner context's connection info keeps it visible.
/// </para>
/// </remarks>
public static class HttpTlsConnectionExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the TLS connection feature for this exchange — the client certificate, TLS protocol,
        /// cipher suite, and negotiated application protocol — or <see langword="null"/> when the
        /// exchange did not arrive over TLS (or its transport does not report the session).
        /// </summary>
        /// <remarks>
        /// An <see cref="IHttpTlsConnectionFeature"/> already installed in
        /// <see cref="IHttpContext.Features"/> is returned as is, so a middleware that installs its own
        /// before the first read overrides the connection's session. Otherwise, when
        /// <see cref="IHttpContext.ConnectionInfo"/> implements <see cref="ITlsConnectionInfo"/>, the
        /// feature is built from it, installed in <see cref="IHttpContext.Features"/>, and returned; later
        /// reads return the same instance. An override installed after that first read wins only if it
        /// replaces the cached feature: registered under the same feature name, or installed after
        /// <c>Features.Set&lt;IHttpTlsConnectionFeature&gt;(null)</c> removed the cached one (see
        /// <see cref="IHttpTlsConnectionFeature"/>). The feature is not disposable, because the
        /// certificate it reports belongs to the connection.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IHttpTlsConnectionFeature? TlsConnection
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                IHttpTlsConnectionFeature? feature = context.Features.Get<IHttpTlsConnectionFeature>();

                if (feature is null && context.ConnectionInfo is ITlsConnectionInfo tls)
                {
                    feature = new HttpTlsConnectionFeature(tls);
                    context.Features.Set(feature);
                }

                return feature;
            }
        }
    }
}
