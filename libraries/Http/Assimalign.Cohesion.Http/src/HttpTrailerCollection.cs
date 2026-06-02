using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpTrailerCollection"/>. Wraps an inner
/// <see cref="IHttpHeaderCollection"/> for storage (a trailer section is a field
/// section) and adds the <see cref="IsSupported"/> capability signal.
/// </summary>
/// <remarks>
/// When <see cref="IsSupported"/> is <see langword="false"/> the collection is
/// empty and every mutating operation throws <see cref="InvalidOperationException"/>,
/// so trailers that the exchange cannot transmit fail loudly at the point of
/// addition rather than being silently dropped on the wire. The shared
/// <see cref="Unsupported"/> instance is the default for exchanges that do not
/// carry trailers.
/// </remarks>
public sealed class HttpTrailerCollection : IHttpTrailerCollection
{
    private readonly IHttpHeaderCollection _fields;

    /// <summary>
    /// Initializes a trailer collection with a fresh, empty backing store.
    /// </summary>
    /// <param name="isSupported">Whether the exchange surfaces trailers.</param>
    public HttpTrailerCollection(bool isSupported = true)
        : this(new HttpHeaderCollection(), isSupported)
    {
    }

    /// <summary>
    /// Initializes a trailer collection over an existing field collection (for
    /// example the trailer fields a transport already parsed).
    /// </summary>
    /// <param name="fields">The backing field collection.</param>
    /// <param name="isSupported">Whether the exchange surfaces trailers.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    public HttpTrailerCollection(IHttpHeaderCollection fields, bool isSupported = true)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = fields;
        IsSupported = isSupported;
    }

    /// <summary>
    /// A shared, empty, read-only trailer collection for exchanges that do not
    /// support trailers. Mutating it throws.
    /// </summary>
    public static HttpTrailerCollection Unsupported { get; } = new(isSupported: false);

    /// <inheritdoc />
    public bool IsSupported { get; }

    /// <inheritdoc />
    public int Count => _fields.Count;

    /// <inheritdoc />
    public bool IsReadOnly => !IsSupported || _fields.IsReadOnly;

    /// <inheritdoc />
    public HttpHeaderValue this[HttpHeaderKey key]
    {
        get => _fields[key];
        set
        {
            ThrowIfUnsupported();
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
        ThrowIfUnsupported();
        _fields.Add(key, value);
    }

    /// <inheritdoc />
    public void Remove(HttpHeaderKey key)
    {
        ThrowIfUnsupported();
        _fields.Remove(key);
    }

    /// <inheritdoc />
    public void Clear()
    {
        ThrowIfUnsupported();
        _fields.Clear();
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void ThrowIfUnsupported()
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException(
                "This exchange does not support a trailer section, so trailers cannot be added. " +
                "Check IHttpTrailerCollection.IsSupported before adding trailers.");
        }
    }
}
