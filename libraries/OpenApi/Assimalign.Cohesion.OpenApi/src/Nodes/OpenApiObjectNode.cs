using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// An insertion-ordered map of string keys to <see cref="OpenApiNode"/> values in the OpenAPI value tree.
/// </summary>
/// <remarks>
/// Insertion order is preserved so that serialized output is deterministic and readable. Key order is
/// not semantically significant in OpenAPI, but preserving it keeps generated documents stable across runs.
/// </remarks>
public sealed class OpenApiObjectNode : OpenApiNode, IEnumerable<KeyValuePair<string, OpenApiNode>>
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, OpenApiNode> _map = new(StringComparer.Ordinal);

    /// <summary>Gets the number of members in the object.</summary>
    public int Count => _order.Count;

    /// <summary>Gets the member keys in insertion order.</summary>
    public IReadOnlyList<string> Keys => _order;

    /// <summary>Gets or sets the value for the specified key, appending the key when it is new.</summary>
    /// <param name="key">The member name.</param>
    /// <returns>The value associated with <paramref name="key"/>.</returns>
    public OpenApiNode this[string key]
    {
        get => _map[key];
        set
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(value);
            if (!_map.ContainsKey(key))
            {
                _order.Add(key);
            }

            _map[key] = value;
        }
    }

    /// <summary>Adds a member to the object.</summary>
    /// <param name="key">The member name.</param>
    /// <param name="value">The member value.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is already present.</exception>
    public void Add(string key, OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (_map.ContainsKey(key))
        {
            throw new ArgumentException($"An element with the key '{key}' already exists.", nameof(key));
        }

        _order.Add(key);
        _map[key] = value;
    }

    /// <summary>Determines whether the object contains the specified key.</summary>
    /// <param name="key">The member name.</param>
    /// <returns><see langword="true"/> when present; otherwise <see langword="false"/>.</returns>
    public bool ContainsKey(string key) => _map.ContainsKey(key);

    /// <summary>Attempts to get the value for the specified key.</summary>
    /// <param name="key">The member name.</param>
    /// <param name="value">When this method returns, the value when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when found; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out OpenApiNode value) => _map.TryGetValue(key, out value);

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, OpenApiNode>> GetEnumerator()
    {
        foreach (var key in _order)
        {
            yield return new KeyValuePair<string, OpenApiNode>(key, _map[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
