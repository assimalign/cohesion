using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Writes the fields of a described list type, such as a performative, in order, and leaves out the
/// trailing fields that are <c>null</c> (AMQP 1.0 Part 1, section 1.4: a missing trailing field is
/// <c>null</c>).
/// </summary>
/// <remarks>
/// A field whose value equals its default is written as <c>null</c>, which a peer reads as the default,
/// so a list of defaults shrinks to <c>list0</c>. Call <see cref="Complete"/> once, after the last field.
/// </remarks>
[DebuggerDisplay("AmqpListWriter")]
[DebuggerTypeProxy(typeof(DebugView))]
internal ref struct AmqpListWriter
{
    private readonly AmqpBufferWriter _writer;
    private readonly int _start;
    private int _count;
    private int _significantCount;
    private int _significantEnd;

    public AmqpListWriter(AmqpBufferWriter writer, ulong descriptor)
    {
        AmqpEncoder.WriteDescriptor(writer, descriptor);

        _writer = writer;
        _start = AmqpEncoder.BeginCompound(writer, AmqpFormatCode.List32);
        _significantEnd = writer.Length;
    }

    /// <summary>
    /// Gets the buffer, for a field written by another encoder; follow it with <see cref="Field(bool)"/>.
    /// </summary>
    public readonly AmqpBufferWriter Writer => _writer;

    public void WriteNull()
    {
        AmqpEncoder.WriteNull(_writer);
        _count++;
    }

    public void WriteBoolean(bool value)
    {
        AmqpEncoder.WriteBoolean(_writer, value);
        Field(false);
    }

    public void WriteBoolean(bool value, bool defaultValue)
    {
        if (value == defaultValue)
        {
            WriteNull();
        }
        else
        {
            WriteBoolean(value);
        }
    }

    public void WriteBoolean(bool? value)
    {
        if (value is bool boolean)
        {
            WriteBoolean(boolean);
        }
        else
        {
            WriteNull();
        }
    }

    public void WriteUByte(byte value, byte defaultValue)
    {
        if (value == defaultValue)
        {
            WriteNull();
        }
        else
        {
            AmqpEncoder.WriteUByte(_writer, value);
            Field(false);
        }
    }

    public void WriteUByte(byte? value)
    {
        if (value is byte number)
        {
            AmqpEncoder.WriteUByte(_writer, number);
            Field(false);
        }
        else
        {
            WriteNull();
        }
    }

    public void WriteUShort(ushort value, ushort defaultValue)
    {
        if (value == defaultValue)
        {
            WriteNull();
        }
        else
        {
            AmqpEncoder.WriteUShort(_writer, value);
            Field(false);
        }
    }

    public void WriteUShort(ushort? value)
    {
        if (value is ushort number)
        {
            AmqpEncoder.WriteUShort(_writer, number);
            Field(false);
        }
        else
        {
            WriteNull();
        }
    }

    public void WriteUInt(uint value)
    {
        AmqpEncoder.WriteUInt(_writer, value);
        Field(false);
    }

    public void WriteUInt(uint value, uint defaultValue)
    {
        if (value == defaultValue)
        {
            WriteNull();
        }
        else
        {
            WriteUInt(value);
        }
    }

    public void WriteUInt(uint? value)
    {
        if (value is uint number)
        {
            WriteUInt(number);
        }
        else
        {
            WriteNull();
        }
    }

    public void WriteULong(ulong value)
    {
        AmqpEncoder.WriteULong(_writer, value);
        Field(false);
    }

    public void WriteULong(ulong? value)
    {
        if (value is ulong number)
        {
            WriteULong(number);
        }
        else
        {
            WriteNull();
        }
    }

    public void WriteString(string? value)
    {
        AmqpEncoder.WriteString(_writer, value);
        Field(value is null);
    }

    /// <summary>
    /// Writes a symbol; a default symbol is written as <c>null</c>.
    /// </summary>
    public void WriteSymbol(AmqpSymbol value)
    {
        AmqpEncoder.WriteSymbol(_writer, value);
        Field(value.Value is null);
    }

    public void WriteSymbol(AmqpSymbol? value)
    {
        WriteSymbol(value.GetValueOrDefault());
    }

    /// <summary>
    /// Writes a symbol field that may hold several values; none is written as <c>null</c>.
    /// </summary>
    public void WriteSymbolMultiple(IReadOnlyList<AmqpSymbol>? values)
    {
        if (values is null || values.Count == 0)
        {
            WriteNull();
        }
        else
        {
            AmqpEncoder.WriteSymbolArray(_writer, values);
            Field(false);
        }
    }

    public void WriteSymbolMap(IReadOnlyDictionary<AmqpSymbol, object?>? value)
    {
        AmqpEncoder.WriteSymbolMap(_writer, value, 1);
        Field(value is null);
    }

    public void WriteBinary(ReadOnlySpan<byte> value)
    {
        AmqpEncoder.WriteBinary(_writer, value);
        Field(false);
    }

    public void WriteBinary(ReadOnlyMemory<byte>? value)
    {
        if (value is ReadOnlyMemory<byte> bytes)
        {
            WriteBinary(bytes.Span);
        }
        else
        {
            WriteNull();
        }
    }

    public void WriteTimestamp(DateTimeOffset? value)
    {
        if (value is DateTimeOffset timestamp)
        {
            AmqpEncoder.WriteTimestamp(_writer, timestamp);
            Field(false);
        }
        else
        {
            WriteNull();
        }
    }

    public void WriteValue(object? value)
    {
        AmqpEncoder.WriteValue(_writer, value, 1);
        Field(value is null);
    }

    /// <summary>
    /// Records a field written through <see cref="Writer"/>.
    /// </summary>
    /// <param name="isNull">Whether the field was written as <c>null</c>.</param>
    public void Field(bool isNull)
    {
        _count++;

        if (!isNull)
        {
            _significantCount = _count;
            _significantEnd = _writer.Length;
        }
    }

    /// <summary>
    /// Ends the list, dropping the trailing <c>null</c> fields.
    /// </summary>
    public readonly void Complete()
    {
        _writer.Truncate(_significantEnd);
        AmqpEncoder.EndList(_writer, _start, _significantCount);
    }

    private sealed class DebugView
    {
        private readonly AmqpBufferWriter _writer;

        public DebugView(AmqpListWriter writer)
        {
            // A ref struct cannot be held on the heap, so the view keeps a snapshot of the buffer reference.
            _writer = writer.Writer;
        }

        public AmqpBufferWriter Writer => _writer;
    }
}
