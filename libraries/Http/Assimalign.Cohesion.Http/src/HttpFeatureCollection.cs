using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpFeatureCollection"/> backed by a
/// <see cref="Dictionary{TKey, TValue}"/> keyed on the registered feature
/// interface <see cref="Type"/>.
/// </summary>
/// <remarks>
/// Cheap to construct (one empty dictionary), no reflection at lookup
/// time, AOT-safe — feature lookups go through <c>typeof(TFeature)</c>
/// which is a JIT-time constant.
/// </remarks>
public class HttpFeatureCollection : IHttpFeatureCollection
{
    private sealed class TypeKeyAlternativeLookup : IAlternateEqualityComparer<string, Type>
    {
        public TypeKeyAlternativeLookup()
        {
            
        }


        public Type Create(string alternate)
        {
            throw new NotImplementedException();
        }
        public bool Equals(string alternate, Type other)
        {
            throw new NotImplementedException();
        }

        public int GetHashCode(string alternate)
        {
            throw new NotImplementedException();
        }
    }


    private sealed class TypeKeyComparer : IEqualityComparer<KeyValuePair<Type, object>>
    {
        public bool Equals(KeyValuePair<Type, object> x, KeyValuePair<Type, object> y)
        {
            return x.Key.Equals(y.Key);
        }

        public int GetHashCode(KeyValuePair<Type, object> obj)
        {
            return obj.Key.GetHashCode();
        }
    }

    //private sealed class FeatureCollectionDebugView
    //{
    //    private readonly HttpFeatureCollection _features;

    //    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    //    public DictionaryItemDebugView<Type, object>[] Items => _features.Select<KeyValuePair<Type, object>, DictionaryItemDebugView<Type, object>>((KeyValuePair<Type, object> pair) => new DictionaryItemDebugView<Type, object>(pair)).ToArray();

    //    public FeatureCollectionDebugView(HttpFeatureCollection features)
    //    {
    //        _features = features;
            
    //    }
    //}

    private static readonly TypeKeyComparer FeatureKeyComparer = new TypeKeyComparer();

    private readonly IHttpFeatureCollection? _defaults;
    private readonly int _initialCapacity;
    private Dictionary<string, object>? _features;
    private Dictionary<string, object>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private volatile int _version;


    public HttpFeatureCollection() { }
    public HttpFeatureCollection(int initialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIf(initialCapacity < 0, "Initial capacity must be a non-negative integer.");
        _initialCapacity = initialCapacity;
    }
    public HttpFeatureCollection(IHttpFeatureCollection defaults)
    {
        _defaults = defaults;
    }



    public virtual int Version
    {
        get
        {
            return _version + (_defaults?.Version ?? 0);
        }
    }

    //public object? this[Type key]
    //{
    //    get
    //    {
    //        ArgumentNullException.ThrowIfNull(key, "key");
    //        if (_features is null || !_features.TryGetValue(key, out var value))
    //        {
    //            return _lookup?[key.Name];
    //        }
    //        return value;
    //    }
    //    set
    //    {
    //        ArgumentNullException.ThrowIfNull(key, "key");
    //        if (value == null)
    //        {
    //            if (_features is not null && _features.Remove(key))
    //            {
    //                _version++;
    //            }
    //            return;
    //        }
    //        if (_features is null)
    //        {
    //            _features = new Dictionary<string, object>(_initialCapacity);
    //            _lookup = _features.GetAlternateLookup<ReadOnlySpan<char>>();
    //        }
    //        _features[key.Name] = value;
    //        _version++;
    //    }
    //}


    public IHttpFeature? Get(string name)
    {
        throw new NotImplementedException();
    }

    public void Set(IHttpFeature? feature)
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public IEnumerator<IHttpFeature> GetEnumerator()
    {
        if (_features is not null)
        {
            foreach (IHttpFeature feature in _features.Values)
            {
                yield return feature;
            }
        }
        if (_defaults is null)
        {
            yield break;
        }
        IEnumerable<IHttpFeature> enumerable;
        if (_features is not null)
        {
            enumerable = _defaults.Except<IHttpFeature>(_features.Values, EqualityComparer<object>.Default);
        }
        else
        {
            IEnumerable<IHttpFeature> defaults = _defaults;
            enumerable = defaults;
        }

        foreach (IHttpFeature item in enumerable)
        {
            yield return item;
        }
    }

    //public IEnumerator<KeyValuePair<Type, object>> GetEnumerator()
    //{
    //    if (_features != null)
    //    {
    //        foreach (KeyValuePair<Type, object> feature in _features)
    //        {
    //            yield return feature;
    //        }
    //    }
    //    if (_defaults == null)
    //    {
    //        yield break;
    //    }
    //    IEnumerable<KeyValuePair<Type, object>> enumerable;
    //    if (_features != null)
    //    {
    //        enumerable = _defaults.Except<KeyValuePair<Type, object>>(_features, FeatureKeyComparer);
    //    }
    //    else
    //    {
    //        IEnumerable<KeyValuePair<Type, object>> defaults = _defaults;
    //        enumerable = defaults;
    //    }
    //    foreach (KeyValuePair<Type, object> item in enumerable)
    //    {
    //        yield return item;
    //    }
    //}

    //public TFeature? Get<TFeature>()
    //{
    //    if (typeof(TFeature).IsValueType)
    //    {
    //        object? obj = this[typeof(TFeature)];
    //        if (obj == null && Nullable.GetUnderlyingType(typeof(TFeature)) == null)
    //        {
    //            throw new InvalidOperationException($"{typeof(TFeature).FullName} does not exist in the feature collection and because it is a struct the method can't return null. Use 'featureCollection[typeof({typeof(TFeature).FullName})] is not null' to check if the feature exists.");
    //        }
    //        return (TFeature?)obj;
    //    }
    //    return (TFeature?)this[typeof(TFeature)];
    //}
}