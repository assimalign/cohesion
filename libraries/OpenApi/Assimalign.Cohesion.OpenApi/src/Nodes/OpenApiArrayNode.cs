using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// An ordered sequence of <see cref="OpenApiNode"/> values in the OpenAPI value tree.
/// </summary>
public sealed class OpenApiArrayNode : OpenApiNode, IEnumerable<OpenApiNode>
{
    private readonly List<OpenApiNode> _items = new();

    /// <summary>Gets the number of items in the array.</summary>
    public int Count => _items.Count;

    /// <summary>Gets or sets the item at the specified index.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The node at <paramref name="index"/>.</returns>
    public OpenApiNode this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _items[index] = value;
        }
    }

    /// <summary>Appends a node to the array.</summary>
    /// <param name="item">The node to append.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="item"/> is <see langword="null"/>.</exception>
    public void Add(OpenApiNode item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    /// <inheritdoc/>
    public IEnumerator<OpenApiNode> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
