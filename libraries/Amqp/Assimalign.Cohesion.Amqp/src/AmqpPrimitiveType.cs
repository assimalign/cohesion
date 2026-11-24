namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Names the primitive types of AMQP 1.0 (Part 1, section 1.6), such as the element type of an
/// <see cref="AmqpArray"/>.
/// </summary>
/// <remarks>
/// Each value is the number of the type's definition in Part 1, section 1.6: <see cref="Null"/> is defined in
/// section 1.6.1 and <see cref="Array"/> in section 1.6.24. No value is zero, so a default value names no
/// type. Each member names the CLR types the codec reads the type as and writes it from.
/// </remarks>
public enum AmqpPrimitiveType
{
    /// <summary>
    /// The null type (section 1.6.1), whose one value is <see langword="null"/>. An array of null holds no
    /// elements: its element constructor has no width, so a reader could not count them.
    /// </summary>
    Null = 1,

    /// <summary>
    /// A boolean (section 1.6.2): a <see cref="bool"/>.
    /// </summary>
    Boolean = 2,

    /// <summary>
    /// An unsigned 8-bit integer (section 1.6.3): a <see cref="byte"/>. A <see cref="byte"/>[] is binary, so
    /// an array of ubyte is an <see cref="AmqpArray"/>.
    /// </summary>
    UByte = 3,

    /// <summary>
    /// An unsigned 16-bit integer (section 1.6.4): a <see cref="ushort"/>.
    /// </summary>
    UShort = 4,

    /// <summary>
    /// An unsigned 32-bit integer (section 1.6.5): a <see cref="uint"/>.
    /// </summary>
    UInt = 5,

    /// <summary>
    /// An unsigned 64-bit integer (section 1.6.6): a <see cref="ulong"/>.
    /// </summary>
    ULong = 6,

    /// <summary>
    /// A signed 8-bit integer (section 1.6.7): an <see cref="sbyte"/>.
    /// </summary>
    Byte = 7,

    /// <summary>
    /// A signed 16-bit integer (section 1.6.8): a <see cref="short"/>.
    /// </summary>
    Short = 8,

    /// <summary>
    /// A signed 32-bit integer (section 1.6.9): an <see cref="int"/>.
    /// </summary>
    Int = 9,

    /// <summary>
    /// A signed 64-bit integer (section 1.6.10): a <see cref="long"/>.
    /// </summary>
    Long = 10,

    /// <summary>
    /// A 32-bit IEEE 754 floating point number (section 1.6.11): a <see cref="float"/>.
    /// </summary>
    Float = 11,

    /// <summary>
    /// A 64-bit IEEE 754 floating point number (section 1.6.12): a <see cref="double"/>.
    /// </summary>
    Double = 12,

    /// <summary>
    /// A 32-bit decimal number (section 1.6.13): an <see cref="AmqpDecimal32"/>.
    /// </summary>
    Decimal32 = 13,

    /// <summary>
    /// A 64-bit decimal number (section 1.6.14): an <see cref="AmqpDecimal64"/>.
    /// </summary>
    Decimal64 = 14,

    /// <summary>
    /// A 128-bit decimal number (section 1.6.15): an <see cref="AmqpDecimal128"/>.
    /// </summary>
    Decimal128 = 15,

    /// <summary>
    /// A Unicode scalar value (section 1.6.16): a <see cref="System.Text.Rune"/>, or written from a
    /// <see cref="char"/> that is not half of a surrogate pair.
    /// </summary>
    Char = 16,

    /// <summary>
    /// A point in time, in milliseconds since the Unix epoch (section 1.6.17): a <see cref="System.DateTimeOffset"/>,
    /// or written from a <see cref="System.DateTime"/>.
    /// </summary>
    Timestamp = 17,

    /// <summary>
    /// A universally unique identifier (section 1.6.18): a <see cref="System.Guid"/>.
    /// </summary>
    Uuid = 18,

    /// <summary>
    /// A sequence of octets (section 1.6.19): a <see cref="byte"/>[], or written from a
    /// <see cref="System.ReadOnlyMemory{T}"/>, <see cref="System.Memory{T}"/> or <see cref="System.ArraySegment{T}"/> of
    /// <see cref="byte"/>, or an <see cref="AmqpDeliveryTag"/>.
    /// </summary>
    Binary = 19,

    /// <summary>
    /// A sequence of Unicode characters (section 1.6.20): a <see cref="string"/>.
    /// </summary>
    String = 20,

    /// <summary>
    /// A symbolic value from a constrained domain, in seven-bit ASCII (section 1.6.21): an <see cref="AmqpSymbol"/>.
    /// </summary>
    Symbol = 21,

    /// <summary>
    /// A sequence of values of any type (section 1.6.22): a <see cref="System.Collections.Generic.List{T}"/> of
    /// <see cref="object"/>, or written from another <see cref="System.Collections.Generic.IReadOnlyList{T}"/> of
    /// <see cref="object"/>, an <see cref="object"/>[] or an <see cref="System.Collections.IList"/>.
    /// </summary>
    List = 22,

    /// <summary>
    /// A polymorphic mapping from distinct keys to values (section 1.6.23): a
    /// <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/> of <see cref="object"/> to <see cref="object"/>,
    /// or written from a read-only dictionary keyed by <see cref="object"/>, <see cref="string"/> or
    /// <see cref="AmqpSymbol"/>, or an <see cref="System.Collections.IDictionary"/>.
    /// </summary>
    Map = 23,

    /// <summary>
    /// A sequence of values of one type (section 1.6.24): the typed CLR array of its element type, such as an
    /// <see cref="int"/>[] or a <see cref="string"/>[], or an <see cref="AmqpArray"/> when no CLR array type holds it.
    /// </summary>
    Array = 24
}
