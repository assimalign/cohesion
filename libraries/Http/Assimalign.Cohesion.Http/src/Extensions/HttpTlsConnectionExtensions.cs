using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the TLS session of the connection an exchange arrived on.
/// </summary>
public static class HttpTlsConnectionExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the TLS connection feature for this exchange — the client certificate, TLS protocol,
        /// cipher suite, and negotiated application protocol — or <see langword="null"/> when the
        /// exchange did not arrive over TLS (or its transport does not report the session).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IHttpTlsConnectionFeature? TlsConnection
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpTlsConnectionFeature>();
            }
        }
    }
}
