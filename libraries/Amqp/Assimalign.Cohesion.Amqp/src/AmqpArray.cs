using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.Amqp.Internal;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents an AMQP 1.0 array (Part 1, section 1.6.24) that no CLR array type holds: an array of ubyte,
/// null, lists, maps or arrays, or one whose elements share a descriptor.
/// </summary>
/// <remarks>
/// <para>
/// An AMQP array is a sequence of values of one type, written with one constructor that every element
/// shares. The codec decodes an array to the typed CLR array of its element type where there is one, such as
/// an <see cref="int"/>[] or a <see cref="string"/>[], and to an <see cref="AmqpArray"/> where there is none:
/// a <see cref="byte"/>[] is binary, so an array of ubyte has no CLR array type of its own, and neither has an
/// array of null, lists, maps or arrays, nor an array of described values, whose descriptor the array carries
/// once for all its elements. Encoding an <see cref="AmqpArray"/> writes it back as the same array, so a value
/// relayed through decode and encode keeps its type on the wire.
/// </para>
/// <para>
/// Any element type may be written this way, an array of <see cref="AmqpPrimitiveType.Int"/> included; it
/// decodes again as an <see cref="int"/>[] unless it is described. The array holds its elements as given, and
/// the encoder checks each one against <see cref="ElementType"/>, refusing the array before writing any of it
/// when an element is not a value of that type (<see cref="AmqpPrimitiveType"/> names the CLR types each type
/// is written from). An array of <see cref="AmqpPrimitiveType.Null"/> holds no elements.
/// </para>
/// </remarks>
public sealed class AmqpArray
{
    /// <summary>
    /// Initializes a new array.
    /// </summary>
    /// <param name="elementType">The primitive type of every element.</param>
    /// <param name="elements">The elements, which the array copies.</param>
    /// <param name="descriptor">
    /// The descriptor every element shares, which makes each element a described value: an
    /// <see cref="AmqpSymbol"/> name or a <see cref="ulong"/> code, a <see cref="string"/> being converted to an
    /// <see cref="AmqpSymbol"/>; or <see langword="null"/> when the elements are not described.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="elementType"/> is not a defined type.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="elements"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="descriptor"/> is neither a symbol nor an unsigned long, is a default symbol, which
    /// holds no name, or is a string that is not seven-bit ASCII.
    /// </exception>
    public AmqpArray(AmqpPrimitiveType elementType, IEnumerable<object?> elements, object? descriptor = null)
    {
        if (!Enum.IsDefined(elementType))
        {
            throw new ArgumentOutOfRangeException(nameof(elementType));
        }

        ArgumentNullException.ThrowIfNull(elements);

        object?[] copy = [.. elements];

        ElementType = elementType;
        Descriptor = descriptor is null ? null : AmqpDescriptors.Normalize(descriptor, nameof(descriptor));
        Elements = new ReadOnlyCollection<object?>(copy);
    }

    /// <summary>
    /// Gets the primitive type of every element.
    /// </summary>
    public AmqpPrimitiveType ElementType { get; }

    /// <summary>
    /// Gets the descriptor every element shares: an <see cref="AmqpSymbol"/> or a <see cref="ulong"/>, or
    /// <see langword="null"/> when the elements are not described.
    /// </summary>
    public object? Descriptor { get; }

    /// <summary>
    /// Gets the elements. Each one is a value of <see cref="ElementType"/>; a described element is its value
    /// alone, without the <see cref="Descriptor"/> it shares with the others.
    /// </summary>
    public IReadOnlyList<object?> Elements { get; }
}
