using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents a mutable, case-insensitive collection of HTTP headers.
/// </summary>
public sealed partial class HttpHeaderCollection : IHttpHeaderCollection
{
    private static readonly IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> _emptyEnumerator = default(Enumerator);

    private readonly HttpHeaderCollection? _readOnlySource;
    private readonly bool _usesInlineStorage;
    private Dictionary<HttpHeaderKey, HttpHeaderValue>? _store;
    private HttpHeaderKey _inlineKey;
    private HttpHeaderValue _inlineValue;
    private bool _hasInlineEntry;
    private int _version;

    /// <summary>
    /// Initializes an empty header collection.
    /// </summary>
    public HttpHeaderCollection()
    {
        _usesInlineStorage = true;
    }

    /// <summary>
    /// Initializes an empty header collection with the requested dictionary capacity.
    /// </summary>
    /// <param name="capacity">The initial number of headers the dictionary can hold.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public HttpHeaderCollection(int capacity)
    {
        _store = new Dictionary<HttpHeaderKey, HttpHeaderValue>(capacity);
    }

    /// <summary>
    /// Initializes a live view over a dictionary, or an empty collection when it is null.
    /// </summary>
    /// <param name="store">The dictionary to share with the collection.</param>
    public HttpHeaderCollection(Dictionary<HttpHeaderKey, HttpHeaderValue>? store)
    {
        _store = store;
        _usesInlineStorage = store is null;
    }

    private HttpHeaderCollection(HttpHeaderCollection source)
    {
        _readOnlySource = source;
    }

    /// <inheritdoc />
    public HttpHeaderValue this[HttpHeaderKey key]
    {
        get => TryGetValue(key, out var value) ? value : HttpHeaderValue.Empty;
        set
        {
            ThrowIfReadOnly();
            if (value.Count == 0)
            {
                Remove(key);
                return;
            }
            if (_hasInlineEntry && _inlineKey.Equals(key))
            {
                _inlineValue = value;
                return;
            }
            if (_store is not null && _store.ContainsKey(key))
            {
                _store[key] = value;
                return;
            }
            Add(key, value);
        }
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            HttpHeaderCollection source = _readOnlySource ?? this;
            return (source._hasInlineEntry ? 1 : 0) + (source._store?.Count ?? 0);
        }
    }

    /// <inheritdoc />
    public bool IsReadOnly => _readOnlySource is not null;

    /// <inheritdoc />
    public void Add(HttpHeaderKey key, HttpHeaderValue value)
    {
        ThrowIfReadOnly();
        if (_hasInlineEntry && _inlineKey.Equals(key))
        {
            throw new ArgumentException(ErrorMessages.DuplicateCollectionKey, nameof(key));
        }
        if (_usesInlineStorage && !_hasInlineEntry && (_store is null || !_store.ContainsKey(key)))
        {
            // Match Dictionary's rejection of a default (null-valued) key before storing it.
            ArgumentNullException.ThrowIfNull(key.Value);
            _inlineKey = key;
            _inlineValue = value;
            _hasInlineEntry = true;
        }
        else
        {
            (_store ??= new Dictionary<HttpHeaderKey, HttpHeaderValue>()).Add(key, value);
        }
        _version++;
    }

    /// <inheritdoc />
    public void Remove(HttpHeaderKey key)
    {
        ThrowIfReadOnly();
        if (_hasInlineEntry && _inlineKey.Equals(key))
        {
            _inlineKey = default;
            _inlineValue = default;
            _hasInlineEntry = false;
        }
        else
        {
            _store?.Remove(key);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        ThrowIfReadOnly();
        _inlineKey = default;
        _inlineValue = default;
        _hasInlineEntry = false;
        _store?.Clear();
    }

    /// <summary>
    /// Returns a read-only view over the same underlying collection. Reads (including enumeration)
    /// observe the live collection; every mutation attempt on the view throws
    /// <see cref="InvalidOperationException"/>. Returns the current instance when it is already
    /// read-only.
    /// </summary>
    /// <returns>A read-only view of this collection.</returns>
    public HttpHeaderCollection AsReadOnly() => IsReadOnly ? this : new HttpHeaderCollection(this);

    /// <inheritdoc />
    public bool ContainsKey(HttpHeaderKey key) => TryGetValue(key, out _);

    /// <inheritdoc />
    public bool TryGetValue(HttpHeaderKey key, [MaybeNullWhen(false)] out HttpHeaderValue value)
    {
        HttpHeaderCollection source = _readOnlySource ?? this;
        if (source._hasInlineEntry && source._inlineKey.Equals(key))
        {
            value = source._inlineValue;
            return true;
        }
        if (source._store is not null)
        {
            return source._store.TryGetValue(key, out value);
        }
        value = default;
        return false;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> GetEnumerator()
    {
        HttpHeaderCollection source = _readOnlySource ?? this;
        return source.Count == 0 ? _emptyEnumerator : new Enumerator(source);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        HttpHeaderCollection source = _readOnlySource ?? this;
        return source.Count == 0 ? _emptyEnumerator : new Enumerator(source, nonGeneric: true);
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(ErrorMessages.HeaderCollectionReadOnly);
        }
    }

    private struct Enumerator : IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>>
    {
        private readonly HttpHeaderCollection? _source;
        private readonly int _version;
        private readonly bool _nonGeneric;
        private Dictionary<HttpHeaderKey, HttpHeaderValue>.Enumerator _dictionaryEnumerator;
        private bool _inlineVisited;
        private bool _hasCurrent;

        internal Enumerator(HttpHeaderCollection source, bool nonGeneric = false)
        {
            _source = source;
            _version = source._version;
            _nonGeneric = nonGeneric;
            _dictionaryEnumerator = source._store?.GetEnumerator() ?? default;
        }

        public KeyValuePair<HttpHeaderKey, HttpHeaderValue> Current { get; private set; }

        object IEnumerator.Current => _nonGeneric && !_hasCurrent
            ? throw new InvalidOperationException(ErrorMessages.EnumerationPositionInvalid)
            : Current;

        public bool MoveNext()
        {
            if (_source is null)
            {
                return false;
            }
            ThrowIfModified();
            if (!_inlineVisited)
            {
                _inlineVisited = true;
                if (_source._hasInlineEntry)
                {
                    Current = new(_source._inlineKey, _source._inlineValue);
                    return _hasCurrent = true;
                }
            }
            if (_source._store is not null && _dictionaryEnumerator.MoveNext())
            {
                Current = _dictionaryEnumerator.Current;
                return _hasCurrent = true;
            }
            Current = default;
            return _hasCurrent = false;
        }

        public void Dispose() { }

        void IEnumerator.Reset()
        {
            ThrowIfModified();
            _dictionaryEnumerator = _source?._store?.GetEnumerator() ?? default;
            _inlineVisited = false;
            _hasCurrent = false;
            Current = default;
        }

        private void ThrowIfModified()
        {
            if (_source is not null && _source._version != _version)
            {
                throw new InvalidOperationException(ErrorMessages.CollectionModifiedDuringEnumeration);
            }
        }
    }
}
