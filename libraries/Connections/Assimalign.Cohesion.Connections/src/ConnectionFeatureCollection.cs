using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Stores connection features by name, exposing wrapped features unless locally replaced or hidden.
/// </summary>
/// <remarks>
/// Mutations publish immutable snapshots so readers and enumerators remain safe during changes.
/// Enumeration visits local features first, followed by visible wrapped features. A traversal is not
/// an atomic snapshot of independently changing wrapped collections.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("ConnectionFeatureCollection")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public class ConnectionFeatureCollection : IConnectionFeatureCollection
{
    private readonly IConnectionFeatureCollection? _inner;
    private readonly object _sync = new();
    private KeyValuePair<string, IConnectionFeature?>[] _features = [];

    /// <summary>
    /// Initializes an empty collection with an optional wrapped collection.
    /// </summary>
    /// <param name="inner">The wrapped connection's features, or <see langword="null"/>.</param>
    public ConnectionFeatureCollection(IConnectionFeatureCollection? inner = null)
    {
        _inner = inner;
    }

    /// <inheritdoc />
    public IConnectionFeature? Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        KeyValuePair<string, IConnectionFeature?>[] snapshot = Volatile.Read(ref _features);
        int index = Find(snapshot, name);
        return index >= 0 ? snapshot[index].Value : _inner is null ? null : GetInnerFeature(_inner, name);
    }

    /// <inheritdoc />
    public void Set<TFeature>(TFeature feature) where TFeature : class, IConnectionFeature
    {
        ArgumentNullException.ThrowIfNull(feature);
        string name = feature.Name;
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (_sync)
        {
            Publish(name, feature);
        }
    }

    /// <inheritdoc />
    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (_sync)
        {
            int index = Find(_features, name);
            bool removed = index >= 0 ? _features[index].Value is not null : _inner?.Get(name) is not null;
            if (index < 0 || _features[index].Value is not null)
            {
                // The tombstone remains even if the wrapped collection adds this name later.
                Publish(name, null);
            }

            return removed;
        }
    }

    internal TFeature? GetFeature<TFeature>() where TFeature : class, IConnectionFeature
    {
        ConnectionFeatureCollection level = this;
        while (true)
        {
            foreach (KeyValuePair<string, IConnectionFeature?> entry in Volatile.Read(ref level._features))
            {
                if (entry.Value is TFeature match && !IsShadowedAbove(level, entry.Key))
                {
                    return match;
                }
            }

            IConnectionFeatureCollection? inner = level._inner;
            if (inner is null)
            {
                return null;
            }

            // A subclass can reimplement enumeration, so preserve its effective set and ordering.
            if (inner.GetType() != typeof(ConnectionFeatureCollection))
            {
                foreach (IConnectionFeature feature in inner)
                {
                    if (feature is TFeature match && !IsShadowedAbove(inner, feature.Name))
                    {
                        return match;
                    }
                }

                return null;
            }

            level = (ConnectionFeatureCollection)inner;
        }
    }

    /// <inheritdoc />
    public IEnumerator<IConnectionFeature> GetEnumerator()
    {
        KeyValuePair<string, IConnectionFeature?>[] snapshot = Volatile.Read(ref _features);
        foreach (KeyValuePair<string, IConnectionFeature?> entry in snapshot)
        {
            if (entry.Value is not null)
            {
                yield return entry.Value;
            }
        }

        if (_inner is not null)
        {
            foreach (IConnectionFeature feature in _inner)
            {
                if (Find(snapshot, feature.Name) < 0)
                {
                    yield return feature;
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static IConnectionFeature? GetInnerFeature(IConnectionFeatureCollection inner, string name)
    {
        while (true)
        {
            // Subclasses may reimplement named lookup; only bypass dispatch for the exact built-in type.
            if (inner.GetType() != typeof(ConnectionFeatureCollection))
            {
                return inner.Get(name);
            }

            ConnectionFeatureCollection level = (ConnectionFeatureCollection)inner;
            KeyValuePair<string, IConnectionFeature?>[] snapshot = Volatile.Read(ref level._features);
            int index = Find(snapshot, name);
            if (index >= 0)
            {
                return snapshot[index].Value;
            }

            IConnectionFeatureCollection? next = level._inner;
            if (next is null)
            {
                return null;
            }

            inner = next;
        }
    }

    private void Publish(string name, IConnectionFeature? feature)
    {
        int index = Find(_features, name);
        var updated = new KeyValuePair<string, IConnectionFeature?>[_features.Length + (index < 0 ? 1 : 0)];
        _features.CopyTo(updated, 0);
        updated[index < 0 ? _features.Length : index] = new(name, feature);
        Volatile.Write(ref _features, updated);
    }

    private bool IsShadowedAbove(IConnectionFeatureCollection level, string name)
    {
        IConnectionFeatureCollection? current = this;
        while (!ReferenceEquals(current, level) && current is ConnectionFeatureCollection collection)
        {
            if (Find(Volatile.Read(ref collection._features), name) >= 0)
            {
                return true;
            }

            current = collection._inner;
        }

        return false;
    }

    private static int Find(KeyValuePair<string, IConnectionFeature?>[] snapshot, string name)
    {
        for (int index = 0; index < snapshot.Length; index++)
        {
            if (string.Equals(snapshot[index].Key, name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class DebugView
    {
        public DebugView(ConnectionFeatureCollection value)
        {
        }
    }
}
