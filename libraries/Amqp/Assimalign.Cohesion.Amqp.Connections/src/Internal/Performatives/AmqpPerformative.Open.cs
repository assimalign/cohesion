using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>open</c> performative: negotiates the connection's parameters (AMQP 1.0 Part 2, section 2.7.1).
/// </summary>
internal sealed class AmqpOpenPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.Open;

    public string ContainerId { get; set; } = string.Empty;

    public string? HostName { get; set; }

    public uint MaxFrameSize { get; set; } = uint.MaxValue;

    public ushort ChannelMax { get; set; } = ushort.MaxValue;

    /// <summary>
    /// Gets or sets the idle timeout in milliseconds, or <see langword="null"/> for none.
    /// </summary>
    public uint? IdleTimeout { get; set; }

    public IReadOnlyList<AmqpSymbol>? OutgoingLocales { get; set; }

    public IReadOnlyList<AmqpSymbol>? IncomingLocales { get; set; }

    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteString(ContainerId);
        list.WriteString(HostName);
        list.WriteUInt(MaxFrameSize, uint.MaxValue);
        list.WriteUShort(ChannelMax, ushort.MaxValue);
        list.WriteUInt(IdleTimeout);
        list.WriteSymbolMultiple(OutgoingLocales);
        list.WriteSymbolMultiple(IncomingLocales);
        list.WriteSymbolMultiple(OfferedCapabilities);
        list.WriteSymbolMultiple(DesiredCapabilities);
        list.WriteSymbolMap(Properties);
        list.Complete();
    }

    public static AmqpOpenPerformative Decode(ref AmqpReader fields, int count)
    {
        string? containerId = count > 0 ? fields.ReadString() : null;

        if (containerId is null)
        {
            throw MandatoryFieldMissing("open", "container-id");
        }

        return new AmqpOpenPerformative
        {
            ContainerId = containerId,
            HostName = count > 1 ? fields.ReadString() : null,
            MaxFrameSize = (count > 2 ? fields.ReadNullableUInt() : null) ?? uint.MaxValue,
            ChannelMax = (count > 3 ? fields.ReadNullableUShort() : null) ?? ushort.MaxValue,
            IdleTimeout = count > 4 ? fields.ReadNullableUInt() : null,
            OutgoingLocales = count > 5 ? fields.ReadSymbolMultiple() : null,
            IncomingLocales = count > 6 ? fields.ReadSymbolMultiple() : null,
            OfferedCapabilities = count > 7 ? fields.ReadSymbolMultiple() : null,
            DesiredCapabilities = count > 8 ? fields.ReadSymbolMultiple() : null,
            Properties = count > 9 ? fields.ReadFields() : null
        };
    }
}
