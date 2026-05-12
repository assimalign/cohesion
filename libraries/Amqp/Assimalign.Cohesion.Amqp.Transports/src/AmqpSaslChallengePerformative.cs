using System;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents the AMQP SASL challenge performative.
/// </summary>
public sealed class AmqpSaslChallengePerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the challenge payload.
    /// </summary>
    public ReadOnlyMemory<byte> Challenge { get; set; }
}
