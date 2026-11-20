using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>begin</c> performative: maps a session onto a channel (AMQP 1.0 Part 2, section 2.7.2).
/// </summary>
internal sealed class AmqpBeginPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.Begin;

    /// <summary>
    /// Gets or sets the channel of the session this one answers, or <see langword="null"/> when it begins a new one.
    /// </summary>
    public ushort? RemoteChannel { get; set; }

    public uint NextOutgoingId { get; set; }

    public uint IncomingWindow { get; set; }

    public uint OutgoingWindow { get; set; }

    public uint HandleMax { get; set; } = uint.MaxValue;

    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteUShort(RemoteChannel);
        list.WriteUInt(NextOutgoingId);
        list.WriteUInt(IncomingWindow);
        list.WriteUInt(OutgoingWindow);
        list.WriteUInt(HandleMax, uint.MaxValue);
        list.WriteSymbolMultiple(OfferedCapabilities);
        list.WriteSymbolMultiple(DesiredCapabilities);
        list.WriteSymbolMap(Properties);
        list.Complete();
    }

    public static AmqpBeginPerformative Decode(ref AmqpReader fields, int count)
    {
        ushort? remoteChannel = count > 0 ? fields.ReadNullableUShort() : null;
        uint? nextOutgoingId = count > 1 ? fields.ReadNullableUInt() : null;
        uint? incomingWindow = count > 2 ? fields.ReadNullableUInt() : null;
        uint? outgoingWindow = count > 3 ? fields.ReadNullableUInt() : null;

        return new AmqpBeginPerformative
        {
            RemoteChannel = remoteChannel,
            NextOutgoingId = nextOutgoingId ?? throw MandatoryFieldMissing("begin", "next-outgoing-id"),
            IncomingWindow = incomingWindow ?? throw MandatoryFieldMissing("begin", "incoming-window"),
            OutgoingWindow = outgoingWindow ?? throw MandatoryFieldMissing("begin", "outgoing-window"),
            HandleMax = (count > 4 ? fields.ReadNullableUInt() : null) ?? uint.MaxValue,
            OfferedCapabilities = count > 5 ? fields.ReadSymbolMultiple() : null,
            DesiredCapabilities = count > 6 ? fields.ReadSymbolMultiple() : null,
            Properties = count > 7 ? fields.ReadSymbolMap() : null
        };
    }
}
