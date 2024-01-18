using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Net.Http;

public sealed partial class HttpHeaderCollection : IHttpHeaderCollection
{
    private static readonly HttpHeaderKey[] EmptyKeys = Array.Empty<HttpHeaderKey>();
    private static readonly HttpHeaderValue[] EmptyValues = Array.Empty<HttpHeaderValue>();
    private static readonly IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> EmptyIEnumeratorType = default(Enumerator);
    private static readonly IEnumerator EmptyIEnumerator = default(Enumerator);

    private Dictionary<HttpHeaderKey, HttpHeaderValue>? store;

    public HttpHeaderCollection() { }
    public HttpHeaderCollection(int capacity)
    {
        EnsureStore(capacity);
    }
    public HttpHeaderCollection(Dictionary<HttpHeaderKey, HttpHeaderValue>? store)
    {
        this.store = store;
    }


    public HttpHeaderValue this[HttpHeaderKey key]
    {
        get
        {
            if (store == null)
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
                store?.Remove(key);
                return;
            }
            EnsureStore(1);
            store![key] = value;
        }
    }

    public ICollection<HttpHeaderKey> Keys => store == null ? EmptyKeys : store!.Keys;
    public ICollection<HttpHeaderValue> Values => store == null ? EmptyValues : store!.Values;

    public int Count => store?.Count ?? 0;
    public bool IsReadOnly { get; set; }

    public void Add(HttpHeaderKey key, HttpHeaderValue value)
    {
        ThrowIfReadOnly();
        EnsureStore(1);
        store!.Add(key, value);
    }
    public void Add(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    {
        ThrowIfReadOnly();
        EnsureStore(1);
        store!.Add(item.Key, item.Value);
    }
    public void Clear()
    {
        ThrowIfReadOnly();
        store?.Clear();
    }
    public bool Contains(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    {
        if (store == null || !store!.TryGetValue(item.Key, out var value) || !HttpHeaderValue.Equals(value, item.Value))
        {
            return false;
        }
        return true;
    }
    public bool ContainsKey(HttpHeaderKey key)
    {
        if (store == null)
        {
            return false;
        }
        return store!.ContainsKey(key);
    }
    public void CopyTo(KeyValuePair<HttpHeaderKey, HttpHeaderValue>[] array, int arrayIndex)
    {
        if (store == null)
        {
            return;
        }
        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> item in store!)
        {
            var keyValuePair = (array[arrayIndex] = item);
            arrayIndex++;
        }
    }  
    public bool Remove(HttpHeaderKey key)
    {
        ThrowIfReadOnly();
        if (store == null)
        {
            return false;
        }
        return store!.Remove(key);
    }
    public bool Remove(KeyValuePair<HttpHeaderKey, HttpHeaderValue> item)
    {
        ThrowIfReadOnly();
        if (store == null)
        {
            return false;
        }
        if (store!.TryGetValue(item.Key, out var value) && HttpHeaderValue.Equals(item.Value, value))
        {
            return store!.Remove(item.Key);
        }
        return false;
    }
    public bool TryGetValue(HttpHeaderKey key, [MaybeNullWhen(false)] out HttpHeaderValue value)
    {
        if (store == null)
        {
            value = default(HttpHeaderValue);
            return false;
        }
        return store!.TryGetValue(key, out value);
    }
    
    
    public IEnumerator<KeyValuePair<HttpHeaderKey, HttpHeaderValue>> GetEnumerator()
    {
        if (store == null || store!.Count == 0)
        {
            return default(Enumerator);
        }
        return new Enumerator(store!.GetEnumerator());
    }
    IEnumerator IEnumerable.GetEnumerator()
    {
        if (store == null || store!.Count == 0)
        {
            return EmptyIEnumerator;
        }
        return store!.GetEnumerator();
    }


    [MemberNotNull("store")]
    private void EnsureStore(int capacity)
    {
        if (store == null)
        {
            store = new Dictionary<HttpHeaderKey, HttpHeaderValue>(capacity);
        }
    }
    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The response headers cannot be modified because the response has already started.");
        }
    }


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





    public HttpHeaderValue? Accepts => GetHeaderValue(HttpHeader.Accept);
    public HttpHeaderValue? ContentType => GetHeaderValue(HttpHeader.ContentType);
    public HttpHeaderValue? ContentLength => GetHeaderValue(HttpHeader.ContentLength);
    public HttpHeaderValue? TransferEncoding => GetHeaderValue(HttpHeader.TransferEncoding);
    public HttpHeaderValue? Connection => GetHeaderValue(HttpHeader.Connection);


    private HttpHeaderValue? GetHeaderValue(string key)
    {
        var value = this[key];

        if (value.IsEmpty)
        {
            return null;
        }

        return value;
    }
}