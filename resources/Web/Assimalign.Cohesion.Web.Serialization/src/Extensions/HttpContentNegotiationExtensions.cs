using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Internal;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// Server-driven content negotiation over the content-serialization registry (#149): picks which
/// registered <see cref="IHttpContentWriter"/> serves a request from its <c>Accept</c> header, and
/// writes the negotiated representation. Negotiation composes <em>over</em> the registry — it never
/// redesigns the #864 matching rules; the q-value / precedence core is the shared
/// <see cref="HttpContentNegotiation"/> primitive (#771).
/// </summary>
/// <remarks>
/// <para>
/// The seam is a pure query (<see cref="TryNegotiate"/> / <see cref="TryNegotiateContentType"/>)
/// usable from any middleware or handler without a service container: it reads the registered
/// writers and the request's <c>Accept</c> header and returns the best concrete media type, or
/// reports that nothing is acceptable — the <c>406</c> signal. The write helper
/// (<see cref="WriteNegotiatedContentAsync{T}"/>) negotiates, stamps <c>Vary: Accept</c>, and
/// either writes through the registry or composes the bodyless <c>406</c> outcome.
/// </para>
/// <para>
/// Negotiation covers media types only; <c>Accept-Charset</c>, <c>Accept-Language</c>, and the
/// client-side <c>Content-Type</c> selection are out of scope (see the package DESIGN notes).
/// </para>
/// </remarks>
public static class HttpContentNegotiationExtensions
{
    extension(IHttpContentSerializationFeature feature)
    {
        /// <summary>
        /// Selects the best concrete media type for an <c>Accept</c> header from the registry's
        /// writers. Exact RFC 9110 §12.5.1 matching (q-value, then specificity, then registration
        /// order) is tried first; a bare base-type range (<c>application/json</c>) then falls back
        /// to a registered structured-suffix representation (<c>application/problem+json</c>).
        /// </summary>
        /// <param name="acceptHeader">The raw request <c>Accept</c> header value, or <see langword="null"/> (treated as "accept anything").</param>
        /// <param name="mediaType">On success, the negotiated concrete media type; otherwise the default value.</param>
        /// <returns>
        /// <see langword="true"/> when an acceptable representation exists; <see langword="false"/>
        /// when none does (a <c>406</c> signal), including when no writers are registered. This
        /// query never throws for an unacceptable request — it is the non-throwing outcome surface.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="feature"/> is <see langword="null"/>.</exception>
        public bool TryNegotiate(string? acceptHeader, out HttpMediaType mediaType)
        {
            ArgumentNullException.ThrowIfNull(feature);

            return ContentNegotiator.TryNegotiate(feature.Writers, acceptHeader, out mediaType);
        }
    }

    extension(IHttpContext context)
    {
        /// <summary>
        /// Negotiates the best concrete media type for the current exchange, reading the registry
        /// and the request's <c>Accept</c> header from the exchange. Equivalent to resolving the
        /// serialization feature and calling <see cref="TryNegotiate"/> with the request's
        /// <c>Accept</c> value.
        /// </summary>
        /// <param name="mediaType">On success, the negotiated concrete media type; otherwise the default value.</param>
        /// <returns><see langword="true"/> when an acceptable representation exists; otherwise <see langword="false"/> (a <c>406</c> signal).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="HttpContentSerializationException">No content-serialization registry is composed on the application.</exception>
        public bool TryNegotiateContentType(out HttpMediaType mediaType)
        {
            ArgumentNullException.ThrowIfNull(context);

            IHttpContentSerializationFeature feature = ContentSerializationFeatureResolver.GetRequired(context);
            string? acceptHeader = context.Request.Headers.GetValue(HttpHeaderKey.Accept);

            return ContentNegotiator.TryNegotiate(feature.Writers, acceptHeader, out mediaType);
        }

        /// <summary>
        /// Negotiates the response media type from the request's <c>Accept</c> header and writes
        /// <paramref name="value"/> as the winning representation, or composes a <c>406</c> outcome
        /// when nothing is acceptable. <c>Vary: Accept</c> is appended to the response either way,
        /// since the representation depends on the request's <c>Accept</c> header.
        /// </summary>
        /// <typeparam name="T">The declared type; must be covered by the registered serialization contracts.</typeparam>
        /// <param name="value">The value to serialize, which may be <see langword="null"/>.</param>
        /// <param name="cancellationToken">A token that cancels the write.</param>
        /// <returns>
        /// <see langword="true"/> when an acceptable representation was negotiated and written;
        /// <see langword="false"/> when no acceptable representation exists and the response was set
        /// to <c>406 Not Acceptable</c> with no body (an outcome the status-code-pages middleware
        /// can upgrade to a problem+json explanation).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="HttpContentSerializationException">
        /// No content-serialization registry is composed on the application, or the negotiated
        /// writer has no serialization contract for <typeparamref name="T"/>.
        /// </exception>
        /// <remarks>
        /// A missing registry is a composition fault (thrown); the absence of an acceptable
        /// representation — including an empty registry — is a protocol outcome (<c>406</c>), per
        /// the package's faults-vs-outcomes split.
        /// </remarks>
        public async Task<bool> WriteNegotiatedContentAsync<T>(T value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            IHttpContentSerializationFeature feature = ContentSerializationFeatureResolver.GetRequired(context);
            IHttpResponse response = context.Response;

            // The chosen representation depends on the request's Accept header, so downstream caches
            // must key on it. Append Accept to Vary without clobbering an existing token (e.g. the
            // Origin a CORS layer added).
            AppendVaryAccept(response);

            string? acceptHeader = context.Request.Headers.GetValue(HttpHeaderKey.Accept);
            if (!ContentNegotiator.TryNegotiate(feature.Writers, acceptHeader, out HttpMediaType mediaType))
            {
                // No acceptable representation is an OUTCOME, not a fault: a bodyless 406 that the
                // status-code-pages middleware (#881) upgrades into a problem+json explanation.
                response.StatusCode = HttpStatusCode.NotAcceptable;
                return false;
            }

            await response.WriteContentAsync(value, mediaType, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// Appends the <c>Accept</c> token to the response <c>Vary</c> header, preserving any tokens
    /// already present and never duplicating <c>Accept</c> (or overriding a <c>Vary: *</c>).
    /// </summary>
    private static void AppendVaryAccept(IHttpResponse response)
    {
        IHttpHeaderCollection headers = response.Headers;

        if (!headers.TryGetValue(HttpHeaderKey.Vary, out HttpHeaderValue existing) || existing.IsEmpty)
        {
            headers[HttpHeaderKey.Vary] = "Accept";
            return;
        }

        string current = existing.Value;
        foreach (string segment in current.Split(','))
        {
            string token = segment.Trim();
            if (token == "*" || string.Equals(token, "Accept", StringComparison.OrdinalIgnoreCase))
            {
                // Already varying by Accept (or by everything): leave the header as-is.
                return;
            }
        }

        headers[HttpHeaderKey.Vary] = $"{current}, Accept";
    }
}
