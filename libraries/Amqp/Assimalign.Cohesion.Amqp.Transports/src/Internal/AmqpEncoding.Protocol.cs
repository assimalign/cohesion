using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Amqp.Transports.Internal;

internal static partial class AmqpEncoding
{
    internal const ulong OpenDescriptor = 0x10ul;
    internal const ulong BeginDescriptor = 0x11ul;
    internal const ulong AttachDescriptor = 0x12ul;
    internal const ulong FlowDescriptor = 0x13ul;
    internal const ulong TransferDescriptor = 0x14ul;
    internal const ulong DispositionDescriptor = 0x15ul;
    internal const ulong DetachDescriptor = 0x16ul;
    internal const ulong EndDescriptor = 0x17ul;
    internal const ulong CloseDescriptor = 0x18ul;
    internal const ulong ErrorDescriptor = 0x1dul;
    internal const ulong SourceDescriptor = 0x28ul;
    internal const ulong TargetDescriptor = 0x29ul;
    internal const ulong SaslMechanismsDescriptor = 0x40ul;
    internal const ulong SaslInitDescriptor = 0x41ul;
    internal const ulong SaslChallengeDescriptor = 0x42ul;
    internal const ulong SaslResponseDescriptor = 0x43ul;
    internal const ulong SaslOutcomeDescriptor = 0x44ul;
    internal const ulong HeaderDescriptor = 0x70ul;
    internal const ulong DeliveryAnnotationsDescriptor = 0x71ul;
    internal const ulong MessageAnnotationsDescriptor = 0x72ul;
    internal const ulong PropertiesDescriptor = 0x73ul;
    internal const ulong ApplicationPropertiesDescriptor = 0x74ul;
    internal const ulong DataDescriptor = 0x75ul;
    internal const ulong SequenceDescriptor = 0x76ul;
    internal const ulong ValueDescriptor = 0x77ul;
    internal const ulong FooterDescriptor = 0x78ul;

    internal static byte[] EncodeProtocolHeader(AmqpProtocolHeader header)
    {
        return new byte[]
        {
            (byte) 'A',
            (byte) 'M',
            (byte) 'Q',
            (byte) 'P',
            (byte) header.ProtocolId,
            header.Major,
            header.Minor,
            header.Revision
        };
    }

    internal static AmqpProtocolHeader DecodeProtocolHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 8 ||
            bytes[0] != (byte) 'A' ||
            bytes[1] != (byte) 'M' ||
            bytes[2] != (byte) 'Q' ||
            bytes[3] != (byte) 'P')
        {
            throw new AmqpProtocolException("The AMQP protocol header is invalid.");
        }

        return new AmqpProtocolHeader((AmqpProtocolId) bytes[4], bytes[5], bytes[6], bytes[7]);
    }

    internal static byte[] EncodeFrame(AmqpFrame frame, AmqpFrameType frameType, uint maxFrameSize)
    {
        ArrayBufferWriter<byte> bodyWriter = new();

        WritePerformative(bodyWriter, frame.Performative);

        if (!frame.Payload.IsEmpty)
        {
            WriteBytes(bodyWriter, frame.Payload.Span);
        }

        uint size = (uint) (8 + bodyWriter.WrittenCount);

        if (size > maxFrameSize)
        {
            throw new AmqpProtocolException($"The encoded AMQP frame size '{size}' exceeds the configured maximum frame size '{maxFrameSize}'.");
        }

        ArrayBufferWriter<byte> frameWriter = new();
        WriteUInt32(frameWriter, size);
        WriteByte(frameWriter, 2);
        WriteByte(frameWriter, (byte) frameType);
        WriteUInt16(frameWriter, frame.Channel);
        WriteBytes(frameWriter, bodyWriter.WrittenSpan);

        return frameWriter.WrittenSpan.ToArray();
    }

    internal static AmqpFrame DecodeFrame(ReadOnlySpan<byte> bytes, AmqpFrameType expectedFrameType)
    {
        if (bytes.Length < 8)
        {
            throw new AmqpProtocolException("The AMQP frame is smaller than the minimum frame header size.");
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        byte doff = bytes[4];
        byte frameType = bytes[5];
        ushort channel = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        int offset = doff * 4;

        if (size != bytes.Length)
        {
            throw new AmqpProtocolException("The AMQP frame size does not match the supplied payload.");
        }

        if (offset < 8 || offset > bytes.Length)
        {
            throw new AmqpProtocolException("The AMQP frame data offset is invalid.");
        }

        if (frameType != (byte) expectedFrameType)
        {
            throw new AmqpProtocolException("The AMQP frame type does not match the current protocol phase.");
        }

        AmqpBufferReader reader = new(bytes[offset..]);

        if (reader.End)
        {
            throw new AmqpProtocolException("The AMQP frame does not contain a performative.");
        }

        AmqpPerformative performative = ReadPerformative(ref reader, expectedFrameType);
        byte[] payload = reader.UnreadSpan.ToArray();

        return new AmqpFrame(channel, performative, payload);
    }

    internal static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, AmqpFrameType frameType, out AmqpFrame frame)
    {
        frame = null!;

        if (buffer.Length < 8)
        {
            return false;
        }

        Span<byte> prefix = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(prefix);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(prefix);

        if (size < 8)
        {
            throw new AmqpProtocolException("The AMQP frame size is invalid.");
        }

        if (buffer.Length < size)
        {
            return false;
        }

        byte[] frameBytes = buffer.Slice(0, size).ToArray();
        frame = DecodeFrame(frameBytes, frameType);
        buffer = buffer.Slice(size);

        return true;
    }

    internal static byte[] EncodeMessage(AmqpMessage message)
    {
        ArrayBufferWriter<byte> writer = new();

        if (message.Durable.HasValue ||
            message.Priority.HasValue ||
            message.TimeToLive.HasValue ||
            message.FirstAcquirer.HasValue ||
            message.DeliveryCount.HasValue)
        {
            WriteDescribedList(writer, HeaderDescriptor, new object?[]
            {
                message.Durable,
                message.Priority,
                message.TimeToLive,
                message.FirstAcquirer,
                message.DeliveryCount
            });
        }

        if (message.DeliveryAnnotations is not null)
        {
            WriteDescribedValue(writer, DeliveryAnnotationsDescriptor, message.DeliveryAnnotations);
        }

        if (message.MessageAnnotations is not null)
        {
            WriteDescribedValue(writer, MessageAnnotationsDescriptor, message.MessageAnnotations);
        }

        if (HasProperties(message))
        {
            WriteDescribedList(writer, PropertiesDescriptor, new object?[]
            {
                message.MessageId,
                message.UserId,
                message.To,
                message.Subject,
                message.ReplyTo,
                message.CorrelationId,
                message.ContentType,
                message.ContentEncoding,
                message.AbsoluteExpiryTime,
                message.CreationTime,
                message.GroupId,
                message.GroupSequence,
                message.ReplyToGroupId
            });
        }

        if (message.ApplicationProperties is not null)
        {
            WriteDescribedValue(writer, ApplicationPropertiesDescriptor, message.ApplicationProperties);
        }

        if (message.DataSections is not null)
        {
            foreach (ReadOnlyMemory<byte> dataSection in message.DataSections)
            {
                WriteDescribedValue(writer, DataDescriptor, dataSection);
            }
        }

        if (message.SequenceSections is not null)
        {
            foreach (IReadOnlyList<object?> sequenceSection in message.SequenceSections)
            {
                WriteDescribedValue(writer, SequenceDescriptor, sequenceSection);
            }
        }

        if (message.Value is not null)
        {
            WriteDescribedValue(writer, ValueDescriptor, message.Value);
        }

        if (message.Footer is not null)
        {
            WriteDescribedValue(writer, FooterDescriptor, message.Footer);
        }

        return writer.WrittenSpan.ToArray();
    }

    internal static AmqpMessage DecodeMessage(ReadOnlySpan<byte> bytes)
    {
        AmqpBufferReader reader = new(bytes);
        AmqpMessage message = new();
        List<ReadOnlyMemory<byte>>? dataSections = null;
        List<IReadOnlyList<object?>>? sequenceSections = null;

        while (!reader.End)
        {
            if (reader.ReadByte() != 0x00)
            {
                throw new AmqpProtocolException("AMQP message sections must be encoded as described values.");
            }

            (object descriptor, object? value) = ReadDescribedRaw(ref reader);

            if (descriptor is not ulong descriptorCode)
            {
                throw new AmqpProtocolException("AMQP message section descriptors must be numeric.");
            }

            switch (descriptorCode)
            {
                case HeaderDescriptor:
                    ApplyHeader(message, GetList(value));
                    break;
                case DeliveryAnnotationsDescriptor:
                    message.DeliveryAnnotations = AsSymbolMap(value);
                    break;
                case MessageAnnotationsDescriptor:
                    message.MessageAnnotations = AsSymbolMap(value);
                    break;
                case PropertiesDescriptor:
                    ApplyProperties(message, GetList(value));
                    break;
                case ApplicationPropertiesDescriptor:
                    message.ApplicationProperties = AsStringMap(value);
                    break;
                case DataDescriptor:
                    dataSections ??= new List<ReadOnlyMemory<byte>>();
                    dataSections.Add(AsBinary(value));
                    break;
                case SequenceDescriptor:
                    sequenceSections ??= new List<IReadOnlyList<object?>>();
                    sequenceSections.Add(GetList(value));
                    break;
                case ValueDescriptor:
                    message.Value = value;
                    break;
                case FooterDescriptor:
                    message.Footer = AsSymbolMap(value);
                    break;
                default:
                    throw new AmqpProtocolException($"The AMQP message section descriptor '0x{descriptorCode:x}' is not supported.");
            }
        }

        message.DataSections = dataSections;
        message.SequenceSections = sequenceSections;

        return message;
    }

    private static void WritePerformative(ArrayBufferWriter<byte> writer, AmqpPerformative performative)
    {
        switch (performative)
        {
            case AmqpOpenPerformative open:
                WriteDescribedList(writer, OpenDescriptor, new object?[] { open.ContainerId, open.HostName, open.MaxFrameSize, open.ChannelMax, open.IdleTimeOut, open.OutgoingLocales, open.IncomingLocales, open.OfferedCapabilities, open.DesiredCapabilities, open.Properties });
                return;
            case AmqpBeginPerformative begin:
                WriteDescribedList(writer, BeginDescriptor, new object?[] { begin.RemoteChannel, begin.NextOutgoingId, begin.IncomingWindow, begin.OutgoingWindow, begin.HandleMax, begin.OfferedCapabilities, begin.DesiredCapabilities, begin.Properties });
                return;
            case AmqpAttachPerformative attach:
                WriteDescribedList(writer, AttachDescriptor, new object?[] { attach.Name, attach.Handle, attach.Role, attach.SenderSettleMode.HasValue ? (byte) attach.SenderSettleMode.Value : null, attach.ReceiverSettleMode.HasValue ? (byte) attach.ReceiverSettleMode.Value : null, attach.Source, attach.Target, attach.Unsettled is null ? null : ToObjectMap(attach.Unsettled), attach.IncompleteUnsettled, attach.InitialDeliveryCount, attach.MaxMessageSize, attach.OfferedCapabilities, attach.DesiredCapabilities, attach.Properties });
                return;
            case AmqpFlowPerformative flow:
                WriteDescribedList(writer, FlowDescriptor, new object?[] { flow.NextIncomingId, flow.IncomingWindow, flow.NextOutgoingId, flow.OutgoingWindow, flow.Handle, flow.DeliveryCount, flow.LinkCredit, flow.Available, flow.Drain, flow.Echo, flow.Properties });
                return;
            case AmqpTransferPerformative transfer:
                WriteDescribedList(writer, TransferDescriptor, new object?[] { transfer.Handle, transfer.DeliveryId, transfer.DeliveryTag, transfer.MessageFormat, transfer.Settled, transfer.More, transfer.ReceiverSettleMode.HasValue ? (byte) transfer.ReceiverSettleMode.Value : null, transfer.State, transfer.Resume, transfer.Aborted, transfer.Batchable });
                return;
            case AmqpDispositionPerformative disposition:
                WriteDescribedList(writer, DispositionDescriptor, new object?[] { disposition.Role, disposition.First, disposition.Last, disposition.Settled, disposition.State, disposition.Batchable });
                return;
            case AmqpDetachPerformative detach:
                WriteDescribedList(writer, DetachDescriptor, new object?[] { detach.Handle, detach.Closed, detach.Error });
                return;
            case AmqpEndPerformative end:
                WriteDescribedList(writer, EndDescriptor, new object?[] { end.Error });
                return;
            case AmqpClosePerformative close:
                WriteDescribedList(writer, CloseDescriptor, new object?[] { close.Error });
                return;
            case AmqpSaslMechanismsPerformative saslMechanisms:
                WriteDescribedList(writer, SaslMechanismsDescriptor, new object?[] { saslMechanisms.SaslServerMechanisms });
                return;
            case AmqpSaslInitPerformative saslInit:
                WriteDescribedList(writer, SaslInitDescriptor, new object?[] { saslInit.Mechanism, saslInit.InitialResponse, saslInit.HostName });
                return;
            case AmqpSaslChallengePerformative saslChallenge:
                WriteDescribedList(writer, SaslChallengeDescriptor, new object?[] { saslChallenge.Challenge });
                return;
            case AmqpSaslResponsePerformative saslResponse:
                WriteDescribedList(writer, SaslResponseDescriptor, new object?[] { saslResponse.Response });
                return;
            case AmqpSaslOutcomePerformative saslOutcome:
                WriteDescribedList(writer, SaslOutcomeDescriptor, new object?[] { (byte) saslOutcome.Code, saslOutcome.AdditionalData });
                return;
            default:
                throw new AmqpProtocolException($"The AMQP performative type '{performative.GetType().FullName}' is not supported.");
        }
    }

    private static AmqpPerformative ReadPerformative(ref AmqpBufferReader reader, AmqpFrameType frameType)
    {
        if (reader.ReadByte() != 0x00)
        {
            throw new AmqpProtocolException("AMQP performatives must be encoded as described values.");
        }

        (object descriptor, object? value) = ReadDescribedRaw(ref reader);
        object?[] fields = GetList(value);

        if (descriptor is not ulong descriptorCode)
        {
            throw new AmqpProtocolException("AMQP performative descriptors must be numeric.");
        }

        return descriptorCode switch
        {
            OpenDescriptor when frameType == AmqpFrameType.Amqp => new AmqpOpenPerformative { ContainerId = AsString(GetField(fields, 0)) ?? string.Empty, HostName = AsString(GetField(fields, 1)), MaxFrameSize = AsUIntNullable(GetField(fields, 2)), ChannelMax = AsUShortNullable(GetField(fields, 3)), IdleTimeOut = AsUIntNullable(GetField(fields, 4)), OutgoingLocales = AsSymbolList(GetField(fields, 5)), IncomingLocales = AsSymbolList(GetField(fields, 6)), OfferedCapabilities = AsSymbolList(GetField(fields, 7)), DesiredCapabilities = AsSymbolList(GetField(fields, 8)), Properties = AsSymbolMap(GetField(fields, 9)) },
            BeginDescriptor when frameType == AmqpFrameType.Amqp => new AmqpBeginPerformative { RemoteChannel = AsUShortNullable(GetField(fields, 0)), NextOutgoingId = AsUInt(GetField(fields, 1)), IncomingWindow = AsUInt(GetField(fields, 2)), OutgoingWindow = AsUInt(GetField(fields, 3)), HandleMax = AsUIntNullable(GetField(fields, 4)), OfferedCapabilities = AsSymbolList(GetField(fields, 5)), DesiredCapabilities = AsSymbolList(GetField(fields, 6)), Properties = AsSymbolMap(GetField(fields, 7)) },
            AttachDescriptor when frameType == AmqpFrameType.Amqp => new AmqpAttachPerformative { Name = AsString(GetField(fields, 0)) ?? string.Empty, Handle = AsUInt(GetField(fields, 1)), Role = AsBool(GetField(fields, 2)), SenderSettleMode = AsByteNullable(GetField(fields, 3)) is byte senderMode ? (AmqpSenderSettleMode) senderMode : null, ReceiverSettleMode = AsByteNullable(GetField(fields, 4)) is byte receiverMode ? (AmqpReceiverSettleMode) receiverMode : null, Source = AsSource(GetField(fields, 5)), Target = AsTarget(GetField(fields, 6)), Unsettled = AsUnsettledMap(GetField(fields, 7)), IncompleteUnsettled = AsBoolNullable(GetField(fields, 8)), InitialDeliveryCount = AsUIntNullable(GetField(fields, 9)), MaxMessageSize = AsULongNullable(GetField(fields, 10)), OfferedCapabilities = AsSymbolList(GetField(fields, 11)), DesiredCapabilities = AsSymbolList(GetField(fields, 12)), Properties = AsSymbolMap(GetField(fields, 13)) },
            FlowDescriptor when frameType == AmqpFrameType.Amqp => new AmqpFlowPerformative { NextIncomingId = AsUIntNullable(GetField(fields, 0)), IncomingWindow = AsUInt(GetField(fields, 1)), NextOutgoingId = AsUInt(GetField(fields, 2)), OutgoingWindow = AsUInt(GetField(fields, 3)), Handle = AsUIntNullable(GetField(fields, 4)), DeliveryCount = AsUIntNullable(GetField(fields, 5)), LinkCredit = AsUIntNullable(GetField(fields, 6)), Available = AsUIntNullable(GetField(fields, 7)), Drain = AsBoolNullable(GetField(fields, 8)), Echo = AsBoolNullable(GetField(fields, 9)), Properties = AsSymbolMap(GetField(fields, 10)) },
            TransferDescriptor when frameType == AmqpFrameType.Amqp => new AmqpTransferPerformative { Handle = AsUInt(GetField(fields, 0)), DeliveryId = AsUIntNullable(GetField(fields, 1)), DeliveryTag = AsBinaryNullable(GetField(fields, 2)), MessageFormat = AsUIntNullable(GetField(fields, 3)), Settled = AsBoolNullable(GetField(fields, 4)), More = AsBoolNullable(GetField(fields, 5)), ReceiverSettleMode = AsByteNullable(GetField(fields, 6)) is byte receiverSettleMode ? (AmqpReceiverSettleMode) receiverSettleMode : null, State = AsDescribedValue(GetField(fields, 7)), Resume = AsBoolNullable(GetField(fields, 8)), Aborted = AsBoolNullable(GetField(fields, 9)), Batchable = AsBoolNullable(GetField(fields, 10)) },
            DispositionDescriptor when frameType == AmqpFrameType.Amqp => new AmqpDispositionPerformative { Role = AsBool(GetField(fields, 0)), First = AsUInt(GetField(fields, 1)), Last = AsUIntNullable(GetField(fields, 2)), Settled = AsBoolNullable(GetField(fields, 3)), State = AsDescribedValue(GetField(fields, 4)), Batchable = AsBoolNullable(GetField(fields, 5)) },
            DetachDescriptor when frameType == AmqpFrameType.Amqp => new AmqpDetachPerformative { Handle = AsUInt(GetField(fields, 0)), Closed = AsBoolNullable(GetField(fields, 1)), Error = AsError(GetField(fields, 2)) },
            EndDescriptor when frameType == AmqpFrameType.Amqp => new AmqpEndPerformative { Error = AsError(GetField(fields, 0)) },
            CloseDescriptor when frameType == AmqpFrameType.Amqp => new AmqpClosePerformative { Error = AsError(GetField(fields, 0)) },
            SaslMechanismsDescriptor when frameType == AmqpFrameType.Sasl => new AmqpSaslMechanismsPerformative { SaslServerMechanisms = AsSymbolList(GetField(fields, 0)) ?? Array.Empty<AmqpSymbol>() },
            SaslInitDescriptor when frameType == AmqpFrameType.Sasl => new AmqpSaslInitPerformative { Mechanism = AsSymbol(GetField(fields, 0)), InitialResponse = AsBinaryNullable(GetField(fields, 1)), HostName = AsString(GetField(fields, 2)) },
            SaslChallengeDescriptor when frameType == AmqpFrameType.Sasl => new AmqpSaslChallengePerformative { Challenge = AsBinary(GetField(fields, 0)) },
            SaslResponseDescriptor when frameType == AmqpFrameType.Sasl => new AmqpSaslResponsePerformative { Response = AsBinary(GetField(fields, 0)) },
            SaslOutcomeDescriptor when frameType == AmqpFrameType.Sasl => new AmqpSaslOutcomePerformative { Code = (AmqpSaslCode) AsByte(GetField(fields, 0)), AdditionalData = AsBinaryNullable(GetField(fields, 1)) },
            _ => throw new AmqpProtocolException($"The AMQP performative descriptor '0x{descriptorCode:x}' is not valid for the current protocol phase.")
        };
    }

    private static void ApplyHeader(AmqpMessage message, object?[] fields)
    {
        message.Durable = AsBoolNullable(GetField(fields, 0));
        message.Priority = AsByteNullable(GetField(fields, 1));
        message.TimeToLive = AsUIntNullable(GetField(fields, 2));
        message.FirstAcquirer = AsBoolNullable(GetField(fields, 3));
        message.DeliveryCount = AsUIntNullable(GetField(fields, 4));
    }

    private static void ApplyProperties(AmqpMessage message, object?[] fields)
    {
        message.MessageId = GetField(fields, 0);
        message.UserId = AsBinaryNullable(GetField(fields, 1));
        message.To = AsString(GetField(fields, 2));
        message.Subject = AsString(GetField(fields, 3));
        message.ReplyTo = AsString(GetField(fields, 4));
        message.CorrelationId = GetField(fields, 5);
        message.ContentType = AsSymbolNullable(GetField(fields, 6));
        message.ContentEncoding = AsSymbolNullable(GetField(fields, 7));
        message.AbsoluteExpiryTime = AsTimestampNullable(GetField(fields, 8));
        message.CreationTime = AsTimestampNullable(GetField(fields, 9));
        message.GroupId = AsString(GetField(fields, 10));
        message.GroupSequence = AsUIntNullable(GetField(fields, 11));
        message.ReplyToGroupId = AsString(GetField(fields, 12));
    }

    private static bool HasProperties(AmqpMessage message)
    {
        return message.MessageId is not null ||
            message.UserId.HasValue ||
            message.To is not null ||
            message.Subject is not null ||
            message.ReplyTo is not null ||
            message.CorrelationId is not null ||
            message.ContentType.HasValue ||
            message.ContentEncoding.HasValue ||
            message.AbsoluteExpiryTime.HasValue ||
            message.CreationTime.HasValue ||
            message.GroupId is not null ||
            message.GroupSequence.HasValue ||
            message.ReplyToGroupId is not null;
    }

    private static object? GetField(object?[] fields, int index) => index < fields.Length ? fields[index] : null;
    private static object?[] GetList(object? value) => value as object?[] ?? throw new AmqpProtocolException("The AMQP value was expected to be encoded as a list.");
    private static string? AsString(object? value) => value as string;
    private static bool AsBool(object? value) => AsBoolNullable(value) ?? throw new AmqpProtocolException("The AMQP value was expected to be a boolean.");
    private static bool? AsBoolNullable(object? value) => value is bool boolean ? boolean : null;
    private static byte AsByte(object? value) => AsByteNullable(value) ?? throw new AmqpProtocolException("The AMQP value was expected to be a byte.");
    private static byte? AsByteNullable(object? value) => value switch { byte byteValue => byteValue, sbyte signedByteValue => unchecked((byte) signedByteValue), _ => null };
    private static ushort? AsUShortNullable(object? value) => value switch { ushort ushortValue => ushortValue, uint uintValue when uintValue <= ushort.MaxValue => (ushort) uintValue, _ => null };
    private static uint AsUInt(object? value) => AsUIntNullable(value) ?? throw new AmqpProtocolException("The AMQP value was expected to be an unsigned integer.");
    private static uint? AsUIntNullable(object? value) => value switch { uint uintValue => uintValue, int intValue when intValue >= 0 => unchecked((uint) intValue), byte byteValue => byteValue, ushort ushortValue => ushortValue, _ => null };
    private static ulong? AsULongNullable(object? value) => value switch { ulong ulongValue => ulongValue, long longValue when longValue >= 0 => unchecked((ulong) longValue), uint uintValue => uintValue, _ => null };
    private static DateTimeOffset? AsTimestampNullable(object? value) => value is DateTimeOffset timestamp ? timestamp : default(DateTimeOffset?);
    private static AmqpSymbol AsSymbol(object? value) => AsSymbolNullable(value) ?? throw new AmqpProtocolException("The AMQP value was expected to be a symbol.");
    private static AmqpSymbol? AsSymbolNullable(object? value) => value is AmqpSymbol symbol ? symbol : default(AmqpSymbol?);

    private static IReadOnlyList<AmqpSymbol>? AsSymbolList(object? value)
    {
        return value switch
        {
            null => null,
            AmqpSymbol[] symbols => symbols,
            object?[] objects => Array.ConvertAll(objects, static item => item is AmqpSymbol symbol ? symbol : throw new AmqpProtocolException("The AMQP array element was expected to be a symbol.")),
            _ => throw new AmqpProtocolException("The AMQP value was expected to be a symbol array.")
        };
    }

    private static ReadOnlyMemory<byte> AsBinary(object? value) => AsBinaryNullable(value) ?? throw new AmqpProtocolException("The AMQP value was expected to be binary.");
    private static ReadOnlyMemory<byte>? AsBinaryNullable(object? value) => value switch { byte[] byteArray => byteArray, ReadOnlyMemory<byte> memory => memory, _ => null };
    private static AmqpDescribedValue? AsDescribedValue(object? value) => value as AmqpDescribedValue;
    private static AmqpSource? AsSource(object? value) => value as AmqpSource;
    private static AmqpTarget? AsTarget(object? value) => value as AmqpTarget;
    private static AmqpError? AsError(object? value) => value as AmqpError;

    private static IReadOnlyDictionary<AmqpSymbol, object?>? AsSymbolMap(object? value)
    {
        if (value is null)
        {
            return null;
        }

        Dictionary<object, object?> map = value as Dictionary<object, object?> ?? throw new AmqpProtocolException("The AMQP value was expected to be a map.");
        Dictionary<AmqpSymbol, object?> converted = new(map.Count);

        foreach ((object key, object? mapValue) in map)
        {
            converted[AsSymbol(key)] = mapValue;
        }

        return converted;
    }

    private static IReadOnlyDictionary<string, object?>? AsStringMap(object? value)
    {
        if (value is null)
        {
            return null;
        }

        Dictionary<object, object?> map = value as Dictionary<object, object?> ?? throw new AmqpProtocolException("The AMQP value was expected to be a map.");
        Dictionary<string, object?> converted = new(map.Count);

        foreach ((object key, object? mapValue) in map)
        {
            if (key is not string stringKey)
            {
                throw new AmqpProtocolException("The AMQP application-properties map requires string keys.");
            }

            converted[stringKey] = mapValue;
        }

        return converted;
    }

    private static IReadOnlyDictionary<ReadOnlyMemory<byte>, AmqpDescribedValue?>? AsUnsettledMap(object? value)
    {
        if (value is null)
        {
            return null;
        }

        Dictionary<object, object?> map = value as Dictionary<object, object?> ?? throw new AmqpProtocolException("The AMQP unsettled map was expected to be a map.");
        Dictionary<ReadOnlyMemory<byte>, AmqpDescribedValue?> converted = new(map.Count);

        foreach ((object key, object? unsettledValue) in map)
        {
            converted[AsBinary(key)] = AsDescribedValue(unsettledValue);
        }

        return converted;
    }

    private static Dictionary<object, object?> ToObjectMap(IReadOnlyDictionary<ReadOnlyMemory<byte>, AmqpDescribedValue?> values)
    {
        Dictionary<object, object?> map = new(values.Count);

        foreach ((ReadOnlyMemory<byte> key, AmqpDescribedValue? value) in values)
        {
            map[key] = value;
        }

        return map;
    }

    private static AmqpError ReadError(object? value)
    {
        object?[] fields = GetList(value);

        return new AmqpError
        {
            Condition = AsSymbol(GetField(fields, 0)),
            Description = AsString(GetField(fields, 1)),
            Info = AsSymbolMap(GetField(fields, 2))
        };
    }

    private static AmqpSource ReadSource(object? value)
    {
        object?[] fields = GetList(value);

        return new AmqpSource
        {
            Address = AsString(GetField(fields, 0)),
            Durable = AsUIntNullable(GetField(fields, 1)),
            ExpiryPolicy = AsSymbolNullable(GetField(fields, 2)),
            Timeout = AsUIntNullable(GetField(fields, 3)),
            Dynamic = AsBoolNullable(GetField(fields, 4)),
            DynamicNodeProperties = AsSymbolMap(GetField(fields, 5)),
            DistributionMode = AsSymbolNullable(GetField(fields, 6)),
            Filter = AsSymbolMap(GetField(fields, 7)),
            DefaultOutcome = AsDescribedValue(GetField(fields, 8)),
            Outcomes = AsSymbolList(GetField(fields, 9)),
            Capabilities = AsSymbolList(GetField(fields, 10))
        };
    }

    private static AmqpTarget ReadTarget(object? value)
    {
        object?[] fields = GetList(value);

        return new AmqpTarget
        {
            Address = AsString(GetField(fields, 0)),
            Durable = AsUIntNullable(GetField(fields, 1)),
            ExpiryPolicy = AsSymbolNullable(GetField(fields, 2)),
            Timeout = AsUIntNullable(GetField(fields, 3)),
            Dynamic = AsBoolNullable(GetField(fields, 4)),
            DynamicNodeProperties = AsSymbolMap(GetField(fields, 5)),
            Capabilities = AsSymbolList(GetField(fields, 6))
        };
    }
}
