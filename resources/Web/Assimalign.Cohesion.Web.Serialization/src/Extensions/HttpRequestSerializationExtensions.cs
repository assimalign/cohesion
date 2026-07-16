using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Internal;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// Typed request-body reads over the content-serialization registry: the request's
/// <c>Content-Type</c> selects the registered <see cref="IHttpContentReader"/>, which
/// deserializes the body without per-call-site serializer ceremony.
/// </summary>
/// <remarks>
/// These are the throwing convenience path — an unresolvable read is surfaced as an
/// <see cref="HttpContentSerializationException"/> fault for the application's <c>OnError</c>
/// hook. Layers that turn unsupported media types into protocol outcomes (<c>415</c>) consult
/// <see cref="IHttpContentSerializationFeature.GetReader"/> first instead.
/// </remarks>
public static class HttpRequestSerializationExtensions
{
    extension(IHttpRequest request)
    {
        /// <summary>
        /// Deserializes the request body as <typeparamref name="T"/> using the reader registered
        /// for the request's <c>Content-Type</c>.
        /// </summary>
        /// <typeparam name="T">The target type; must be covered by the registered serialization contracts.</typeparam>
        /// <param name="cancellationToken">A token that cancels the read.</param>
        /// <returns>The deserialized value, or <see langword="null"/> for a null payload.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
        /// <exception cref="HttpContentSerializationException">
        /// No registry is composed, the request carries no parseable <c>Content-Type</c>, no
        /// reader is registered for it, or the reader has no contract for <typeparamref name="T"/>.
        /// </exception>
        public async ValueTask<T?> ReadContentAsync<T>(CancellationToken cancellationToken = default)
        {
            return (T?)await request.ReadContentAsync(typeof(T), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Deserializes the request body as <paramref name="type"/> using the reader registered
        /// for the request's <c>Content-Type</c>.
        /// </summary>
        /// <param name="type">The target type; must be covered by the registered serialization contracts.</param>
        /// <param name="cancellationToken">A token that cancels the read.</param>
        /// <returns>The deserialized value, or <see langword="null"/> for a null payload.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="type"/> is <see langword="null"/>.</exception>
        /// <exception cref="HttpContentSerializationException">
        /// No registry is composed, the request carries no parseable <c>Content-Type</c>, no
        /// reader is registered for it, or the reader has no contract for <paramref name="type"/>.
        /// </exception>
        public ValueTask<object?> ReadContentAsync(Type type, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(type);

            IHttpContentSerializationFeature feature = ContentSerializationFeatureResolver.GetRequired(request.HttpContext);

            if (!request.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue header)
                || !HttpMediaType.TryParse(header.Value, out HttpMediaType contentType))
            {
                throw new HttpContentSerializationException(
                    "The request carries no parseable Content-Type header, so no content reader can be selected. " +
                    "Typed reads require the client to declare the body's media type.");
            }

            IHttpContentReader reader = feature.GetReader(contentType)
                ?? throw new HttpContentSerializationException(
                    $"No content reader is registered for the request media type '{contentType}'.");

            return reader.ReadAsync(request, type, cancellationToken);
        }
    }
}
