using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// A compact case-insensitive route value dictionary optimized for small collections.
/// </summary>
[DebuggerTypeProxy(typeof(DictionaryDebugView<string, object?>))]
[DebuggerDisplay("Count = {Count}")]
public class RouteValueDictionary : IDictionary<string, object?>, IReadOnlyDictionary<string, object?>
{
    /// <summary>
    /// Enumerates dictionary entries without allocations.
    /// </summary>
    public struct Enumerator : IEnumerator<KeyValuePair<string, object?>>
    {
        private readonly RouteValueDictionary _dictionary;
        private int _index;

        internal Enumerator(RouteValueDictionary dictionary)
        {
            ArgumentNullException.ThrowIfNull(dictionary);

            _dictionary = dictionary;
            _index = 0;
            Current = default;
        }

        /// <inheritdoc />
        public KeyValuePair<string, object?> Current { get; private set; }

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public void Dispose()
        {
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if ((uint)_index < (uint)_dictionary._count)
            {
                Current = _dictionary._arrayStorage[_index++];
                return true;
            }

            _index = _dictionary._count;
            Current = default;
            return false;
        }

        /// <inheritdoc />
        public void Reset()
        {
            _index = 0;
            Current = default;
        }
    }

    private const int DefaultCapacity = 4;

    internal KeyValuePair<string, object?>[] _arrayStorage;

    private int _count;

    /// <summary>
    /// Creates an empty route value dictionary.
    /// </summary>
    public RouteValueDictionary()
    {
        _arrayStorage = Array.Empty<KeyValuePair<string, object?>>();
    }

    /// <summary>
    /// Creates a route value dictionary populated from an existing sequence.
    /// </summary>
    /// <param name="values">The values to copy.</param>
    public RouteValueDictionary(IEnumerable<KeyValuePair<string, object?>>? values)
    {
        _arrayStorage = Array.Empty<KeyValuePair<string, object?>>();

        if (values is not null)
        {
            Initialize(values);
        }
    }

    /// <inheritdoc />
    public object? this[string key]
    {
        get
        {
            if (key is null)
            {
                ThrowArgumentNullExceptionForKey();
            }

            TryGetValue(key, out object? value);
            return value;
        }
        set
        {
            if (key is null)
            {
                ThrowArgumentNullExceptionForKey();
            }

            int index = FindIndex(key);
            if (index < 0)
            {
                EnsureCapacity(_count + 1);
                _arrayStorage[_count++] = new KeyValuePair<string, object?>(key, value);
                return;
            }

            _arrayStorage[index] = new KeyValuePair<string, object?>(key, value);
        }
    }

    /// <inheritdoc />
    public int Count => _count;

    /// <inheritdoc />
    public ICollection<string> Keys
    {
        get
        {
            string[] keys = new string[_count];
            for (int index = 0; index < _count; index++)
            {
                keys[index] = _arrayStorage[index].Key;
            }

            return keys;
        }
    }

    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => Keys;

    /// <inheritdoc />
    public ICollection<object?> Values
    {
        get
        {
            object?[] values = new object?[_count];
            for (int index = 0; index < _count; index++)
            {
                values[index] = _arrayStorage[index].Value;
            }

            return values;
        }
    }

    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values => Values;

    bool ICollection<KeyValuePair<string, object?>>.IsReadOnly => false;

    /// <inheritdoc />
    public void Add(string key, object? value)
    {
        if (key is null)
        {
            ThrowArgumentNullExceptionForKey();
        }

        if (ContainsKeyArray(key))
        {
            throw new ArgumentException($"An element with the key '{key}' already exists in the RouteValueDictionary.", nameof(key));
        }

        EnsureCapacity(_count + 1);
        _arrayStorage[_count++] = new KeyValuePair<string, object?>(key, value);
    }

    void ICollection<KeyValuePair<string, object?>>.Add(KeyValuePair<string, object?> item)
    {
        Add(item.Key, item.Value);
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_count == 0)
        {
            return;
        }

        Array.Clear(_arrayStorage, 0, _count);
        _count = 0;
    }

    /// <inheritdoc />
    public bool ContainsKey(string key)
    {
        if (key is null)
        {
            ThrowArgumentNullExceptionForKey();
        }

        return ContainsKeyArray(key);
    }

    bool ICollection<KeyValuePair<string, object?>>.Contains(KeyValuePair<string, object?> item)
    {
        return TryGetValue(item.Key, out object? value) &&
            EqualityComparer<object?>.Default.Equals(value, item.Value);
    }

    /// <inheritdoc />
    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
    }

    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc />
    public bool Remove(string key)
    {
        if (key is null)
        {
            ThrowArgumentNullExceptionForKey();
        }

        return RemoveCore(key, out _);
    }

    bool ICollection<KeyValuePair<string, object?>>.Remove(KeyValuePair<string, object?> item)
    {
        int index = FindIndex(item.Key);
        if (index < 0 ||
            !EqualityComparer<object?>.Default.Equals(_arrayStorage[index].Value, item.Value))
        {
            return false;
        }

        RemoveAt(index, out _);
        return true;
    }

    /// <summary>
    /// Removes the value associated with the specified key and returns the removed value.
    /// </summary>
    /// <param name="key">The route value key.</param>
    /// <param name="value">The removed value when successful.</param>
    /// <returns><see langword="true"/> when the key existed; otherwise <see langword="false"/>.</returns>
    public bool Remove(string key, out object? value)
    {
        if (key is null)
        {
            ThrowArgumentNullExceptionForKey();
        }

        return RemoveCore(key, out value);
    }

    /// <summary>
    /// Attempts to add the supplied key/value pair.
    /// </summary>
    /// <param name="key">The route value key.</param>
    /// <param name="value">The route value.</param>
    /// <returns><see langword="true"/> when the key was added; otherwise <see langword="false"/>.</returns>
    public bool TryAdd(string key, object? value)
    {
        if (key is null)
        {
            ThrowArgumentNullExceptionForKey();
        }

        if (ContainsKeyArray(key))
        {
            return false;
        }

        EnsureCapacity(_count + 1);
        _arrayStorage[_count++] = new KeyValuePair<string, object?>(key, value);
        return true;
    }

    /// <inheritdoc />
    public bool TryGetValue(string key, out object? value)
    {
        if (key is null)
        {
            ThrowArgumentNullExceptionForKey();
        }

        return TryFindItem(key, out value);
    }

    void ICollection<KeyValuePair<string, object?>>.CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);

        if (arrayIndex < 0 || arrayIndex > array.Length || array.Length - arrayIndex < Count)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        }

        if (_count > 0)
        {
            Array.Copy(_arrayStorage, 0, array, arrayIndex, _count);
        }
    }

    private void Initialize(IEnumerable<KeyValuePair<string, object?>> keyValueEnumerable)
    {
        foreach (KeyValuePair<string, object?> item in keyValueEnumerable)
        {
            Add(item.Key, item.Value);
        }
    }

    [DoesNotReturn]
    private static void ThrowArgumentNullExceptionForKey()
    {
        throw new ArgumentNullException("key");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int capacity)
    {
        if (_arrayStorage.Length >= capacity)
        {
            return;
        }

        int newCapacity = _arrayStorage.Length == 0
            ? DefaultCapacity
            : _arrayStorage.Length * 2;

        if (newCapacity < capacity)
        {
            newCapacity = capacity;
        }

        KeyValuePair<string, object?>[] array = new KeyValuePair<string, object?>[newCapacity];
        if (_count > 0)
        {
            Array.Copy(_arrayStorage, 0, array, 0, _count);
        }

        _arrayStorage = array;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindIndex(string key)
    {
        for (int index = 0; index < _count; index++)
        {
            if (string.Equals(_arrayStorage[index].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryFindItem(string key, out object? value)
    {
        int index = FindIndex(key);
        if (index >= 0)
        {
            value = _arrayStorage[index].Value;
            return true;
        }

        value = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ContainsKeyArray(string key)
    {
        return FindIndex(key) >= 0;
    }

    private bool RemoveCore(string key, out object? value)
    {
        int index = FindIndex(key);
        if (index < 0)
        {
            value = null;
            return false;
        }

        RemoveAt(index, out value);
        return true;
    }

    private void RemoveAt(int index, out object? value)
    {
        value = _arrayStorage[index].Value;

        _count--;
        if (index < _count)
        {
            Array.Copy(_arrayStorage, index + 1, _arrayStorage, index, _count - index);
        }

        _arrayStorage[_count] = default;
    }

    internal sealed class DictionaryDebugView<TKey, TValue> where TKey : notnull
    {
        private readonly IDictionary<TKey, TValue> _dict;

        public DictionaryDebugView(IDictionary<TKey, TValue> dictionary)
        {
            _dict = dictionary;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public DictionaryItemDebugView<TKey, TValue>[] Items
        {
            get
            {
                KeyValuePair<TKey, TValue>[] items = new KeyValuePair<TKey, TValue>[_dict.Count];
                _dict.CopyTo(items, 0);

                DictionaryItemDebugView<TKey, TValue>[] views = new DictionaryItemDebugView<TKey, TValue>[items.Length];
                for (int index = 0; index < views.Length; index++)
                {
                    views[index] = new DictionaryItemDebugView<TKey, TValue>(items[index]);
                }

                return views;
            }
        }
    }

    [DebuggerDisplay("{Value}", Name = "[{Key}]")]
    internal readonly struct DictionaryItemDebugView<TKey, TValue>
    {
        public DictionaryItemDebugView(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        public DictionaryItemDebugView(KeyValuePair<TKey, TValue> keyValue)
        {
            Key = keyValue.Key;
            Value = keyValue.Value;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
        public TKey Key { get; }

        [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
        public TValue Value { get; }
    }
}
