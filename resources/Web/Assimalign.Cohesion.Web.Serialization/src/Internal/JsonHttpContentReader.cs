using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// The built-in JSON request-body reader: deserializes UTF-8 JSON through the contract metadata
/// of the resolver registered at composition time (<c>AddJsonSerialization</c>) — the
/// <see cref="JsonTypeInfo"/>-based System.Text.Json entry points only, so the path is
/// reflection-free under NativeAOT.
/// </summary>
internal sealed class JsonHttpContentReader : IHttpContentReader
{
    private readonly JsonSerializerOptions _options;

    public JsonHttpContentReader(JsonSerializerOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<HttpMediaType> MediaTypes => JsonContentDefaults.MediaTypes;

    /// <inheritdoc />
    public bool CanRead(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _options.TryGetTypeInfo(type, out _);
    }

    /// <inheritdoc />
    public async ValueTask<object?> ReadAsync(IHttpRequest request, Type type, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(type);

        JsonTypeInfo typeInfo = JsonContentDefaults.GetRequiredTypeInfo(_options, type);

        return await JsonSerializer.DeserializeAsync(request.Body, typeInfo, cancellationToken).ConfigureAwait(false);
    }
}
