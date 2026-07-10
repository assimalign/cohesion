using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// JSON body-writing helpers for <see cref="IHttpResponse"/>, using caller-supplied
/// source-generated <see cref="JsonTypeInfo{T}"/> so the write path stays NativeAOT- and
/// trimming-safe with zero reflection.
/// </summary>
public static class HttpResponseJsonExtensions
{
    extension(IHttpResponse response)
    {
        /// <summary>
        /// Serializes <paramref name="value"/> through <paramref name="typeInfo"/> and writes it to
        /// the response body, setting <c>Content-Type</c> (default
        /// <c>application/json; charset=utf-8</c>) and <c>Content-Length</c>. The status code is
        /// left untouched.
        /// </summary>
        /// <typeparam name="T">The DTO type being serialized.</typeparam>
        /// <param name="value">The value to serialize. A <see langword="null"/> reference serializes as JSON <c>null</c>.</param>
        /// <param name="typeInfo">The source-generated serialization metadata for <typeparamref name="T"/>.</param>
        /// <param name="contentType">The <c>Content-Type</c> to set, or <see langword="null"/> for the JSON default.</param>
        /// <param name="cancellationToken">A token that cancels the body write.</param>
        /// <returns>A task that completes when the payload has been written to the response body.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> or <paramref name="typeInfo"/> is <see langword="null"/>.</exception>
        public async Task WriteJsonAsync<T>(
            T? value,
            JsonTypeInfo<T> typeInfo,
            string? contentType = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentNullException.ThrowIfNull(typeInfo);

            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                // A null reference is legal here: the serializer emits JSON null for it, so the
                // null-forgiving cast never produces an invalid write.
                JsonSerializer.Serialize(writer, value!, typeInfo);
            }

            await HttpResultWriter.WritePayloadAsync(
                response.HttpContext,
                statusCode: null,
                contentType ?? HttpResultDefaults.JsonMediaType,
                buffer.WrittenMemory,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
