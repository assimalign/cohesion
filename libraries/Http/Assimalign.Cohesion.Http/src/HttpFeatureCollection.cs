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
    private sealed class KeyComparer : IEqualityComparer<KeyValuePair<Type, object>>
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

    private static readonly KeyComparer FeatureKeyComparer = new KeyComparer();

    private readonly IHttpFeatureCollection? _defaults;
    private readonly int _initialCapacity;
    private IDictionary<Type, object>? _features;
    private volatile int _version;

    

    public HttpFeatureCollection()
    {
        
    }

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

    public bool IsReadOnly => false;

    public object? this[Type key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key, "key");
            if (_features is null || !_features.TryGetValue(key, out var value))
            {
                return _defaults?[key];
            }
            return value;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(key, "key");
            if (value == null)
            {
                if (_features is not null && _features.Remove(key))
                {
                    _version++;
                }
                return;
            }
            if (_features is null)
            {
                _features = new Dictionary<Type, object>(_initialCapacity);
            }
            _features[key] = value;
            _version++;
        }
    }


    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public IEnumerator<KeyValuePair<Type, object>> GetEnumerator()
    {
        if (_features != null)
        {
            foreach (KeyValuePair<Type, object> feature in _features)
            {
                yield return feature;
            }
        }
        if (_defaults == null)
        {
            yield break;
        }
        IEnumerable<KeyValuePair<Type, object>> enumerable;
        if (_features != null)
        {
            enumerable = _defaults.Except<KeyValuePair<Type, object>>(_features, FeatureKeyComparer);
        }
        else
        {
            IEnumerable<KeyValuePair<Type, object>> defaults = _defaults;
            enumerable = defaults;
        }
        foreach (KeyValuePair<Type, object> item in enumerable)
        {
            yield return item;
        }
    }

    public TFeature? Get<TFeature>()
    {
        if (typeof(TFeature).IsValueType)
        {
            object? obj = this[typeof(TFeature)];
            if (obj == null && Nullable.GetUnderlyingType(typeof(TFeature)) == null)
            {
                throw new InvalidOperationException($"{typeof(TFeature).FullName} does not exist in the feature collection and because it is a struct the method can't return null. Use 'featureCollection[typeof({typeof(TFeature).FullName})] is not null' to check if the feature exists.");
            }
            return (TFeature?)obj;
        }
        return (TFeature?)this[typeof(TFeature)];
    }

    public void Set<TFeature>(TFeature? instance)
    {
        this[typeof(TFeature)] = instance;
    }
}