using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing;


[DebuggerTypeProxy(typeof(DictionaryDebugView<string, object>))]
[DebuggerDisplay("Count = {Count}")]
public class RouteValueDictionary : IDictionary<string, object?>, ICollection<KeyValuePair<string, object?>>, IEnumerable<KeyValuePair<string, object?>>, IEnumerable, IReadOnlyDictionary<string, object?>, IReadOnlyCollection<KeyValuePair<string, object?>>
{
    public struct Enumerator : IEnumerator<KeyValuePair<string, object?>>, IEnumerator, IDisposable
    {
        private readonly RouteValueDictionary _dictionary;

        private int _index;

        public KeyValuePair<string, object?> Current { get; private set; }

        object IEnumerator.Current => Current;

        public Enumerator(RouteValueDictionary dictionary)
        {
            ArgumentNullException.ThrowIfNull(dictionary, "dictionary");
            _dictionary = dictionary;
            Current = default(KeyValuePair<string, object?>);
            _index = 0;
        }

        public void Dispose()
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            RouteValueDictionary dictionary = _dictionary;
            if ((uint)_index < (uint)dictionary._count)
            {
                Current = dictionary._arrayStorage[_index];
                _index++;
                return true;
            }
            return MoveNextRare();
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The constructor that would result in _propertyStorage being non-null is annotated with RequiresUnreferencedCodeAttribute. We do not need to additionally produce an error in this method since it is shared by trimmer friendly code paths.")]
        private bool MoveNextRare()
        {
            RouteValueDictionary dictionary = _dictionary;
            _index = dictionary._count;
            Current = default(KeyValuePair<string, object?>);
            return false;
        }

        public void Reset()
        {
            Current = default(KeyValuePair<string, object?>);
            _index = 0;
        }
    }

    private const int DefaultCapacity = 4;

    internal KeyValuePair<string, object?>[] _arrayStorage;

    private int _count;

    public object? this[string key]
    {
        get
        {
            if (key == null)
            {
                ThrowArgumentNullExceptionForKey();
            }
            TryGetValue(key, out object? value);
            return value;
        }
        set
        {
            if (key == null)
            {
                ThrowArgumentNullExceptionForKey();
            }
            EnsureCapacity(_count);
            int num = FindIndex(key);
            if (num < 0)
            {
                EnsureCapacity(_count + 1);
                _arrayStorage[_count++] = new KeyValuePair<string, object?>(key, value);
            }
            else
            {
                _arrayStorage[num] = new KeyValuePair<string, object?>(key, value);
            }
        }
    }

    public int Count => _count;

    bool ICollection<KeyValuePair<string, object?>>.IsReadOnly => false;

    public ICollection<string> Keys
    {
        get
        {
            EnsureCapacity(_count);
            KeyValuePair<string, object>[] arrayStorage = _arrayStorage;
            string[] array = new string[_count];
            for (int i = 0; i < array.Length; i++)
            {
                array[i] = arrayStorage[i].Key;
            }
            return array;
        }
    }

    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => Keys;

    public ICollection<object?> Values
    {
        get
        {
            EnsureCapacity(_count);
            KeyValuePair<string, object>[] arrayStorage = _arrayStorage;
            object[] array = new object[_count];
            for (int i = 0; i < array.Length; i++)
            {
                array[i] = arrayStorage[i].Value;
            }
            return array;
        }
    }

    IEnumerable<object?> IReadOnlyDictionary<string, object>.Values => Values;

    public RouteValueDictionary()
    {
        _arrayStorage = Array.Empty<KeyValuePair<string, object>>();
    }

    public RouteValueDictionary(IEnumerable<KeyValuePair<string, object?>>? values)
    {
        if (values != null)
        {
            Initialize(values);
        }
        else
        {
            _arrayStorage = Array.Empty<KeyValuePair<string, object>>();
        }
    }

    [MemberNotNull("_arrayStorage")]
    private void Initialize(IEnumerable<KeyValuePair<string, object>> keyValueEnumerable)
    {
        _arrayStorage = Array.Empty<KeyValuePair<string, object>>();
        foreach (KeyValuePair<string, object> item in keyValueEnumerable)
        {
            Add(item.Key, item.Value);
        }
    }

    void ICollection<KeyValuePair<string, object>>.Add(KeyValuePair<string, object> item)
    {
        Add(item.Key, item.Value);
    }

    public void Add(string key, object? value)
    {
        if (key == null)
        {
            ThrowArgumentNullExceptionForKey();
        }
        EnsureCapacity(_count + 1);
        if (ContainsKeyArray(key))
        {
            throw new ArgumentException($"An element with the key '{key}' already exists in the {"RouteValueDictionary"}.");
        }
        _arrayStorage[_count] = new KeyValuePair<string, object>(key, value);
        _count++;
    }

    public void Clear()
    {
        if (_count != 0)
        {
            Array.Clear(_arrayStorage, 0, _count);
            _count = 0;
        }
    }

    bool ICollection<KeyValuePair<string, object>>.Contains(KeyValuePair<string, object> item)
    {
        if (TryGetValue(item.Key, out object value))
        {
            return EqualityComparer<object>.Default.Equals(value, item.Value);
        }
        return false;
    }

    public bool ContainsKey(string key)
    {
        if (key == null)
        {
            ThrowArgumentNullExceptionForKey();
        }
        return ContainsKeyCore(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ContainsKeyCore(string key)
    {
        return ContainsKeyArray(key);
    }

    void ICollection<KeyValuePair<string, object>>.CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array, "array");
        if (arrayIndex < 0 || arrayIndex > array.Length || array.Length - arrayIndex < Count)
        {
            throw new ArgumentOutOfRangeException("arrayIndex");
        }
        if (Count != 0)
        {
            EnsureCapacity(Count);
            Array.Copy(_arrayStorage, 0, array, arrayIndex, _count);
        }
    }

    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
    }

    IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    bool ICollection<KeyValuePair<string, object>>.Remove(KeyValuePair<string, object> item)
    {
        if (Count == 0)
        {
            return false;
        }
        EnsureCapacity(Count);
        int num = FindIndex(item.Key);
        KeyValuePair<string, object>[] arrayStorage = _arrayStorage;
        if (num >= 0 && EqualityComparer<object>.Default.Equals(arrayStorage[num].Value, item.Value))
        {
            Array.Copy(arrayStorage, num + 1, arrayStorage, num, _count - num);
            _count--;
            arrayStorage[_count] = default(KeyValuePair<string, object>);
            return true;
        }
        return false;
    }

    public bool Remove(string key)
    {
        if (key == null)
        {
            ThrowArgumentNullExceptionForKey();
        }
        if (Count == 0)
        {
            return false;
        }
        EnsureCapacity(_count);
        int num = FindIndex(key);
        if (num >= 0)
        {
            _count--;
            KeyValuePair<string, object>[] arrayStorage = _arrayStorage;
            Array.Copy(arrayStorage, num + 1, arrayStorage, num, _count - num);
            arrayStorage[_count] = default(KeyValuePair<string, object>);
            return true;
        }
        return false;
    }

    public bool Remove(string key, out object? value)
    {
        if (key == null)
        {
            ThrowArgumentNullExceptionForKey();
        }
        if (_count == 0)
        {
            value = null;
            return false;
        }
        EnsureCapacity(_count);
        int num = FindIndex(key);
        if (num >= 0)
        {
            _count--;
            KeyValuePair<string, object>[] arrayStorage = _arrayStorage;
            value = arrayStorage[num].Value;
            Array.Copy(arrayStorage, num + 1, arrayStorage, num, _count - num);
            arrayStorage[_count] = default(KeyValuePair<string, object>);
            return true;
        }
        value = null;
        return false;
    }

    public bool TryAdd(string key, object? value)
    {
        if (key == null)
        {
            ThrowArgumentNullExceptionForKey();
        }
        if (ContainsKeyCore(key))
        {
            return false;
        }
        EnsureCapacity(Count + 1);
        _arrayStorage[Count] = new KeyValuePair<string, object>(key, value);
        _count++;
        return true;
    }

    public bool TryGetValue(string key, out object? value)
    {
        if (key == null)
        {
            ThrowArgumentNullExceptionForKey();
        }
        return TryFindItem(key, out value);
    }

    [DoesNotReturn]
    private static void ThrowArgumentNullExceptionForKey()
    {
        throw new ArgumentNullException("key");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int capacity)
    {
        if (_arrayStorage.Length < capacity)
        {
            EnsureCapacitySlow(capacity);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The constructor that would result in _propertyStorage being non-null is annotated with RequiresUnreferencedCodeAttribute. We do not need to additionally produce an error in this method since it is shared by trimmer friendly code paths.")]
    private void EnsureCapacitySlow(int capacity)
    {
        if (_arrayStorage.Length < capacity)
        {
            capacity = ((_arrayStorage.Length == 0) ? 4 : (_arrayStorage.Length * 2));
            KeyValuePair<string, object>[] array = new KeyValuePair<string, object>[capacity];
            if (_count > 0)
            {
                Array.Copy(_arrayStorage, 0, array, 0, _count);
            }
            _arrayStorage = array;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindIndex(string key)
    {
        KeyValuePair<string, object>[] arrayStorage = _arrayStorage;
        int count = _count;
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(arrayStorage[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryFindItem(string key, out object value)
    {
        KeyValuePair<string, object>[] arrayStorage = _arrayStorage;
        int count = _count;
        if ((uint)count <= (uint)arrayStorage.Length)
        {
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(arrayStorage[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = arrayStorage[i].Value;
                    return true;
                }
            }
        }
        value = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ContainsKeyArray(string key)
    {
        KeyValuePair<string, object>[] arrayStorage = _arrayStorage;
        int count = _count;
        if ((uint)count <= (uint)arrayStorage.Length)
        {
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(arrayStorage[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }


    internal sealed class DictionaryDebugView<TKey, TValue> where TKey : notnull
    {
        private readonly IDictionary<TKey, TValue> _dict;

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public DictionaryItemDebugView<TKey, TValue>[] Items
        {
            get
            {
                KeyValuePair<TKey, TValue>[] array = new KeyValuePair<TKey, TValue>[_dict.Count];
                _dict.CopyTo(array, 0);
                DictionaryItemDebugView<TKey, TValue>[] array2 = new DictionaryItemDebugView<TKey, TValue>[array.Length];
                for (int i = 0; i < array2.Length; i++)
                {
                    array2[i] = new DictionaryItemDebugView<TKey, TValue>(array[i]);
                }
                return array2;
            }
        }

        public DictionaryDebugView(IDictionary<TKey, TValue> dictionary)
        {
            _dict = dictionary;
        }
    }

    [DebuggerDisplay("{Value}", Name = "[{Key}]")]
    internal readonly struct DictionaryItemDebugView<TKey, TValue>
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
        public TKey Key { get; }

        [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
        public TValue Value { get; }

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
    }
}
