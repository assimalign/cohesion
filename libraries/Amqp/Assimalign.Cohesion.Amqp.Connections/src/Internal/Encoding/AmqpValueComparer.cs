using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Compares the keys of a decoded AMQP map: binary keys, which decode as arrays, by their content, and
/// every other key by its own equality.
/// </summary>
internal sealed class AmqpValueComparer : IEqualityComparer<object>
{
    private AmqpValueComparer()
    {
    }

    public static AmqpValueComparer Instance { get; } = new();

    public new bool Equals(object? x, object? y)
    {
        if (x is byte[] left && y is byte[] right)
        {
            return left.AsSpan().SequenceEqual(right);
        }

        return object.Equals(x, y);
    }

    public int GetHashCode(object obj)
    {
        if (obj is byte[] bytes)
        {
            HashCode hash = new();

            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        return obj.GetHashCode();
    }
}
