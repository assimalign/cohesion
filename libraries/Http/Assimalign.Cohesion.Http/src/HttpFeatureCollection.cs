using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpFeatureCollection"/> implementation backed by a
/// name-keyed <see cref="Dictionary{TKey, TValue}"/>. Features are identified
/// by their <see cref="IHttpFeature.Name"/>; replacing a feature uses the same
/// name as the slot.
/// </summary>
/// <remarks>
/// <para>
/// Cheap to construct (one lazily-allocated dictionary), no reflection at
/// lookup or mutation time, AOT-safe. An optional <c>defaults</c> source can be
/// supplied; features installed locally shadow same-named defaults, and the
/// enumerator yields local features first then non-shadowed defaults.
/// </para>
/// <para>
/// The collection's <see cref="Version"/> increments on every local mutation
/// and adds the defaults' <see cref="IHttpFeatureCollection.Version"/> so
/// consumers can cheaply detect any change in the effective feature set.
/// </para>
/// </remarks>
public class HttpFeatureCollection : IHttpFeatureCollection
{
    private readonly IHttpFeatureCollection? _defaults;
    private readonly int _initialCapacity;
    private Dictionary<string, IHttpFeature>? _features;
    private int _version;

    /// <summary>
    /// Initializes an empty feature collection.
    /// </summary>
    public HttpFeatureCollection()
    {
    }

    /// <summary>
    /// Initializes an empty feature collection with the supplied initial
    /// capacity hint for the backing dictionary.
    /// </summary>
    /// <param name="initialCapacity">A non-negative capacity hint.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is negative.</exception>
    public HttpFeatureCollection(int initialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _initialCapacity = initialCapacity;
    }

    /// <summary>
    /// Initializes a feature collection that falls back to <paramref name="defaults"/>
    /// for any name that is not installed locally.
    /// </summary>
    /// <param name="defaults">A read-through fallback feature source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    public HttpFeatureCollection(IHttpFeatureCollection defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _defaults = defaults;
    }

    /// <inheritdoc />
    public virtual int Version => _version + (_defaults?.Version ?? 0);

    /// <inheritdoc />
    public IHttpFeature? Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_features is not null && _features.TryGetValue(name, out IHttpFeature? local))
        {
            return local;
        }

        return _defaults?.Get(name);
    }

    /// <inheritdoc />
    public void Set(IHttpFeature? feature)
    {
        // Set(null) is a no-op: the contract carries no name when feature is null,
        // so there is no slot to remove. Callers that want explicit removal call
        // Remove(name) directly.
        if (feature is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrEmpty(feature.Name);

        _features ??= new Dictionary<string, IHttpFeature>(_initialCapacity, StringComparer.Ordinal);
        _features[feature.Name] = feature;
        _version++;
    }

    /// <summary>
    /// Removes the feature registered under <paramref name="name"/>. Returns
    /// <see langword="true"/> when a local registration was removed; returns
    /// <see langword="false"/> when no local registration existed (a same-named
    /// feature in the defaults source is not touched and will still be visible
    /// through <see cref="Get"/>).
    /// </summary>
    /// <param name="name">The feature name to remove.</param>
    /// <returns><see langword="true"/> if a local feature was removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_features is null || !_features.Remove(name))
        {
            return false;
        }

        _version++;
        return true;
    }

    /// <inheritdoc />
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

        foreach (IHttpFeature fallback in _defaults)
        {
            if (_features is null || !_features.ContainsKey(fallback.Name))
            {
                yield return fallback;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
