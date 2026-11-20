using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable collection of parsed query-string values.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HttpQueryCollection {Count,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HttpQueryCollection : IHttpQueryCollection
{
    private readonly Dictionary<HttpQueryKey, HttpQueryValue> _store;
    private HttpQuery? _source;

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
    /// Gets the ordered values for a key, or replaces all of its values.
    /// </summary>
    /// <param name="key">The query key.</param>
    public HttpQueryValue this[HttpQueryKey key]
    {
        get => TryGetValue(key, out HttpQueryValue value) ? value : HttpQueryValue.Empty;
        set
        {
            ThrowIfReadOnly();
            _store[key] = value;
            _source = null;
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
    /// Gets the query text this collection was parsed from, or <see langword="null"/> when the collection
    /// was built in code or has changed since it was parsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HttpQuery.Parse"/> sets it. Any call to <see cref="Add(HttpQueryKey, HttpQueryValue)"/>,
    /// the indexer's setter, <see cref="Clear"/>, or a <c>Remove</c> that removes an entry clears it, because
    /// the text would no longer describe the collection. Changing <see cref="IsReadOnly"/> keeps it.
    /// </para>
    /// <para>
    /// The parsed collection does not round-trip: keys compare case-insensitively, repeated keys are
    /// grouped, a parameter with an empty name is skipped, and <see cref="HttpQuery.Create"/> re-encodes
    /// every name and value. Code that must pass the query on as the client sent it, such as a proxy or a
    /// signed-URL check, reads this property first and falls back to <see cref="HttpQuery.Create"/> when it
    /// is <see langword="null"/>.
    /// </para>
    /// </remarks>
    public HttpQuery? Source => _source;

    /// <summary>
    /// Appends query values, preserving existing occurrences and their order.
    /// </summary>
    /// <param name="key">The query key to add.</param>
    /// <param name="value">The query value to add.</param>
    public void Add(HttpQueryKey key, HttpQueryValue value)
    {
        ThrowIfReadOnly();
        _store[key] = _store.TryGetValue(key, out HttpQueryValue existing)
            ? HttpQueryValue.Concat(existing, value)
            : value;
        _source = null;
    }

    /// <summary>
    /// Appends query values, preserving existing occurrences and their order.
    /// </summary>
    /// <param name="item">The query entry to add.</param>
    public void Add(KeyValuePair<HttpQueryKey, HttpQueryValue> item)
    {
        Add(item.Key, item.Value);
    }

    /// <summary>
    /// Removes all query entries from the collection.
    /// </summary>
    public void Clear()
    {
        ThrowIfReadOnly();
        _store.Clear();
        _source = null;
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
        return ClearSourceWhen(_store.Remove(key));
    }

    /// <summary>
    /// Removes the supplied entry from the collection.
    /// </summary>
    /// <param name="item">The entry to remove.</param>
    /// <returns><see langword="true"/> when the entry was removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(KeyValuePair<HttpQueryKey, HttpQueryValue> item)
    {
        ThrowIfReadOnly();
        return ClearSourceWhen(((ICollection<KeyValuePair<HttpQueryKey, HttpQueryValue>>)_store).Remove(item));
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

    // Records the query text a parse produced the collection from. Called last by HttpQuery.Parse, after
    // the Add calls that would otherwise clear it.
    internal void SetSource(HttpQuery source)
    {
        _source = source;
    }

    private bool ClearSourceWhen(bool removed)
    {
        if (removed)
        {
            _source = null;
        }

        return removed;
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(ErrorMessages.QueryCollectionReadOnly);
        }
    }

    private sealed class DebugView
    {
        private readonly HttpQueryCollection _target;

        public DebugView(HttpQueryCollection value)
        {
            _target = value;
        }

        public ICollection<HttpQueryKey> Keys => _target.Keys;
        public ICollection<HttpQueryValue> Values => _target.Values;
        public int Count => _target.Count;
        public bool IsReadOnly => _target.IsReadOnly;
        public HttpQuery? Source => _target.Source;
    }
}
