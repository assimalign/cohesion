using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>A value projection row with ordinal and column-name access.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphRow Count={Count}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Client.GraphRow.DebugView))]
public sealed class GraphRow : IReadOnlyList<object?>
{
    private readonly object?[] _values;
    private readonly IReadOnlyDictionary<string, int> _ordinals;

    internal GraphRow(object?[] values, IReadOnlyDictionary<string, int> ordinals)
    {
        _values = values;
        _ordinals = ordinals;
    }

    /// <summary>Gets the number of values.</summary>
    public int Count => _values.Length;

    /// <summary>Gets a value by ordinal.</summary>
    /// <param name="index">The zero-based column ordinal.</param>
    /// <returns>The scalar, object array, read-only ordinal map, or null.</returns>
    /// <exception cref="IndexOutOfRangeException">The ordinal is outside the row.</exception>
    public object? this[int index] => _values[index];

    /// <summary>Gets a value by column name; the first duplicate name wins.</summary>
    /// <param name="name">The exact column name.</param>
    /// <returns>The scalar, object array, read-only ordinal map, or null.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="KeyNotFoundException">No column has that name.</exception>
    public object? this[string name] => _values[_ordinals[name]];

    /// <inheritdoc />
    public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Client.GraphRow _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Client.GraphRow value)
        {
            _target = value;
        }

        public int Count => _target.Count;
    }
}

