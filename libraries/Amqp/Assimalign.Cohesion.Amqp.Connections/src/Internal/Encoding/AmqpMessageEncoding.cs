using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Encodes and decodes the sections of an AMQP 1.0 message (Part 3, section 3.2).
/// </summary>
/// <remarks>
/// Sections are decoded in the order the specification fixes: header, delivery annotations, message
/// annotations, properties, application properties, body, footer. Each appears at most once, except that
/// the body may be several <c>data</c> or several <c>amqp-sequence</c> sections. A message without a body
/// section decodes with a <see langword="null"/> <see cref="AmqpMessage.Body"/>.
/// </remarks>
internal static class AmqpMessageEncoding
{
    private const int stageHeader = 1;
    private const int stageDeliveryAnnotations = 2;
    private const int stageMessageAnnotations = 3;
    private const int stageProperties = 4;
    private const int stageApplicationProperties = 5;
    private const int stageBody = 6;
    private const int stageFooter = 7;

    public static void Encode(AmqpBufferWriter writer, AmqpMessage message)
    {
        if (message.Header is AmqpHeader header)
        {
            WriteHeader(writer, header);
        }

        WriteAnnotations(writer, AmqpDescriptor.DeliveryAnnotations, message.DeliveryAnnotations);
        WriteAnnotations(writer, AmqpDescriptor.MessageAnnotations, message.MessageAnnotations);

        if (message.Properties is AmqpProperties properties)
        {
            WriteProperties(writer, properties);
        }

        if (message.ApplicationProperties is not null)
        {
            AmqpEncoder.WriteDescriptor(writer, AmqpDescriptor.ApplicationProperties);
            AmqpEncoder.WriteStringMap(writer, message.ApplicationProperties);
        }

        switch (message.Body)
        {
            case AmqpDataBody data:
                foreach (ReadOnlyMemory<byte> section in data.Sections)
                {
                    AmqpEncoder.WriteDescriptor(writer, AmqpDescriptor.Data);
                    AmqpEncoder.WriteBinary(writer, section.Span);
                }

                break;
            case AmqpSequenceBody sequence:
                foreach (IReadOnlyList<object?> section in sequence.Sequences)
                {
                    AmqpEncoder.WriteDescriptor(writer, AmqpDescriptor.AmqpSequence);
                    AmqpEncoder.WriteList(writer, section);
                }

                break;
            case AmqpValueBody value:
                AmqpEncoder.WriteDescriptor(writer, AmqpDescriptor.AmqpValue);
                AmqpEncoder.WriteValue(writer, value.Value);
                break;
        }

        WriteAnnotations(writer, AmqpDescriptor.Footer, message.Footer);
    }

    /// <summary>
    /// Decodes a message. <c>data</c> sections and the <c>user-id</c> are slices of
    /// <paramref name="payload"/>, not copies.
    /// </summary>
    public static AmqpMessage Decode(ReadOnlyMemory<byte> payload)
    {
        AmqpReader reader = new(payload.Span);
        AmqpMessage message = new();
        int stage = 0;
        List<ReadOnlyMemory<byte>>? data = null;
        List<IReadOnlyList<object?>>? sequences = null;
        AmqpValueBody? value = null;

        while (!reader.IsEnd)
        {
            if (!reader.TryReadDescriptor(out ulong? code, out _))
            {
                throw AmqpException.DecodeError(ErrorMessages.SectionNotDescribed);
            }

            switch (code)
            {
                case AmqpDescriptor.Header:
                    Advance(ref stage, stageHeader);
                    message.Header = ReadHeaderBody(ref reader);
                    break;
                case AmqpDescriptor.DeliveryAnnotations:
                    Advance(ref stage, stageDeliveryAnnotations);
                    message.DeliveryAnnotations = reader.ReadSymbolMap();
                    break;
                case AmqpDescriptor.MessageAnnotations:
                    Advance(ref stage, stageMessageAnnotations);
                    message.MessageAnnotations = reader.ReadSymbolMap();
                    break;
                case AmqpDescriptor.Properties:
                    Advance(ref stage, stageProperties);
                    message.Properties = ReadPropertiesBody(ref reader, payload);
                    break;
                case AmqpDescriptor.ApplicationProperties:
                    Advance(ref stage, stageApplicationProperties);
                    message.ApplicationProperties = reader.ReadStringMap();
                    break;
                case AmqpDescriptor.Data:
                    if (sequences is not null || value is not null || (stage > stageBody) || (stage == stageBody && data is null))
                    {
                        throw AmqpException.DecodeError(ErrorMessages.SectionOrderInvalid);
                    }

                    stage = stageBody;

                    if (!reader.ReadBinaryRange(out int offset, out int length))
                    {
                        throw AmqpException.DecodeError(ErrorMessages.ValueNotBinary);
                    }

                    (data ??= new List<ReadOnlyMemory<byte>>()).Add(payload.Slice(offset, length));
                    break;
                case AmqpDescriptor.AmqpSequence:
                    if (data is not null || value is not null || (stage > stageBody) || (stage == stageBody && sequences is null))
                    {
                        throw AmqpException.DecodeError(ErrorMessages.SectionOrderInvalid);
                    }

                    stage = stageBody;

                    if (reader.ReadValue() is not List<object?> sequence)
                    {
                        throw AmqpException.DecodeError(ErrorMessages.ValueNotList);
                    }

                    (sequences ??= new List<IReadOnlyList<object?>>()).Add(sequence);
                    break;
                case AmqpDescriptor.AmqpValue:
                    Advance(ref stage, stageBody);
                    value = new AmqpValueBody(reader.ReadValue());
                    break;
                case AmqpDescriptor.Footer:
                    Advance(ref stage, stageFooter);
                    message.Footer = reader.ReadSymbolMap();
                    break;
                default:
                    throw AmqpException.DecodeError(ErrorMessages.SectionDescriptorUnsupported);
            }
        }

        if (data is not null)
        {
            message.Body = new AmqpDataBody(data);
        }
        else if (sequences is not null)
        {
            message.Body = new AmqpSequenceBody(sequences);
        }
        else
        {
            message.Body = value;
        }

        return message;
    }

    /// <summary>
    /// Finds the bare message: the properties, application properties and body sections, which an
    /// intermediary must forward unchanged (Part 3, section 3.2).
    /// </summary>
    /// <returns>The offset and length of the bare message within <paramref name="payload"/>.</returns>
    public static (int Offset, int Length) FindBareMessage(ReadOnlySpan<byte> payload)
    {
        AmqpReader reader = new(payload);
        int start = -1;
        int end = -1;

        while (!reader.IsEnd)
        {
            int sectionStart = reader.Position;

            if (!reader.TryReadDescriptor(out ulong? code, out _))
            {
                throw AmqpException.DecodeError(ErrorMessages.SectionNotDescribed);
            }

            reader.Skip();

            switch (code)
            {
                case AmqpDescriptor.Header:
                case AmqpDescriptor.DeliveryAnnotations:
                case AmqpDescriptor.MessageAnnotations:
                    if (start >= 0)
                    {
                        throw AmqpException.DecodeError(ErrorMessages.SectionOrderInvalid);
                    }

                    break;
                case AmqpDescriptor.Properties:
                case AmqpDescriptor.ApplicationProperties:
                case AmqpDescriptor.Data:
                case AmqpDescriptor.AmqpSequence:
                case AmqpDescriptor.AmqpValue:
                    if (start < 0)
                    {
                        start = sectionStart;
                    }

                    end = reader.Position;
                    break;
                case AmqpDescriptor.Footer:
                    if (!reader.IsEnd)
                    {
                        throw AmqpException.DecodeError(ErrorMessages.SectionOrderInvalid);
                    }

                    break;
                default:
                    throw AmqpException.DecodeError(ErrorMessages.SectionDescriptorUnsupported);
            }
        }

        return start < 0 ? (payload.Length, 0) : (start, end - start);
    }

    public static void WriteHeader(AmqpBufferWriter writer, AmqpHeader header)
    {
        AmqpListWriter list = new(writer, AmqpDescriptor.Header);

        list.WriteBoolean(header.Durable, false);
        list.WriteUByte(header.Priority, 4);
        list.WriteUInt(header.TimeToLive);
        list.WriteBoolean(header.FirstAcquirer, false);
        list.WriteUInt(header.DeliveryCount, 0u);
        list.Complete();
    }

    public static void WriteAnnotations(AmqpBufferWriter writer, ulong descriptor, IReadOnlyDictionary<AmqpSymbol, object?>? annotations)
    {
        if (annotations is null)
        {
            return;
        }

        AmqpEncoder.WriteDescriptor(writer, descriptor);
        AmqpEncoder.WriteSymbolMap(writer, annotations);
    }

    private static void WriteProperties(AmqpBufferWriter writer, AmqpProperties properties)
    {
        AmqpListWriter list = new(writer, AmqpDescriptor.Properties);

        list.WriteValue(properties.MessageId);
        list.WriteBinary(properties.UserId);
        list.WriteString(properties.To);
        list.WriteString(properties.Subject);
        list.WriteString(properties.ReplyTo);
        list.WriteValue(properties.CorrelationId);
        list.WriteSymbol(properties.ContentType);
        list.WriteSymbol(properties.ContentEncoding);
        list.WriteTimestamp(properties.AbsoluteExpiryTime);
        list.WriteTimestamp(properties.CreationTime);
        list.WriteString(properties.GroupId);
        list.WriteUInt(properties.GroupSequence);
        list.WriteString(properties.ReplyToGroupId);
        list.Complete();
    }

    private static AmqpHeader ReadHeaderBody(ref AmqpReader reader)
    {
        AmqpReader fields = reader.ReadList(out int count);

        return new AmqpHeader
        {
            Durable = count > 0 && fields.ReadBoolean(false),
            Priority = (count > 1 ? fields.ReadNullableUByte() : null) ?? 4,
            TimeToLive = count > 2 ? fields.ReadNullableUInt() : null,
            FirstAcquirer = count > 3 && fields.ReadBoolean(false),
            DeliveryCount = (count > 4 ? fields.ReadNullableUInt() : null) ?? 0u
        };
    }

    private static AmqpProperties ReadPropertiesBody(ref AmqpReader reader, ReadOnlyMemory<byte> payload)
    {
        AmqpReader fields = reader.ReadList(out int count);
        AmqpProperties properties = new();

        if (count > 0)
        {
            properties.MessageId = ReadMessageId(ref fields);
        }

        if (count > 1 && fields.ReadBinaryRange(out int offset, out int length))
        {
            properties.UserId = payload.Slice(offset, length);
        }

        properties.To = count > 2 ? fields.ReadAddress() : null;
        properties.Subject = count > 3 ? fields.ReadString() : null;
        properties.ReplyTo = count > 4 ? fields.ReadAddress() : null;

        if (count > 5)
        {
            properties.CorrelationId = ReadMessageId(ref fields);
        }

        properties.ContentType = count > 6 ? fields.ReadSymbol() : null;
        properties.ContentEncoding = count > 7 ? fields.ReadSymbol() : null;
        properties.AbsoluteExpiryTime = count > 8 ? fields.ReadNullableTimestamp() : null;
        properties.CreationTime = count > 9 ? fields.ReadNullableTimestamp() : null;
        properties.GroupId = count > 10 ? fields.ReadString() : null;
        properties.GroupSequence = count > 11 ? fields.ReadNullableUInt() : null;
        properties.ReplyToGroupId = count > 12 ? fields.ReadString() : null;

        return properties;
    }

    private static object? ReadMessageId(ref AmqpReader fields)
    {
        object? id = fields.ReadValue();

        // message-id-ulong, message-id-uuid, message-id-binary and message-id-string (Part 3, section 3.2.11).
        // Binary is matched by exact type: an array of byte decodes as sbyte[], which a byte[] pattern
        // accepts through CLR array covariance.
        if (id is not (null or ulong or Guid or string) && id.GetType() != typeof(byte[]))
        {
            throw AmqpException.DecodeError(ErrorMessages.MessageIdInvalid);
        }

        return id;
    }

    private static void Advance(ref int stage, int next)
    {
        if (next <= stage)
        {
            throw AmqpException.DecodeError(ErrorMessages.SectionOrderInvalid);
        }

        stage = next;
    }
}
