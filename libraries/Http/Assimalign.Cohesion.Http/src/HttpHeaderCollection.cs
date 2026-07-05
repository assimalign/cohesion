using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http;

public sealed partial class HttpHeaderCollection : IHttpHeaderCollection
{
    private static readonly IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> EmptyIEnumeratorType = default(Enumerator);
    private static readonly IEnumerator EmptyIEnumerator = default(Enumerator);

    private Dictionary<HttpHeaderKey, HttpHeaderValue> _store;

    #region Constructors

    public HttpHeaderCollection()
    {
        _store = new Dictionary<HttpHeaderKey, HttpHeaderValue>();
    }
    public HttpHeaderCollection(int capacity)
    {
        _store = new Dictionary<HttpHeaderKey, HttpHeaderValue>(capacity);
    }
    public HttpHeaderCollection(Dictionary<HttpHeaderKey, HttpHeaderValue>? store)
    {
        _store = store ?? new Dictionary<HttpHeaderKey, HttpHeaderValue>();
    }
    private HttpHeaderCollection(Dictionary<HttpHeaderKey, HttpHeaderValue> store, bool isReadOnly)
    {
        _store = store;
        IsReadOnly = isReadOnly;
    }

    #endregion

    #region Properties

    public HttpHeaderValue this[HttpHeaderKey key]
    {
        get
        {
            if (_store == null)
            {
                return HttpHeaderValue.Empty;
            }
            if (TryGetValue(key, out var value))
            {
                return value;
            }
            return HttpHeaderValue.Empty;
        }
        set
        {
            ThrowIfReadOnly();
            if (value.Count == 0)
            {
                _store?.Remove(key);
                return;
            }
            EnsureStore(1);
            _store![key] = value;
        }
    }
    public int Count => _store.Count;
    public bool IsReadOnly { get; }

    #endregion

    public void Add(HttpHeaderKey key, HttpHeaderValue value)
    {
        ThrowIfReadOnly();
        EnsureStore(1);
        _store!.Add(key, value);
    }
    public void Remove(HttpHeaderKey key)
    {
        ThrowIfReadOnly();
        //if (_store == null)
        //{
        //    return false;
        //}
        _store!.Remove(key);
    }
    public void Clear()
    {
        ThrowIfReadOnly();
        _store?.Clear();
    }

    /// <summary>
    /// Returns a read-only view over the same underlying store. Reads (including enumeration)
    /// observe the live collection; every mutation attempt on the view throws
    /// <see cref="InvalidOperationException"/>. Returns the current instance when it is already
    /// read-only.
    /// </summary>
    /// <returns>A read-only view of this collection.</returns>
    public HttpHeaderCollection AsReadOnly()
    {
        return IsReadOnly ? this : new HttpHeaderCollection(_store, isReadOnly: true);
    }
    public bool ContainsKey(HttpHeaderKey key)
    {
        if (_store == null)
        {
            return false;
        }
        return _store!.ContainsKey(key);
    }
    public bool TryGetValue(HttpHeaderKey key, [MaybeNullWhen(false)] out HttpHeaderValue value)
    {
        if (_store == null)
        {
            value = default(HttpHeaderValue);
            return false;
        }
        return _store!.TryGetValue(key, out value);
    }
    //public void Add(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    //{
    //    ThrowIfReadOnly();
    //    EnsureStore(1);
    //    _store!.Add(item.Key, item.Value);
    //}
    //public bool Contains(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    //{
    //    if (_store == null || !_store!.TryGetValue(item.Key, out var value) || !HttpHeaderValue.Equals(value, item.Value))
    //    {
    //        return false;
    //    }
    //    return true;
    //}
    //public void CopyTo(KeyValuePair<HttpHeaderKey, HttpHeaderValue>[] array, int arrayIndex)
    //{
    //    if (_store == null)
    //    {
    //        return;
    //    }
    //    foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> item in _store!)
    //    {
    //        var keyValuePair = (array[arrayIndex] = item);
    //        arrayIndex++;
    //    }
    //}  

    //public bool Remove(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    //{
    //    ThrowIfReadOnly();
    //    if (_store == null)
    //    {
    //        return false;
    //    }
    //    if (_store!.TryGetValue(item.Key, out var value) && HttpHeaderValue.Equals(item.Value, value))
    //    {
    //        return _store!.Remove(item.Key);
    //    }
    //    return false;
    //}

    public IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> GetEnumerator()
    {
        if (_store == null || _store!.Count == 0)
        {
            return default(Enumerator);
        }
        return new Enumerator(_store!.GetEnumerator());
    }
    IEnumerator IEnumerable.GetEnumerator()
    {
        if (_store == null || _store!.Count == 0)
        {
            return EmptyIEnumerator;
        }
        return _store!.GetEnumerator();
    }


    private void EnsureStore(int capacity)
    {
        if (_store == null)
        {
            _store = new Dictionary<HttpHeaderKey, HttpHeaderValue>(capacity);
        }
    }
    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The header collection is read-only and cannot be modified.");
        }
    }

    #region Partials
    private struct Enumerator : IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>>, IEnumerator, IDisposable
    {
        private Dictionary<HttpHeaderKey, HttpHeaderValue>.Enumerator enumerator;
        private readonly bool isNotEmpty;

        public KeyValuePair<HttpHeaderKey, HttpHeaderValue> Current
        {
            get
            {
                if (isNotEmpty)
                {
                    return enumerator.Current;
                }
                return default(KeyValuePair<HttpHeaderKey, HttpHeaderValue>);
            }
        }

        object IEnumerator.Current => Current;
        internal Enumerator(Dictionary<HttpHeaderKey, HttpHeaderValue>.Enumerator dictionaryEnumerator)
        {
            enumerator = dictionaryEnumerator;
            isNotEmpty = true;
        }
        public bool MoveNext() => isNotEmpty ? enumerator.MoveNext() : false;
        public void Dispose() { }
        void IEnumerator.Reset()
        {
            if (isNotEmpty)
            {
                ((IEnumerator)enumerator).Reset();
            }
        }
    }
    #endregion
}