using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a delivery tag: up to 32 bytes that identify a delivery on its link (AMQP 1.0 Part 2,
/// section 2.8.7).
/// </summary>
/// <remarks>
/// Two tags are equal when their bytes are equal, so a tag can key a map of unsettled deliveries. The
/// default value is the empty tag, which AMQP allows.
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public readonly struct AmqpDeliveryTag : IEquatable<AmqpDeliveryTag>
{
    /// <summary>
    /// The longest delivery tag AMQP allows, in bytes.
    /// </summary>
    public const int MaxLength = 32;

    private readonly byte[]? _value;

    private AmqpDeliveryTag(byte[] value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the bytes of the tag.
    /// </summary>
    public ReadOnlyMemory<byte> Value => _value;

    /// <summary>
    /// Gets the length of the tag, in bytes.
    /// </summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>
    /// Creates a tag from a copy of the specified bytes.
    /// </summary>
    /// <param name="value">The bytes of the tag.</param>
    /// <returns>The delivery tag.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is longer than <see cref="MaxLength"/> bytes.</exception>
    public static AmqpDeliveryTag Create(ReadOnlySpan<byte> value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(ErrorMessages.DeliveryTagTooLong, nameof(value));
        }

        return new AmqpDeliveryTag(value.ToArray());
    }

    /// <summary>
    /// Creates a tag from an unsigned integer, as its shortest big-endian encoding (at least one byte).
    /// </summary>
    /// <param name="value">The integer.</param>
    /// <returns>The delivery tag.</returns>
    public static AmqpDeliveryTag Create(ulong value)
    {
        int length = Math.Max(1, (64 - BitOperations.LeadingZeroCount(value) + 7) / 8);
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);

        return new AmqpDeliveryTag(buffer[(sizeof(ulong) - length)..].ToArray());
    }

    /// <summary>
    /// Creates a tag from a copy of the specified bytes, when they are short enough.
    /// </summary>
    /// <param name="value">The bytes of the tag.</param>
    /// <param name="tag">The delivery tag, or the empty tag when <paramref name="value"/> is too long.</param>
    /// <returns><see langword="true"/> when the tag was created; otherwise <see langword="false"/>.</returns>
    public static bool TryCreate(ReadOnlySpan<byte> value, out AmqpDeliveryTag tag)
    {
        if (!IsValid(value))
        {
            tag = default;
            return false;
        }

        tag = new AmqpDeliveryTag(value.ToArray());
        return true;
    }

    /// <summary>
    /// Determines whether bytes are short enough to be a delivery tag.
    /// </summary>
    /// <param name="value">The candidate bytes.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is at most <see cref="MaxLength"/> bytes; otherwise <see langword="false"/>.</returns>
    public static bool IsValid(ReadOnlySpan<byte> value)
    {
        return value.Length <= MaxLength;
    }

    /// <summary>
    /// Determines whether two tags hold the same bytes.
    /// </summary>
    /// <param name="left">The first tag.</param>
    /// <param name="right">The second tag.</param>
    /// <returns><see langword="true"/> when the tags are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(AmqpDeliveryTag left, AmqpDeliveryTag right) => left.Equals(right);

    /// <summary>
    /// Determines whether two tags hold different bytes.
    /// </summary>
    /// <param name="left">The first tag.</param>
    /// <param name="right">The second tag.</param>
    /// <returns><see langword="true"/> when the tags differ; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(AmqpDeliveryTag left, AmqpDeliveryTag right) => !left.Equals(right);

    /// <summary>
    /// Determines whether this tag holds the same bytes as another.
    /// </summary>
    /// <param name="other">The tag to compare with.</param>
    /// <returns><see langword="true"/> when the tags are equal; otherwise <see langword="false"/>.</returns>
    public bool Equals(AmqpDeliveryTag other)
    {
        return Value.Span.SequenceEqual(other.Value.Span);
    }

    /// <summary>
    /// Determines whether this tag equals another object.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is an equal tag; otherwise <see langword="false"/>.</returns>
    public override bool Equals(object? obj)
    {
        return obj is AmqpDeliveryTag other && Equals(other);
    }

    /// <summary>
    /// Returns a hash code over the bytes of the tag.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        HashCode hash = default;

        hash.AddBytes(Value.Span);

        return hash.ToHashCode();
    }

    /// <summary>
    /// Returns the bytes of the tag as lowercase hexadecimal text.
    /// </summary>
    /// <returns>The hexadecimal text; empty for the empty tag.</returns>
    public override string ToString()
    {
        return Convert.ToHexStringLower(Value.Span);
    }

    private sealed class DebugView
    {
        private readonly AmqpDeliveryTag _tag;

        public DebugView(AmqpDeliveryTag tag)
        {
            _tag = tag;
        }

        public ReadOnlyMemory<byte> Value => _tag.Value;
        public int Length => _tag.Length;
    }
}
