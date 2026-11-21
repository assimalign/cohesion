using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the target of a link: the terminus a link's messages are delivered to (AMQP 1.0 Part 2,
/// section 2.7.3, the <c>target</c> archetype).
/// </summary>
/// <remarks>
/// AMQP defines two targets: <see cref="AmqpTarget"/>, a messaging node (Part 3, section 3.5.4), and the
/// transaction coordinator (Part 4, section 4.5.1). The codec encodes the targets this library defines;
/// any other implementation is refused when a frame carrying it is encoded.
/// </remarks>
public interface IAmqpTarget
{
    /// <summary>
    /// Gets the extension capabilities the target supports or desires, or <see langword="null"/> for none.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? Capabilities { get; }
}
