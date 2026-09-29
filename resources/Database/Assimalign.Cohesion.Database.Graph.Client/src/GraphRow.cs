using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>A scalar projection row with ordinal and column-name access.</summary>
public sealed class GraphRow : IReadOnlyList<object?>
{
    private readonly object?[] _values;
    private readonly IReadOnlyDictionary<string, int> _ordinals;

    internal GraphRow(object?[] values, IReadOnlyDictionary<string, int> ordinals)
    {
        _values = values;
        _ordinals = ordinals;
    }

    /// <summary>Gets the number of scalar values.</summary>
    public int Count => _values.Length;

    /// <summary>Gets a scalar value by ordinal.</summary>
    /// <param name="index">The zero-based column ordinal.</param>
    /// <returns>The boxed scalar or null.</returns>
    /// <exception cref="IndexOutOfRangeException">The ordinal is outside the row.</exception>
    public object? this[int index] => _values[index];

    /// <summary>Gets a scalar value by column name; the first duplicate name wins.</summary>
    /// <param name="name">The exact column name.</param>
    /// <returns>The boxed scalar or null.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="KeyNotFoundException">No column has that name.</exception>
    public object? this[string name] => _values[_ordinals[name]];

    /// <inheritdoc />
    public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

