using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The field store behind a transport response's trailer collection on HTTP/2 and HTTP/3. It refuses,
/// as they are added, the fields those versions cannot send in a trailer section (see
/// <see cref="HttpTrailerFieldRules.EnsureSendable"/>), and keeps the rest for the send path to encode.
/// </summary>
/// <remarks>
/// <see cref="HttpTrailerCollection"/> forwards every mutation here once its own support check passes,
/// so the application sees the rejection from <c>Response.Trailers.Add</c> or the indexer. Reads, removal
/// and clearing are plain pass-throughs.
/// </remarks>
internal sealed class TransportHttpTrailerFields : IHttpHeaderCollection
{
    private readonly HttpHeaderCollection _fields = new();

    /// <inheritdoc />
    public int Count => _fields.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public HttpHeaderValue this[HttpHeaderKey key]
    {
        get => _fields[key];
        set
        {
            HttpTrailerFieldRules.EnsureSendable(key, value);
            _fields[key] = value;
        }
    }

    /// <inheritdoc />
    public bool ContainsKey(HttpHeaderKey key) => _fields.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(HttpHeaderKey key, out HttpHeaderValue value) => _fields.TryGetValue(key, out value);

    /// <inheritdoc />
    public void Add(HttpHeaderKey key, HttpHeaderValue value)
    {
        HttpTrailerFieldRules.EnsureSendable(key, value);
        _fields.Add(key, value);
    }

    /// <inheritdoc />
    public void Remove(HttpHeaderKey key) => _fields.Remove(key);

    /// <inheritdoc />
    public void Clear() => _fields.Clear();

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
