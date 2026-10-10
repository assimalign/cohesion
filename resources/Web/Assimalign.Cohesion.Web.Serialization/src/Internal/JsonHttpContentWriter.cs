using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// The built-in JSON response-body writer: serializes UTF-8 JSON straight to the response body
/// through the contract metadata of the resolver registered at composition time
/// (<c>AddJsonSerialization</c>) — the <see cref="JsonTypeInfo"/>-based System.Text.Json entry
/// points only, so the path is reflection-free under NativeAOT. The body is streamed, so no
/// <c>Content-Length</c> is set; transports frame the response per protocol.
/// </summary>
internal sealed class JsonHttpContentWriter : IHttpContentWriter
{
    private readonly JsonSerializerOptions _options;

    public JsonHttpContentWriter(JsonSerializerOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<HttpMediaType> MediaTypes => JsonContentDefaults.MediaTypes;

    /// <inheritdoc />
    public bool CanWrite(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _options.TryGetTypeInfo(type, out _);
    }

    /// <summary>
    /// Resolves the contract this writer serializes <paramref name="type"/> with, from the frozen
    /// options the JSON pair was registered with. Backs the public
    /// <c>IHttpContentSerializationFeature.TryGetJsonTypeInfo</c> seam.
    /// </summary>
    /// <param name="type">The declared CLR type.</param>
    /// <param name="typeInfo">The contract, when the registered resolver covers the type.</param>
    /// <returns><see langword="true"/> when the registered resolver covers the type.</returns>
    internal bool TryGetTypeInfo(Type type, [NotNullWhen(true)] out JsonTypeInfo? typeInfo)
        => _options.TryGetTypeInfo(type, out typeInfo);

    /// <inheritdoc />
    public async Task WriteAsync(IHttpResponse response, object? value, Type type, HttpMediaType contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(type);

        JsonTypeInfo typeInfo = JsonContentDefaults.GetRequiredTypeInfo(_options, type);

        // The Content-Type header must be in place before the first body write commits the
        // response head. JSON is always emitted as UTF-8 here; the charset parameter is stamped
        // for interoperability unless the caller's media type already carries one.
        response.Headers[HttpHeaderKey.ContentType] = contentType.Charset is null
            ? $"{contentType}; charset=utf-8"
            : contentType.ToString();

        await JsonSerializer.SerializeAsync(response.Body, value, typeInfo, cancellationToken).ConfigureAwait(false);
    }
}
