using System;
using System.Buffers.Binary;
using System.Text;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// Builds order-preserving, self-describing binary keys from typed scalar
/// components: comparing two encoded keys byte-wise (unsigned lexicographic) yields
/// the same result as comparing their components value-by-value.
/// </summary>
/// <remarks>
/// <para>
/// Every component starts with a tag byte — the component's <see cref="DatabaseType"/>
/// value — so keys are self-describing, nulls (<see cref="AppendNull"/>, tag 0) order
/// before every value, and components of different types order deterministically by
/// tag. Composite keys are built by appending components in significance order.
/// </para>
/// <para>
/// Encodings (all big-endian, chosen so byte order equals value order): integers are
/// sign-flipped; floating point uses the IEEE-754 total-order fold (negative zero
/// below zero, NaN canonicalized above positive infinity); decimals use normalized
/// scientific notation with complemented negatives; strings carry their
/// <see cref="Collation"/> identifier and, for non-binary collations, an
/// order-defining sort key followed by the original bytes for round-tripping;
/// variable-length payloads are zero-escaped and terminated. <see cref="DatabaseType.Json"/>
/// and <see cref="DatabaseType.JsonBinary"/> are not orderable key components.
/// </para>
/// <para>
/// The writer is reusable: <see cref="Reset"/> clears it without releasing the buffer.
/// </para>
/// </remarks>
public sealed class DatabaseKeyWriter
{
    private byte[] _buffer;
    private int _length;

    /// <summary>
    /// Initializes a new writer with the default initial capacity.
    /// </summary>
    public DatabaseKeyWriter()
        : this(64)
    {
    }

    /// <summary>
    /// Initializes a new writer with the given initial capacity.
    /// </summary>
    /// <param name="capacity">Initial buffer capacity in bytes.</param>
    public DatabaseKeyWriter(int capacity)
    {
        _buffer = new byte[Math.Max(capacity, 16)];
    }

    /// <summary>
    /// Gets the encoded key bytes written so far.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>
    /// Gets the number of bytes written.
    /// </summary>
    public int Length => _length;

    /// <summary>
    /// Copies the encoded key to a new array.
    /// </summary>
    /// <returns>The encoded key bytes.</returns>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>
    /// Clears the writer for reuse without releasing the buffer.
    /// </summary>
    public void Reset() => _length = 0;

    /// <summary>
    /// Appends a null component. Nulls order before every value of every type.
    /// </summary>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendNull()
    {
        WriteByte((byte)DatabaseType.Null);
        return this;
    }

    /// <summary>
    /// Appends a boolean component (false before true).
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendBoolean(bool value)
    {
        WriteByte((byte)DatabaseType.Boolean);
        WriteByte(value ? (byte)1 : (byte)0);
        return this;
    }

    /// <summary>
    /// Appends an 8-bit signed integer component.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendInt8(sbyte value)
    {
        WriteByte((byte)DatabaseType.Int8);
        WriteByte((byte)(value ^ sbyte.MinValue));
        return this;
    }

    /// <summary>
    /// Appends a 16-bit signed integer component.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendInt16(short value)
    {
        WriteByte((byte)DatabaseType.Int16);
        Span<byte> scratch = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteUInt16BigEndian(scratch, (ushort)(value ^ short.MinValue));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a 32-bit signed integer component.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendInt32(int value)
    {
        WriteByte((byte)DatabaseType.Int32);
        Span<byte> scratch = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteUInt32BigEndian(scratch, (uint)(value ^ int.MinValue));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a 64-bit signed integer component.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendInt64(long value)
    {
        WriteByte((byte)DatabaseType.Int64);
        Span<byte> scratch = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteUInt64BigEndian(scratch, (ulong)(value ^ long.MinValue));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a 32-bit floating point component (IEEE-754 total order).
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendFloat32(float value)
    {
        WriteByte((byte)DatabaseType.Float32);
        Span<byte> scratch = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(scratch, KeyComponentEncoding.FoldSingle(value));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a 64-bit floating point component (IEEE-754 total order).
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendFloat64(double value)
    {
        WriteByte((byte)DatabaseType.Float64);
        Span<byte> scratch = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(scratch, KeyComponentEncoding.FoldDouble(value));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a decimal component.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendDecimal(decimal value)
    {
        WriteByte((byte)DatabaseType.Decimal);
        KeyComponentEncoding.WriteDecimal(this, value);
        return this;
    }

    /// <summary>
    /// Appends a string component under an explicit collation.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <param name="collation">The collation defining the component's order.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendString(string value, Collation collation)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(collation);

        WriteByte((byte)DatabaseType.String);
        WriteByte(collation.Id);

        byte[] utf8 = Encoding.UTF8.GetBytes(value);

        if (ReferenceEquals(collation, Collation.Binary))
        {
            // UTF-8 byte order is the collation order — the payload is the sort key.
            KeyComponentEncoding.WriteEscaped(this, utf8);
            return this;
        }

        // Linguistic collations: an order-defining sort key, then the original bytes
        // (length-prefixed) so decoding round-trips. Sort-key ties (collation-equal
        // strings) break deterministically on the original bytes.
        byte[] sortKey = System.Globalization.CultureInfo.InvariantCulture.CompareInfo
            .GetSortKey(value, System.Globalization.CompareOptions.None).KeyData;
        KeyComponentEncoding.WriteEscaped(this, sortKey);

        Span<byte> lengthScratch = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(lengthScratch, utf8.Length);
        WriteBytes(lengthScratch);
        WriteBytes(utf8);
        return this;
    }

    /// <summary>
    /// Appends a binary component (unsigned lexicographic byte order).
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendBinary(ReadOnlySpan<byte> value)
    {
        WriteByte((byte)DatabaseType.Binary);
        KeyComponentEncoding.WriteEscaped(this, value);
        return this;
    }

    /// <summary>
    /// Appends a date component.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendDate(DateOnly value)
    {
        WriteByte((byte)DatabaseType.Date);
        Span<byte> scratch = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteUInt32BigEndian(scratch, (uint)(value.DayNumber ^ int.MinValue));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a time-of-day component.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendTime(TimeOnly value)
    {
        WriteByte((byte)DatabaseType.Time);
        Span<byte> scratch = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteUInt64BigEndian(scratch, (ulong)(value.Ticks ^ long.MinValue));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a date-time component ordered by ticks. The kind is preserved for
    /// round-tripping but does not participate in ordering — store UTC when values
    /// of mixed kinds could meet in one key.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendDateTime(DateTime value)
    {
        WriteByte((byte)DatabaseType.DateTime);
        Span<byte> scratch = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteUInt64BigEndian(scratch, (ulong)(value.Ticks ^ long.MinValue));
        WriteBytes(scratch);
        WriteByte((byte)value.Kind);
        return this;
    }

    /// <summary>
    /// Appends a date-time-offset component ordered by its UTC instant; the offset is
    /// preserved for round-tripping and breaks instant ties deterministically.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendDateTimeOffset(DateTimeOffset value)
    {
        WriteByte((byte)DatabaseType.DateTimeOffset);
        Span<byte> scratch = stackalloc byte[sizeof(long) + sizeof(short)];
        BinaryPrimitives.WriteUInt64BigEndian(scratch, (ulong)(value.UtcTicks ^ long.MinValue));
        BinaryPrimitives.WriteUInt16BigEndian(scratch[sizeof(long)..], (ushort)((short)value.Offset.TotalMinutes ^ short.MinValue));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a duration component.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendTimeSpan(TimeSpan value)
    {
        WriteByte((byte)DatabaseType.TimeSpan);
        Span<byte> scratch = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteUInt64BigEndian(scratch, (ulong)(value.Ticks ^ long.MinValue));
        WriteBytes(scratch);
        return this;
    }

    /// <summary>
    /// Appends a GUID component ordered by its RFC 4122 big-endian byte sequence.
    /// </summary>
    /// <param name="value">The value to append.</param>
    /// <returns>This writer.</returns>
    public DatabaseKeyWriter AppendGuid(Guid value)
    {
        WriteByte((byte)DatabaseType.Guid);
        Span<byte> scratch = stackalloc byte[16];
        value.TryWriteBytes(scratch, bigEndian: true, out _);
        WriteBytes(scratch);
        return this;
    }

    internal void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    internal void WriteBytes(ReadOnlySpan<byte> values)
    {
        EnsureCapacity(values.Length);
        values.CopyTo(_buffer.AsSpan(_length));
        _length += values.Length;
    }

    private void EnsureCapacity(int additional)
    {
        if (_length + additional <= _buffer.Length)
        {
            return;
        }

        int newSize = Math.Max(_buffer.Length * 2, _length + additional);
        Array.Resize(ref _buffer, newSize);
    }
}
