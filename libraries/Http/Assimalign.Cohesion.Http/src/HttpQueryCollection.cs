using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable collection of parsed query-string values.
/// </summary>
public sealed class HttpQueryCollection : IHttpQueryCollection
{
    private readonly Dictionary<HttpQueryKey, HttpQueryValue> _store;

    /// <summary>
    /// Initializes an empty query collection.
    /// </summary>
    public HttpQueryCollection()
    {
        _store = new Dictionary<HttpQueryKey, HttpQueryValue>();
    }

    /// <summary>
    /// Initializes a query collection with the specified initial capacity.
    /// </summary>
    /// <param name="capacity">The expected entry capacity.</param>
    public HttpQueryCollection(int capacity)
    {
        _store = new Dictionary<HttpQueryKey, HttpQueryValue>(capacity);
    }

    /// <summary>
    /// Initializes a query collection from an existing store.
    /// </summary>
    /// <param name="store">The backing store to wrap.</param>
    public HttpQueryCollection(Dictionary<HttpQueryKey, HttpQueryValue>? store)
    {
        _store = store ?? new Dictionary<HttpQueryKey, HttpQueryValue>();
    }

    /// <summary>
    /// Gets or sets the value associated with the supplied query key.
    /// </summary>
    /// <param name="key">The query key.</param>
    public HttpQueryValue this[HttpQueryKey key]
    {
        get => TryGetValue(key, out HttpQueryValue value) ? value : HttpQueryValue.Empty;
        set
        {
            ThrowIfReadOnly();
            _store[key] = value;
        }
    }

    /// <summary>
    /// Gets the query keys present in the collection.
    /// </summary>
    public ICollection<HttpQueryKey> Keys => _store.Keys;

    /// <summary>
    /// Gets the query values present in the collection.
    /// </summary>
    public ICollection<HttpQueryValue> Values => _store.Values;

    /// <inheritdoc />
    public int Count => _store.Count;

    /// <summary>
    /// Gets or sets a value indicating whether the collection can be modified.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Adds a query entry to the collection.
    /// </summary>
    /// <param name="key">The query key to add.</param>
    /// <param name="value">The query value to add.</param>
    public void Add(HttpQueryKey key, HttpQueryValue value)
    {
        ThrowIfReadOnly();
        _store.Add(key, value);
    }

    /// <summary>
    /// Adds a query entry to the collection.
    /// </summary>
    /// <param name="item">The query entry to add.</param>
    public void Add(KeyValuePair<HttpQueryKey, HttpQueryValue> item)
    {
        ThrowIfReadOnly();
        _store.Add(item.Key, item.Value);
    }

    /// <summary>
    /// Removes all query entries from the collection.
    /// </summary>
    public void Clear()
    {
        ThrowIfReadOnly();
        _store.Clear();
    }

    /// <summary>
    /// Determines whether the collection contains the supplied entry.
    /// </summary>
    /// <param name="item">The entry to locate.</param>
    /// <returns><see langword="true"/> when the entry exists; otherwise <see langword="false"/>.</returns>
    public bool Contains(KeyValuePair<HttpQueryKey, HttpQueryValue> item) =>
        _store.TryGetValue(item.Key, out HttpQueryValue value) && value == item.Value;

    /// <inheritdoc />
    public bool ContainsKey(HttpQueryKey key)
    {
        return _store.ContainsKey(key);
    }

    /// <summary>
    /// Copies the collection entries into the supplied destination array.
    /// </summary>
    /// <param name="array">The destination array.</param>
    /// <param name="arrayIndex">The zero-based destination index.</param>
    public void CopyTo(KeyValuePair<HttpQueryKey, HttpQueryValue>[] array, int arrayIndex)
    {
        ((ICollection<KeyValuePair<HttpQueryKey, HttpQueryValue>>)_store).CopyTo(array, arrayIndex);
    }

    /// <summary>
    /// Removes the entry associated with the supplied key.
    /// </summary>
    /// <param name="key">The query key to remove.</param>
    /// <returns><see langword="true"/> when an entry was removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(HttpQueryKey key)
    {
        ThrowIfReadOnly();
        return _store.Remove(key);
    }

    /// <summary>
    /// Removes the supplied entry from the collection.
    /// </summary>
    /// <param name="item">The entry to remove.</param>
    /// <returns><see langword="true"/> when the entry was removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(KeyValuePair<HttpQueryKey, HttpQueryValue> item)
    {
        ThrowIfReadOnly();
        return ((ICollection<KeyValuePair<HttpQueryKey, HttpQueryValue>>)_store).Remove(item);
    }

    /// <inheritdoc />
    public bool TryGetValue(HttpQueryKey key, [MaybeNullWhen(false)] out HttpQueryValue value)
    {
        return _store.TryGetValue(key, out value);
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<HttpQueryKey, HttpQueryValue>> GetEnumerator()
    {
        return _store.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The query collection cannot be modified because it is read-only.");
        }
    }
}
