using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Internal;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// Typed response-body writes over the content-serialization registry: a registered
/// <see cref="IHttpContentWriter"/> serializes the value and stamps the <c>Content-Type</c>
/// header, without per-call-site serializer ceremony. The status code is never touched — that
/// stays the handler's decision, per the area's middleware-first model.
/// </summary>
/// <remarks>
/// These are the throwing convenience path — an unresolvable write is surfaced as an
/// <see cref="HttpContentSerializationException"/> fault for the application's <c>OnError</c>
/// hook. Content negotiation (selecting the media type from the request's <c>Accept</c> header)
/// composes above this surface and passes its choice through the explicit
/// <see cref="HttpMediaType"/> overload.
/// </remarks>
public static class HttpResponseSerializationExtensions
{
    extension(IHttpResponse response)
    {
        /// <summary>
        /// Serializes <paramref name="value"/> to the response body. The target format is the
        /// response's already-set <c>Content-Type</c> header when present; otherwise the first
        /// registered writer and its canonical media type.
        /// </summary>
        /// <typeparam name="T">The declared type; must be covered by the registered serialization contracts.</typeparam>
        /// <param name="value">The value to serialize, which may be <see langword="null"/>.</param>
        /// <param name="cancellationToken">A token that cancels the write.</param>
        /// <returns>A task that completes when the payload has been written to the response body.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
        /// <exception cref="HttpContentSerializationException">
        /// No registry is composed, no writer matches the response's declared <c>Content-Type</c>,
        /// no writer is registered at all, or the writer has no contract for <typeparamref name="T"/>.
        /// </exception>
        public Task WriteContentAsync<T>(T value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);

            IHttpContentSerializationFeature feature = ContentSerializationFeatureResolver.GetRequired(response.HttpContext);

            IHttpContentWriter writer;
            HttpMediaType target;

            if (response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue header)
                && HttpMediaType.TryParse(header.Value, out HttpMediaType declared))
            {
                writer = feature.GetWriter(declared)
                    ?? throw new HttpContentSerializationException(
                        $"No content writer is registered for the response's declared Content-Type '{declared}'.");
                target = declared;
            }
            else if (feature.Writers.Count > 0)
            {
                writer = feature.Writers[0];
                target = writer.MediaTypes[0];
            }
            else
            {
                throw new HttpContentSerializationException(
                    "The content-serialization registry has no writers. Register a format at builder " +
                    "time (e.g. AddJsonSerialization) before writing typed content.");
            }

            return writer.WriteAsync(response, value, typeof(T), target, cancellationToken);
        }

        /// <summary>
        /// Serializes <paramref name="value"/> to the response body as <paramref name="contentType"/>.
        /// </summary>
        /// <typeparam name="T">The declared type; must be covered by the registered serialization contracts.</typeparam>
        /// <param name="value">The value to serialize, which may be <see langword="null"/>.</param>
        /// <param name="contentType">The concrete media type to emit.</param>
        /// <param name="cancellationToken">A token that cancels the write.</param>
        /// <returns>A task that completes when the payload has been written to the response body.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="contentType"/> is empty or contains a wildcard.</exception>
        /// <exception cref="HttpContentSerializationException">
        /// No registry is composed, no writer is registered for <paramref name="contentType"/>, or
        /// the writer has no contract for <typeparamref name="T"/>.
        /// </exception>
        public Task WriteContentAsync<T>(T value, HttpMediaType contentType, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);

            if (contentType.IsEmpty || contentType.HasWildcard)
            {
                throw new ArgumentException("A concrete (wildcard-free) media type is required to write content.", nameof(contentType));
            }

            IHttpContentSerializationFeature feature = ContentSerializationFeatureResolver.GetRequired(response.HttpContext);

            IHttpContentWriter writer = feature.GetWriter(contentType)
                ?? throw new HttpContentSerializationException(
                    $"No content writer is registered for the media type '{contentType}'.");

            return writer.WriteAsync(response, value, typeof(T), contentType, cancellationToken);
        }
    }
}
