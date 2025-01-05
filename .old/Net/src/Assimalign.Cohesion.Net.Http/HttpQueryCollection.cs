using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Net.Http;


public sealed partial class HttpQueryCollection : IHttpQueryCollection
{
    private static readonly HttpQueryKey[] EmptyKeys = Array.Empty<HttpQueryKey>();
    private static readonly HttpQueryValue[] EmptyValues = Array.Empty<HttpQueryValue>();
    private static readonly IEnumerator<KeyValuePair<HttpQueryKey, HttpQueryValue>> EmptyIEnumeratorType = default(Enumerator);
    private static readonly IEnumerator EmptyIEnumerator = default(Enumerator);

    private Dictionary<HttpQueryKey, HttpQueryValue>? store;

    public HttpQueryCollection() { }
    public HttpQueryCollection(int capacity)
    {
        EnsureStore(capacity);
    }
    public HttpQueryCollection(Dictionary<HttpQueryKey, HttpQueryValue>? store)
    {
        this.store = store;
    }


    public HttpQueryValue this[HttpQueryKey key]
    {
        get
        {
            if (store == null)
            {
                return HttpQueryValue.Empty;
            }
            if (TryGetValue(key, out var value))
            {
                return value;
            }
            return HttpQueryValue.Empty;
        }
        set
        {
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }
            ThrowIfReadOnly();
            //if (value.Count == 0)
            //{
            //    store?.Remove(key);
            //    return;
            //}
            EnsureStore(1);
            store![key] = value;
        }
    }

    public ICollection<HttpQueryKey> Keys => store == null ? EmptyKeys : store!.Keys;
    public ICollection<HttpQueryValue> Values => store == null ? EmptyValues : store!.Values;

    public int Count => store?.Count ?? 0;
    public bool IsReadOnly { get; set; }

    public void Add(HttpQueryKey key, HttpQueryValue value)
    {
        ThrowIfReadOnly();
        EnsureStore(1);
        store!.Add(key, value);
    }
    public void Add(KeyValuePair<HttpQueryKey, HttpQueryValue> item)
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
    public bool Contains(KeyValuePair<HttpQueryKey, HttpQueryValue> item)
    {
        if (store == null || !store!.TryGetValue(item.Key, out var value) || value != item.Value)
        {
            return false;
        }
        return true;
    }
    public bool ContainsKey(HttpQueryKey key)
    {
        if (store == null)
        {
            return false;
        }
        return store!.ContainsKey(key);
    }
    public void CopyTo(KeyValuePair<HttpQueryKey, HttpQueryValue>[] array, int arrayIndex)
    {
        if (store == null)
        {
            return;
        }
        foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> item in store!)
        {
            KeyValuePair<HttpQueryKey, HttpQueryValue> keyValuePair = (array[arrayIndex] = item);
            arrayIndex++;
        }
    }
    public bool Remove(HttpQueryKey key)
    {
        ThrowIfReadOnly();
        if (store == null)
        {
            return false;
        }
        return store!.Remove(key);
    }
    public bool Remove(KeyValuePair<HttpQueryKey, HttpQueryValue> item)
    {
        ThrowIfReadOnly();
        if (store == null)
        {
            return false;
        }
        if (store!.TryGetValue(item.Key, out var value) && item.Value == value)
        {
            return store!.Remove(item.Key);
        }
        return false;
    }
    public bool TryGetValue(HttpQueryKey key, [MaybeNullWhen(false)] out HttpQueryValue value)
    {
        if (store == null)
        {
            value = default(HttpQueryValue);
            return false;
        }
        return store!.TryGetValue(key, out value);
    }


    public IEnumerator<KeyValuePair<HttpQueryKey, HttpQueryValue>> GetEnumerator()
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
            store = new Dictionary<HttpQueryKey, HttpQueryValue>(capacity);
        }
    }
    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The response headers cannot be modified because the response has already started.");
        }
    }


    private struct Enumerator : IEnumerator<KeyValuePair<HttpQueryKey, HttpQueryValue>>, IEnumerator, IDisposable
    {
        private Dictionary<HttpQueryKey, HttpQueryValue>.Enumerator enumerator;
        private readonly bool isNotEmpty;

        public KeyValuePair<HttpQueryKey, HttpQueryValue> Current
        {
            get
            {
                if (isNotEmpty)
                {
                    return enumerator.Current;
                }
                return default(KeyValuePair<HttpQueryKey, HttpQueryValue>);
            }
        }

        object IEnumerator.Current => Current;
        internal Enumerator(Dictionary<HttpQueryKey, HttpQueryValue>.Enumerator dictionaryEnumerator)
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
}