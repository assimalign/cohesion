using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// A growable, contiguous buffer on pooled arrays that the encoder writes into, and that can rewrite
/// what it already holds: a compound value's size is patched in once its members are written.
/// </summary>
/// <remarks>Not thread-safe. <see cref="Dispose"/> returns the array to the pool.</remarks>
[DebuggerDisplay("Length = {Length}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int minimumCapacity = 256;

    private byte[] _buffer;
    private int _length;

    public AmqpBufferWriter()
        : this(minimumCapacity)
    {
    }

    public AmqpBufferWriter(int initialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, minimumCapacity));
    }

    /// <summary>
    /// Gets the number of bytes written.
    /// </summary>
    public int Length => _length;

    /// <summary>
    /// Gets the bytes written.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>
    /// Gets the bytes written.
    /// </summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_length);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_length);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _length);

        _length += count;
    }

    /// <summary>
    /// Appends <paramref name="count"/> bytes and returns them for the caller to fill.
    /// </summary>
    public Span<byte> Reserve(int count)
    {
        EnsureCapacity(count);

        Span<byte> span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    /// <summary>
    /// Returns bytes already written, for the caller to rewrite.
    /// </summary>
    public Span<byte> GetWrittenSpan(int start, int count)
    {
        Debug.Assert(start >= 0 && count >= 0 && start + count <= _length);

        return _buffer.AsSpan(start, count);
    }

    /// <summary>
    /// Discards everything written after <paramref name="length"/>.
    /// </summary>
    public void Truncate(int length)
    {
        Debug.Assert(length >= 0 && length <= _length);

        _length = length;
    }

    /// <summary>
    /// Moves the written bytes from <paramref name="sourceStart"/> to the end down to
    /// <paramref name="destinationStart"/>, and shortens the buffer by the distance moved.
    /// </summary>
    public void ShiftDown(int sourceStart, int destinationStart)
    {
        Debug.Assert(destinationStart <= sourceStart && sourceStart <= _length);

        int count = _length - sourceStart;

        _buffer.AsSpan(sourceStart, count).CopyTo(_buffer.AsSpan(destinationStart, count));
        _length = destinationStart + count;
    }

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(Reserve(2), value);
    }

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(Reserve(4), value);
    }

    public void WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), value);
    }

    /// <summary>
    /// Discards everything written, keeping the array.
    /// </summary>
    public void Reset()
    {
        _length = 0;
    }

    public byte[] ToArray()
    {
        return WrittenSpan.ToArray();
    }

    public void Dispose()
    {
        byte[] buffer = _buffer;

        _buffer = [];
        _length = 0;

        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        if (_buffer.Length - _length < sizeHint)
        {
            Grow(sizeHint);
        }
    }

    private void Grow(int sizeHint)
    {
        int required = checked(_length + sizeHint);
        int size = (int)Math.Min(Math.Max((long)required, _buffer.Length * 2L), Array.MaxLength);

        if (size < required)
        {
            throw new OutOfMemoryException();
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        byte[] previous = _buffer;

        previous.AsSpan(0, _length).CopyTo(buffer);
        _buffer = buffer;

        if (previous.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(previous);
        }
    }

    private sealed class DebugView
    {
        private readonly AmqpBufferWriter _writer;

        public DebugView(AmqpBufferWriter writer)
        {
            _writer = writer;
        }

        public int Length => _writer.Length;
    }
}
