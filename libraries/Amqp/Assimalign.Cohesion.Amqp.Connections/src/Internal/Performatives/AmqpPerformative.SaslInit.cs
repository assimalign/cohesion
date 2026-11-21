using System;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>sasl-init</c> frame: the mechanism the client selected and its initial response (AMQP 1.0
/// Part 5, section 5.3.3.2).
/// </summary>
internal sealed class AmqpSaslInitPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.SaslInit;

    public AmqpSymbol Mechanism { get; set; }

    /// <summary>
    /// Gets or sets the initial response, or <see langword="null"/> when the client sent none.
    /// </summary>
    public ReadOnlyMemory<byte>? InitialResponse { get; set; }

    public string? HostName { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteSymbol(Mechanism);
        list.WriteBinary(InitialResponse);
        list.WriteString(HostName);
        list.Complete();
    }

    public static AmqpSaslInitPerformative Decode(ref AmqpReader fields, int count)
    {
        AmqpSymbol? mechanism = count > 0 ? fields.ReadSymbol() : null;
        AmqpSaslInitPerformative init = new()
        {
            Mechanism = mechanism ?? throw MandatoryFieldMissing("sasl-init", "mechanism")
        };

        if (count > 1 && fields.ReadBinary(out ReadOnlySpan<byte> response))
        {
            init.InitialResponse = response.ToArray();
        }

        init.HostName = count > 2 ? fields.ReadString() : null;
        return init;
    }
}
