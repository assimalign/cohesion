using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Ergonomic access to the per-exchange <see cref="IHttpInterimResponseFeature"/>: resolving it from
/// the feature collection and emitting the two common interim responses (<c>100 Continue</c> and
/// <c>103 Early Hints</c>) without the caller assembling status codes or header collections by hand.
/// </summary>
public static class HttpInterimResponseExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the interim-response feature for this exchange, or <see langword="null"/> when the
        /// transport did not install one.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IHttpInterimResponseFeature? InterimResponse
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpInterimResponseFeature>();
            }
        }

        /// <summary>
        /// Emits a <c>103 Early Hints</c> interim response (RFC 8297) carrying the supplied
        /// <c>Link</c> field values, letting a client begin fetching linked resources before the
        /// final response is ready. No-op when the exchange cannot carry an interim response — see
        /// <see cref="IHttpInterimResponseFeature.IsInterimResponseSupported"/>.
        /// </summary>
        /// <param name="links">
        /// The <c>Link</c> field values to advertise (each an RFC 8288 link, for example
        /// <c>&lt;/style.css&gt;; rel=preload; as=style</c>). An empty sequence emits a bodyless
        /// <c>103</c> with no fields.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        /// <returns>
        /// <see langword="true"/> when the interim response was emitted; <see langword="false"/> when
        /// no feature is installed or the exchange can no longer carry an interim response.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="context"/> or <paramref name="links"/> is <see langword="null"/>.
        /// </exception>
        public async ValueTask<bool> SendEarlyHintsAsync(
            IEnumerable<string> links,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(links);

            IHttpInterimResponseFeature? feature = context.Features.Get<IHttpInterimResponseFeature>();
            if (feature is null || !feature.IsInterimResponseSupported)
            {
                return false;
            }

            HttpHeaderCollection headers = new();
            foreach (string link in links)
            {
                if (string.IsNullOrEmpty(link))
                {
                    continue;
                }

                headers[HttpHeaderKey.Link] = headers.TryGetValue(HttpHeaderKey.Link, out HttpHeaderValue existing)
                    ? HttpHeaderValue.Concat(existing, link)
                    : new HttpHeaderValue(link);
            }

            await feature.SendInterimResponseAsync(HttpStatusCode.EarlyHints, headers, cancellationToken).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Emits a <c>100 Continue</c> interim response (RFC 9110 §10.1.1) on demand — for a handler
        /// that solicits the request body itself. No-op when the exchange cannot carry an interim
        /// response — see <see cref="IHttpInterimResponseFeature.IsInterimResponseSupported"/>.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        /// <returns>
        /// <see langword="true"/> when the interim response was emitted; <see langword="false"/> when
        /// no feature is installed or the exchange can no longer carry an interim response.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// The HTTP/1.1 transport already emits <c>100 Continue</c> automatically for a request that
        /// carries <c>Expect: 100-continue</c> with a framed body, so this is only needed for a
        /// handler that manages the continue handshake explicitly.
        /// </remarks>
        public async ValueTask<bool> SendContinueAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            IHttpInterimResponseFeature? feature = context.Features.Get<IHttpInterimResponseFeature>();
            if (feature is null || !feature.IsInterimResponseSupported)
            {
                return false;
            }

            await feature.SendInterimResponseAsync(HttpStatusCode.Continue, headers: null, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }
}
