using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Compares the keys of a decoded AMQP map: binary keys, which decode as arrays, by their content, and
/// every other key by its own equality.
/// </summary>
/// <remarks>
/// A binary is a <see cref="byte"/>[] of exactly that type: the runtime lets an <see cref="sbyte"/>[], an array
/// of byte, pass a type test for one, and an array is a key of another type than a binary of the same octets.
/// </remarks>
internal sealed class AmqpValueComparer : IEqualityComparer<object>
{
    private AmqpValueComparer()
    {
    }

    public static AmqpValueComparer Instance { get; } = new();

    public new bool Equals(object? x, object? y)
    {
        if (IsBinary(x, out byte[]? left) && IsBinary(y, out byte[]? right))
        {
            return left.AsSpan().SequenceEqual(right);
        }

        return object.Equals(x, y);
    }

    public int GetHashCode(object obj)
    {
        if (IsBinary(obj, out byte[]? bytes))
        {
            HashCode hash = new();

            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        return obj.GetHashCode();
    }

    private static bool IsBinary(object? value, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = value as byte[];
        return bytes is not null && bytes.GetType() == typeof(byte[]);
    }
}
