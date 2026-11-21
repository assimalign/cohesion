using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>attach</c> performative: attaches a link to a session (AMQP 1.0 Part 2, section 2.7.3).
/// </summary>
internal sealed class AmqpAttachPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.Attach;

    public string Name { get; set; } = string.Empty;

    public uint Handle { get; set; }

    public AmqpRole Role { get; set; }

    public AmqpSenderSettleMode SenderSettleMode { get; set; } = AmqpSenderSettleMode.Mixed;

    public AmqpReceiverSettleMode ReceiverSettleMode { get; set; } = AmqpReceiverSettleMode.First;

    public AmqpSource? Source { get; set; }

    public IAmqpTarget? Target { get; set; }

    public IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? Unsettled { get; set; }

    public bool IncompleteUnsettled { get; set; }

    public uint? InitialDeliveryCount { get; set; }

    /// <summary>
    /// Gets or sets the largest message the endpoint accepts, or <see langword="null"/> for no limit.
    /// </summary>
    public ulong? MaxMessageSize { get; set; }

    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteString(Name);
        list.WriteUInt(Handle);
        list.WriteBoolean(Role == AmqpRole.Receiver);
        list.WriteUByte((byte)SenderSettleMode, (byte)AmqpSenderSettleMode.Mixed);
        list.WriteUByte((byte)ReceiverSettleMode, (byte)AmqpReceiverSettleMode.First);
        AmqpTypeCodec.WriteSource(writer, Source);
        list.Field(Source is null);
        AmqpTypeCodec.WriteTarget(writer, Target);
        list.Field(Target is null);
        AmqpTypeCodec.WriteUnsettled(writer, Unsettled);
        list.Field(Unsettled is null);
        list.WriteBoolean(IncompleteUnsettled, false);
        list.WriteUInt(InitialDeliveryCount);
        list.WriteULong(MaxMessageSize);
        list.WriteSymbolMultiple(OfferedCapabilities);
        list.WriteSymbolMultiple(DesiredCapabilities);
        list.WriteSymbolMap(Properties);
        list.Complete();
    }

    public static AmqpAttachPerformative Decode(ref AmqpReader fields, int count)
    {
        string? name = count > 0 ? fields.ReadString() : null;
        uint? handle = count > 1 ? fields.ReadNullableUInt() : null;
        bool? role = count > 2 ? fields.ReadNullableBoolean() : null;
        byte senderSettleMode = (count > 3 ? fields.ReadNullableUByte() : null) ?? (byte)AmqpSenderSettleMode.Mixed;
        byte receiverSettleMode = (count > 4 ? fields.ReadNullableUByte() : null) ?? (byte)AmqpReceiverSettleMode.First;

        if (senderSettleMode > (byte)AmqpSenderSettleMode.Mixed)
        {
            throw AmqpException.ProtocolViolation(AmqpErrorCondition.InvalidField, ErrorMessages.SenderSettleModeInvalid);
        }

        if (receiverSettleMode > (byte)AmqpReceiverSettleMode.Second)
        {
            throw AmqpException.ProtocolViolation(AmqpErrorCondition.InvalidField, ErrorMessages.ReceiverSettleModeInvalid);
        }

        AmqpAttachPerformative attach = new()
        {
            Name = name ?? throw MandatoryFieldMissing("attach", "name"),
            Handle = handle ?? throw MandatoryFieldMissing("attach", "handle"),
            Role = (role ?? throw MandatoryFieldMissing("attach", "role")) ? AmqpRole.Receiver : AmqpRole.Sender,
            SenderSettleMode = (AmqpSenderSettleMode)senderSettleMode,
            ReceiverSettleMode = (AmqpReceiverSettleMode)receiverSettleMode,
            Source = count > 5 ? AmqpTypeCodec.ReadSource(ref fields) : null,
            Target = count > 6 ? AmqpTypeCodec.ReadTarget(ref fields) : null,
            Unsettled = count > 7 ? AmqpTypeCodec.ReadUnsettled(ref fields) : null,
            IncompleteUnsettled = count > 8 && fields.ReadBoolean(false),
            InitialDeliveryCount = count > 9 ? fields.ReadNullableUInt() : null
        };

        // A zero or missing max-message-size both mean the endpoint sets no limit.
        ulong? maxMessageSize = count > 10 ? fields.ReadNullableULong() : null;

        attach.MaxMessageSize = maxMessageSize is 0ul ? null : maxMessageSize;
        attach.OfferedCapabilities = count > 11 ? fields.ReadSymbolMultiple() : null;
        attach.DesiredCapabilities = count > 12 ? fields.ReadSymbolMultiple() : null;
        attach.Properties = count > 13 ? fields.ReadFields() : null;

        return attach;
    }
}
