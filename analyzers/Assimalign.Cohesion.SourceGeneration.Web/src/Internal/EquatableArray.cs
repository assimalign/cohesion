using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Assimalign.Cohesion.SourceGeneration.Web;

/// <summary>
/// A value-equatable wrapper over <see cref="ImmutableArray{T}"/> so generator pipeline models compare
/// by element value, which is required for the incremental generator's caching to recognize unchanged
/// inputs.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _array;

    internal EquatableArray(ImmutableArray<T> array)
    {
        _array = array;
    }

    internal static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    internal int Count => _array.IsDefault ? 0 : _array.Length;

    internal T this[int index] => _array[index];

    public bool Equals(EquatableArray<T> other)
    {
        if (_array.IsDefault || other._array.IsDefault)
        {
            return _array.IsDefault && other._array.IsDefault;
        }

        if (_array.Length != other._array.Length)
        {
            return false;
        }

        for (var index = 0; index < _array.Length; index++)
        {
            if (!_array[index].Equals(other._array[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_array.IsDefault)
        {
            return 0;
        }

        var hash = 17;
        foreach (var item in _array)
        {
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_array.IsDefault ? ImmutableArray<T>.Empty : _array)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
