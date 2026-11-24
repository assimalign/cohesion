using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Writes and reads AMQP frames and protocol headers (AMQP 1.0 Part 2, sections 2.2 and 2.3).
/// </summary>
internal static class AmqpFrame
{
    /// <summary>
    /// The size of a frame header, and of an empty frame.
    /// </summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// The size of a protocol header.
    /// </summary>
    public const int ProtocolHeaderSize = 8;

    /// <summary>
    /// The largest frame either peer may send before the <c>open</c> exchange sets the limit, and the
    /// limit on every SASL frame (MIN-MAX-FRAME-SIZE).
    /// </summary>
    public const uint MinMaxFrameSize = 512;

    public const byte AmqpType = 0x00;
    public const byte SaslType = 0x01;

    /// <summary>
    /// The protocol identifier of the AMQP protocol.
    /// </summary>
    public const byte AmqpProtocolId = 0;

    /// <summary>
    /// The protocol identifier of the TLS security layer.
    /// </summary>
    public const byte TlsProtocolId = 2;

    /// <summary>
    /// The protocol identifier of the SASL security layer.
    /// </summary>
    public const byte SaslProtocolId = 3;

    /// <summary>
    /// Gets the protocol header that selects AMQP 1.0.0.
    /// </summary>
    public static ReadOnlySpan<byte> AmqpProtocolHeader => [(byte)'A', (byte)'M', (byte)'Q', (byte)'P', AmqpProtocolId, 1, 0, 0];

    /// <summary>
    /// Gets the protocol header that selects the SASL layer of AMQP 1.0.0.
    /// </summary>
    public static ReadOnlySpan<byte> SaslProtocolHeader => [(byte)'A', (byte)'M', (byte)'Q', (byte)'P', SaslProtocolId, 1, 0, 0];

    /// <summary>
    /// Writes a frame carrying a performative and, for a <c>transfer</c>, part of a payload.
    /// </summary>
    public static void Write(AmqpBufferWriter writer, byte type, ushort channel, AmqpPerformative performative, ReadOnlySpan<byte> payload = default)
    {
        int start = Begin(writer, type, channel);

        performative.Encode(writer);
        writer.Write(payload);
        End(writer, start);
    }

    /// <summary>
    /// Writes an empty frame, which keeps an idle connection alive (AMQP 1.0 Part 2, section 2.4.5).
    /// </summary>
    public static void WriteEmpty(AmqpBufferWriter writer)
    {
        Span<byte> frame = writer.Reserve(HeaderSize);

        BinaryPrimitives.WriteUInt32BigEndian(frame, HeaderSize);
        frame[4] = 2;
        frame[5] = AmqpType;
        frame[6] = 0;
        frame[7] = 0;
    }

    /// <summary>
    /// Begins a frame by writing its header with the size left to <see cref="End"/>.
    /// </summary>
    /// <returns>The position of the frame.</returns>
    public static int Begin(AmqpBufferWriter writer, byte type, ushort channel)
    {
        int start = writer.Length;
        Span<byte> header = writer.Reserve(HeaderSize);

        header[4] = 2;
        header[5] = type;
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(6), channel);
        return start;
    }

    /// <summary>
    /// Ends a frame begun with <see cref="Begin"/> by writing its size.
    /// </summary>
    public static void End(AmqpBufferWriter writer, int start)
    {
        BinaryPrimitives.WriteUInt32BigEndian(writer.GetWrittenSpan(start, 4), (uint)(writer.Length - start));
    }

    /// <summary>
    /// Reads the size the next frame header declares, once the buffer holds the four bytes that carry it.
    /// </summary>
    /// <param name="buffer">The received bytes; not advanced.</param>
    /// <param name="size">The declared size of the whole frame, header included.</param>
    /// <returns><see langword="true"/> when the size field has arrived; <see langword="false"/> when more bytes are needed.</returns>
    public static bool TryReadSize(in ReadOnlySequence<byte> buffer, out uint size)
    {
        if (buffer.Length < sizeof(uint))
        {
            size = 0;
            return false;
        }

        Span<byte> bytes = stackalloc byte[sizeof(uint)];

        buffer.Slice(0, sizeof(uint)).CopyTo(bytes);
        size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return true;
    }

    /// <summary>
    /// Reads the next frame when the buffer holds all of it.
    /// </summary>
    /// <param name="buffer">The received bytes; advanced past the frame when one is read.</param>
    /// <param name="maxFrameSize">The largest frame the local endpoint accepts.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="body">The frame body, after any extended header.</param>
    /// <returns><see langword="true"/> when a whole frame was read; <see langword="false"/> when more bytes are needed.</returns>
    /// <exception cref="AmqpException">Thrown with <c>amqp:connection:framing-error</c> when the header is invalid or the frame exceeds <paramref name="maxFrameSize"/>.</exception>
    public static bool TryRead(ref ReadOnlySequence<byte> buffer, uint maxFrameSize, out AmqpFrameHeader header, out ReadOnlySequence<byte> body)
    {
        header = default;
        body = default;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[HeaderSize];

        buffer.Slice(0, HeaderSize).CopyTo(bytes);

        uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        byte dataOffset = bytes[4];

        if (size < HeaderSize)
        {
            throw AmqpException.FramingError(ErrorMessages.FrameSizeInvalid);
        }

        if (size > maxFrameSize)
        {
            throw AmqpException.FramingError(string.Format(CultureInfo.CurrentCulture, ErrorMessages.FrameTooLarge, size, maxFrameSize));
        }

        if (dataOffset < 2 || dataOffset * 4u > size)
        {
            throw AmqpException.FramingError(ErrorMessages.FrameDataOffsetInvalid);
        }

        if (buffer.Length < size)
        {
            return false;
        }

        header = new AmqpFrameHeader(size, dataOffset, bytes[5], BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6)));
        body = buffer.Slice(dataOffset * 4, size - dataOffset * 4u);
        buffer = buffer.Slice(size);
        return true;
    }
}
