using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The numeric descriptors of the described types AMQP 1.0 defines, and the symbolic names they are
/// also known by (Part 1, section 1.5).
/// </summary>
/// <remarks>
/// Every type here lives in the domain reserved for the AMQP specification (<c>0x00000000</c>), so its
/// numeric descriptor is the type's code alone.
/// </remarks>
internal static class AmqpDescriptor
{
    public const ulong Open = 0x10;
    public const ulong Begin = 0x11;
    public const ulong Attach = 0x12;
    public const ulong Flow = 0x13;
    public const ulong Transfer = 0x14;
    public const ulong Disposition = 0x15;
    public const ulong Detach = 0x16;
    public const ulong End = 0x17;
    public const ulong Close = 0x18;
    public const ulong Error = 0x1d;
    public const ulong Received = 0x23;
    public const ulong Accepted = 0x24;
    public const ulong Rejected = 0x25;
    public const ulong Released = 0x26;
    public const ulong Modified = 0x27;
    public const ulong Source = 0x28;
    public const ulong Target = 0x29;
    public const ulong DeleteOnClose = 0x2b;
    public const ulong DeleteOnNoLinks = 0x2c;
    public const ulong DeleteOnNoMessages = 0x2d;
    public const ulong DeleteOnNoLinksOrMessages = 0x2e;
    public const ulong Coordinator = 0x30;
    public const ulong Declare = 0x31;
    public const ulong Discharge = 0x32;
    public const ulong Declared = 0x33;
    public const ulong TransactionalState = 0x34;
    public const ulong SaslMechanisms = 0x40;
    public const ulong SaslInit = 0x41;
    public const ulong SaslChallenge = 0x42;
    public const ulong SaslResponse = 0x43;
    public const ulong SaslOutcome = 0x44;
    public const ulong Header = 0x70;
    public const ulong DeliveryAnnotations = 0x71;
    public const ulong MessageAnnotations = 0x72;
    public const ulong Properties = 0x73;
    public const ulong ApplicationProperties = 0x74;
    public const ulong Data = 0x75;
    public const ulong AmqpSequence = 0x76;
    public const ulong AmqpValue = 0x77;
    public const ulong Footer = 0x78;

    private static readonly FrozenDictionary<string, ulong> _codesByName = new Dictionary<string, ulong>(StringComparer.Ordinal)
    {
        ["amqp:open:list"] = Open,
        ["amqp:begin:list"] = Begin,
        ["amqp:attach:list"] = Attach,
        ["amqp:flow:list"] = Flow,
        ["amqp:transfer:list"] = Transfer,
        ["amqp:disposition:list"] = Disposition,
        ["amqp:detach:list"] = Detach,
        ["amqp:end:list"] = End,
        ["amqp:close:list"] = Close,
        ["amqp:error:list"] = Error,
        ["amqp:received:list"] = Received,
        ["amqp:accepted:list"] = Accepted,
        ["amqp:rejected:list"] = Rejected,
        ["amqp:released:list"] = Released,
        ["amqp:modified:list"] = Modified,
        ["amqp:source:list"] = Source,
        ["amqp:target:list"] = Target,
        ["amqp:delete-on-close:list"] = DeleteOnClose,
        ["amqp:delete-on-no-links:list"] = DeleteOnNoLinks,
        ["amqp:delete-on-no-messages:list"] = DeleteOnNoMessages,
        ["amqp:delete-on-no-links-or-messages:list"] = DeleteOnNoLinksOrMessages,
        ["amqp:coordinator:list"] = Coordinator,
        ["amqp:declare:list"] = Declare,
        ["amqp:discharge:list"] = Discharge,
        ["amqp:declared:list"] = Declared,
        ["amqp:transactional-state:list"] = TransactionalState,
        ["amqp:sasl-mechanisms:list"] = SaslMechanisms,
        ["amqp:sasl-init:list"] = SaslInit,
        ["amqp:sasl-challenge:list"] = SaslChallenge,
        ["amqp:sasl-response:list"] = SaslResponse,
        ["amqp:sasl-outcome:list"] = SaslOutcome,
        ["amqp:header:list"] = Header,
        ["amqp:delivery-annotations:map"] = DeliveryAnnotations,
        ["amqp:message-annotations:map"] = MessageAnnotations,
        ["amqp:properties:list"] = Properties,
        ["amqp:application-properties:map"] = ApplicationProperties,
        ["amqp:data:binary"] = Data,
        ["amqp:amqp-sequence:list"] = AmqpSequence,
        ["amqp:amqp-value:*"] = AmqpValue,
        ["amqp:footer:map"] = Footer
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Finds the numeric descriptor a symbolic descriptor names.
    /// </summary>
    /// <param name="name">The symbolic descriptor.</param>
    /// <param name="code">The numeric descriptor, when the name is one AMQP 1.0 defines.</param>
    /// <returns><see langword="true"/> when the name is one AMQP 1.0 defines; otherwise <see langword="false"/>.</returns>
    public static bool TryGetCode(string name, out ulong code)
    {
        return _codesByName.TryGetValue(name, out code);
    }
}
